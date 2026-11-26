using System.Net;
using System.Text.Json.Nodes;

namespace Streaik.Gateway.Tests;

public sealed class RealtimeProviderTests
{
    [Theory]
    [InlineData("google/gemini-2.5-flash-native-audio-preview", "X-Google-Key", "generativelanguage.googleapis.com", "/v1alpha/auth_tokens", "x-goog-api-key", "fixture", "name", "Constrained")]
    [InlineData("xai/grok-voice-latest", "X-Xai-Key", "api.x.ai", "/v1/realtime/client_secrets", "Authorization", "Bearer fixture", "value", "grok-voice-latest")]
    [InlineData("assemblyai/universal-streaming-english", "X-AssemblyAI-Key", "streaming.assemblyai.com", "/v3/token?expires_in_seconds=60", "Authorization", "fixture", "token", "speech_model=universal-streaming-english")]
    [InlineData("assemblyai/assemblyai-stt-3-7-realtime", "X-AssemblyAI-Key", "streaming.assemblyai.com", "/v3/token?expires_in_seconds=60", "Authorization", "fixture", "token", "speech_model=assemblyai-stt-3-7-realtime")]
    public async Task Temporary_tokens_use_provider_scoped_auth_and_return_no_store(string model, string header, string hostName,
        string path, string authHeader, string expectedAuth, string tokenField, string urlSuffix)
    {
        await using var host = await FixtureHost.StartAsync(_ => FixtureHost.Json(new JsonObject { [tokenField] = "temporary", ["expires_at"] = 1700000000 }.ToJsonString()),
            liveProviders: true);
        using var response = await host.PostAsync("/api/realtime/token/" + model, "{}", (header, "fixture"), ("X-OpenAI-Key", "not-forwarded"));
        var body = JsonNode.Parse(await GatewayTests.Success(response))!;
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("temporary", (string?)body["token"]);
        Assert.EndsWith(urlSuffix, (string)body["url"]!);
        var upstream = Assert.Single(host.Requests);
        Assert.Equal(hostName, upstream.Host);
        Assert.Equal(path, upstream.Path);
        Assert.Equal(expectedAuth, upstream.Headers[authHeader]);
        Assert.DoesNotContain("X-OpenAI-Key", upstream.Headers.Keys, StringComparer.OrdinalIgnoreCase);
        if (hostName.StartsWith("generativelanguage", StringComparison.Ordinal))
            Assert.Equal("models/" + model.Split('/')[1], (string?)upstream.Body!["bidiGenerateContentSetup"]!["model"]);
    }

    [Fact]
    public async Task Voice_token_providers_preserve_native_options_without_a_shared_session_translation()
    {
        await using var host = await FixtureHost.StartAsync(request => request.Host == "api.x.ai"
            ? FixtureHost.Json("""{"value":"temporary-xai"}""")
            : FixtureHost.Json("""{"name":"auth_tokens/temporary-google"}"""), liveProviders: true);
        using var xai = await host.PostAsync("/api/realtime/token/xai/grok-voice-latest", """
            {"sessionConfig":{"providerOptions":{"xai":{"expires_after":{"anchor":"created_at","seconds":60}}}}}
            """, ("X-Xai-Key", "fixture"));
        using var google = await host.PostAsync("/api/realtime/token/google/gemini-2.5-flash-native-audio-preview", """
            {"sessionConfig":{"providerOptions":{"google":{"bidiGenerateContentSetup":{"model":"models/gemini-2.5-flash-native-audio-preview",
              "generationConfig":{"responseModalities":["AUDIO"],"speechConfig":{"voiceConfig":{"prebuiltVoiceConfig":{"voiceName":"Puck"}}}},
              "inputAudioTranscription":{},"realtimeInputConfig":{"automaticActivityDetection":{"disabled":true}}}}}}}
            """, ("X-Google-Key", "fixture"));
        Assert.True(xai.Headers.CacheControl?.NoStore);
        Assert.True(google.Headers.CacheControl?.NoStore);
        Assert.Contains("temporary-xai", await GatewayTests.Success(xai));
        Assert.Contains("auth_tokens/temporary-google", await GatewayTests.Success(google));
        var xaiRequest = Assert.Single(host.Requests, request => request.Host == "api.x.ai").Body!;
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"expires_after":{"anchor":"created_at","seconds":60}}"""), xaiRequest));
        var googleRequest = Assert.Single(host.Requests, request => request.Host == "generativelanguage.googleapis.com").Body!;
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""
            {"model":"models/gemini-2.5-flash-native-audio-preview",
             "generationConfig":{"responseModalities":["AUDIO"],"speechConfig":{"voiceConfig":{"prebuiltVoiceConfig":{"voiceName":"Puck"}}}},
             "inputAudioTranscription":{},"realtimeInputConfig":{"automaticActivityDetection":{"disabled":true}}}
            """), googleRequest["bidiGenerateContentSetup"]));
    }

    [Fact]
    public async Task Deepgram_browser_lease_uses_the_scoped_grant_and_never_returns_the_api_key()
    {
        var token = "temporary";
        await using var host = await FixtureHost.StartAsync(_ => FixtureHost.Json(new JsonObject { ["access_token"] = token, ["expires_in"] = 30 }.ToJsonString()), media: true);
        const string endpoint = "/api/realtime/token/deepgram/nova-3";
        using var response = await host.PostAsync(endpoint, "{}", ("X-Deepgram-Key", "fixture-deepgram"), ("X-OpenAI-Key", "not-forwarded"));
        var body = JsonNode.Parse(await GatewayTests.Success(response))!;
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("temporary", (string?)body["token"]);
        Assert.Equal("wss://api.deepgram.com/v1/listen?model=nova-3", (string?)body["url"]);
        Assert.True((long?)body["expiresAt"] > DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var upstream = Assert.Single(host.Requests);
        Assert.Equal("/v1/auth/grant", upstream.Path);
        Assert.Equal("Token fixture-deepgram", upstream.Headers["Authorization"]);
        Assert.DoesNotContain("X-OpenAI-Key", upstream.Headers.Keys, StringComparer.OrdinalIgnoreCase);

        token = "fixture-deepgram";
        using var echoedCredential = await host.PostAsync(endpoint, "{}", ("X-Deepgram-Key", token));
        Assert.False(echoedCredential.IsSuccessStatusCode);
        Assert.DoesNotContain(token, await echoedCredential.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("google/gemini-2.5-flash", "X-Google-Key", "/v1beta/openai/chat/completions")]
    [InlineData("xai/grok-4", "X-Xai-Key", "/v1/chat/completions")]
    public async Task Compatible_chat_providers_keep_the_gateway_wire_shape(string model, string header, string upstreamPath)
    {
        await using var host = await FixtureHost.StartAsync(_ => FixtureHost.Json("""
            {"id":"chat-1","object":"chat.completion","created":1,"model":"native","choices":[{"index":0,"message":{"role":"assistant","content":"hello"},"finish_reason":"stop"}]}
            """), liveProviders: true);
        const string route = "/v1/chat/completions";
        using var response = await host.PostAsync(route, GatewayTests.Request(route, model, false).ToJsonString(), (header, "fixture"));
        Assert.Equal(model, (string?)JsonNode.Parse(await GatewayTests.Success(response))!["model"]);
        var upstream = Assert.Single(host.Requests);
        Assert.Equal(upstreamPath, upstream.Path);
        Assert.Equal(model.Split('/')[1], (string?)upstream.Body!["model"]);
        Assert.Equal("Bearer fixture", upstream.Headers["Authorization"]);
    }

    [Fact]
    public async Task OpenAI_transcription_secret_routes_by_nested_model_without_adding_voice_model()
    {
        await using var host = await FixtureHost.StartAsync(_ => FixtureHost.Json("""
            {"value":"temporary","expires_at":1999999999,"session":{"type":"transcription","audio":{"input":{"transcription":{"model":"gpt-realtime-transcribe"}}}}}
            """));
        const string path = "/v1/realtime/client_secrets";
        using var response = await host.PostAsync(path, """
            {"session":{"type":"transcription","audio":{"input":{"transcription":{"model":"openai/gpt-realtime-transcribe","language":"en"},"turn_detection":{"type":"server_vad"}}}}}
            """, ("X-OpenAI-Key", "fixture"), ("X-Gladia-Key", "not-forwarded"));
        var body = JsonNode.Parse(await GatewayTests.Success(response))!;
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("temporary", (string?)body["value"]);
        Assert.Equal(1999999999, (long?)body["expires_at"]);
        Assert.Null(body["session"]!["model"]);
        Assert.Equal("openai/gpt-realtime-transcribe", (string?)body["session"]!["audio"]!["input"]!["transcription"]!["model"]);
        var upstream = Assert.Single(host.Requests);
        Assert.Equal(path, upstream.Path);
        Assert.Equal("Bearer fixture", upstream.Headers["Authorization"]);
        Assert.DoesNotContain("X-Gladia-Key", upstream.Headers.Keys, StringComparer.OrdinalIgnoreCase);
        var session = upstream.Body!["session"]!;
        Assert.Equal("transcription", (string?)session["type"]);
        Assert.Null(session["model"]);
        Assert.Equal("gpt-realtime-transcribe", (string?)session["audio"]!["input"]!["transcription"]!["model"]);
        Assert.Equal("en", (string?)session["audio"]!["input"]!["transcription"]!["language"]);

        using var mismatch = await host.PostAsync(path, """
            {"model":"openai/other","session":{"type":"transcription","audio":{"input":{"transcription":{"model":"openai/gpt-realtime-transcribe"}}}}}
            """, ("X-OpenAI-Key", "fixture"));
        Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode);
        Assert.Single(host.Requests);
    }

    [Fact]
    public async Task OpenAI_live_transcribe_secret_preserves_PCM_languages_and_manual_commit()
    {
        await using var host = await FixtureHost.StartAsync(_ => FixtureHost.Json("""{"value":"temporary","expires_at":1999999999}"""));
        using var response = await host.PostAsync("/v1/realtime/client_secrets", """
            {"session":{"type":"transcription","audio":{"input":{"format":{"type":"audio/pcm","rate":24000},"transcription":{"model":"openai/gpt-live-transcribe","languages":["nl"]},"turn_detection":null}}}}
            """, ("X-OpenAI-Key", "fixture"));
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("temporary", (string?)JsonNode.Parse(await GatewayTests.Success(response))!["value"]);
        var session = Assert.IsType<JsonObject>(Assert.Single(host.Requests).Body!["session"]);
        Assert.False(session.ContainsKey("model"));
        var audio = Assert.IsType<JsonObject>(session["audio"]!["input"]);
        Assert.True(audio.ContainsKey("turn_detection"));
        Assert.Null(audio["turn_detection"]);
        Assert.Equal(24000, (int?)audio["format"]!["rate"]);
        Assert.Equal("gpt-live-transcribe", (string?)audio["transcription"]!["model"]);
        Assert.Equal("nl", (string?)audio["transcription"]!["languages"]![0]);
    }

    [Fact]
    public async Task Gladia_live_session_uses_16khz_mono_audio_and_returns_only_the_ephemeral_session()
    {
        await using var host = await FixtureHost.StartAsync(_ => FixtureHost.Json("""
            {"id":"fixture-session","url":"wss://api.gladia.io/v2/live?token=temporary"}
            """), gladia: true);
        using var response = await host.PostAsync("/api/realtime/token/gladia/solaria-1", """
            {"sessionConfig":{"providerOptions":{"gladia":{"language_config":{"languages":["en"]}}}}}
            """, ("X-Gladia-Key", "fixture"), ("X-OpenAI-Key", "not-forwarded"));
        var body = JsonNode.Parse(await GatewayTests.Success(response))!;
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("fixture-session", (string?)body["token"]);
        Assert.Equal("wss://api.gladia.io/v2/live?token=temporary", (string?)body["url"]);
        var upstream = Assert.Single(host.Requests);
        Assert.Equal("/v2/live", upstream.Path);
        Assert.Equal("fixture", upstream.Headers["x-gladia-key"]);
        Assert.DoesNotContain("X-OpenAI-Key", upstream.Headers.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("wav/pcm", (string?)upstream.Body!["encoding"]);
        Assert.Equal(16000, (int?)upstream.Body!["sample_rate"]);
        Assert.Equal(16, (int?)upstream.Body["bit_depth"]);
        Assert.Equal(1, (int?)upstream.Body["channels"]);
    }

    [Fact]
    public async Task OpenAI_native_live_and_existing_client_secret_use_distinct_contracts()
    {
        await using var host = await FixtureHost.StartAsync(request => request.Path switch
        {
            "/v1/live/sessions" => FixtureHost.Json("""{"session":{"id":"live_1"},"transport":{"type":"webrtc","sdp":"answer"}}""", HttpStatusCode.Created),
            "/v1/realtime/client_secrets" => FixtureHost.Json("""{"value":"temporary","expires_at":1700000000,"session":{"model":"gpt-realtime"}}"""),
            "/v1/realtime/calls" => SdpAnswer(),
            _ => throw new InvalidOperationException(request.Path)
        });
        using var live = await host.PostAsync("/v1/live/sessions", """
            {"session":{"model":"openai/gpt-live-1","instructions":"be concise","input":[{"type":"message","role":"user","content":[{"type":"input_text","text":"earlier"}]}],"delegation":{"type":"responses","responses":{"model":"gpt-5-mini","parallel_tool_calls":false}}},"transport":{"type":"webrtc","sdp":"offer"}}
            """, ("X-OpenAI-Key", "fixture"));
        Assert.Equal(HttpStatusCode.Created, live.StatusCode);
        Assert.True(live.Headers.CacheControl?.NoStore);
        Assert.Equal("answer", (string?)JsonNode.Parse(await GatewayTests.Success(live))!["transport"]!["sdp"]);
        using var secret = await host.PostAsync("/v1/realtime/client_secrets", """{"session":{"model":"openai/gpt-realtime"}}""", ("X-OpenAI-Key", "fixture"));
        Assert.True(secret.Headers.CacheControl?.NoStore);
        Assert.Equal("openai/gpt-realtime", (string?)JsonNode.Parse(await GatewayTests.Success(secret))!["session"]!["model"]);
        Assert.Equal("gpt-live-1", (string?)host.Requests.First().Body!["session"]!["model"]);
        Assert.False((bool?)host.Requests.First().Body!["session"]!["store"]);
        Assert.Equal("earlier", (string?)host.Requests.First().Body!["session"]!["input"]![0]!["content"]![0]!["text"]);
        Assert.Equal("gpt-5-mini", (string?)host.Requests.First().Body!["session"]!["delegation"]!["responses"]!["model"]);
        Assert.Equal("gpt-realtime", (string?)host.Requests.Last().Body!["session"]!["model"]);
        using var webRtc = await host.PostAsync("/api/realtime/session/openai/gpt-realtime", """{"sdp":"offer"}""", ("X-OpenAI-Key", "fixture"));
        Assert.True(webRtc.Headers.CacheControl?.NoStore);
        var result = JsonNode.Parse(await GatewayTests.Success(webRtc))!;
        Assert.Equal("call_1", (string?)result["sessionId"]);
        Assert.Equal("answer", (string?)result["sdp"]);

        static HttpResponseMessage SdpAnswer()
        {
            var response = new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("answer") };
            response.Headers.Location = new Uri("/v1/realtime/calls/call_1", UriKind.Relative);
            return response;
        }
    }

    [Theory]
    [InlineData("/api/realtime/token/openai/gpt-realtime", "token", "expiresAt", """
        {"sessionConfig":{"providerOptions":{"openai":{"audio":{"input":{"turn_detection":null}}}}}}
        """)]
    [InlineData("/v1/realtime/client_secrets", "value", "expires_at", """
        {"expires_after":{"anchor":"created_at","seconds":120},"session":{"type":"realtime","model":"openai/gpt-realtime","audio":{"input":{"turn_detection":null}}}}
        """)]
    public async Task OpenAI_conversation_tokens_preserve_settings_and_scope_auth(string endpoint, string tokenField, string expiryField, string body)
    {
        await using var host = await FixtureHost.StartAsync(_ => FixtureHost.Json("""{"value":"temporary","expires_at":1999999999}"""));
        using var response = await host.PostAsync(endpoint, body, ("X-OpenAI-Key", "fixture"), ("X-Gladia-Key", "unrelated"));
        var result = JsonNode.Parse(await GatewayTests.Success(response))!;
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("temporary", (string?)result[tokenField]);
        Assert.Equal(1999999999L, (long?)result[expiryField]);
        var upstream = Assert.Single(host.Requests);
        Assert.Equal("/v1/realtime/client_secrets", upstream.Path);
        Assert.Equal("Bearer fixture", upstream.Headers["Authorization"]);
        Assert.DoesNotContain("X-Gladia-Key", upstream.Headers.Keys, StringComparer.OrdinalIgnoreCase);
        var session = upstream.Body!["session"]!;
        Assert.Equal("gpt-realtime", (string?)session["model"]);
        Assert.Equal("realtime", (string?)session["type"]);
        var audio = Assert.IsType<JsonObject>(session["audio"]!["input"]);
        Assert.True(audio.ContainsKey("turn_detection"));
        Assert.Null(audio["turn_detection"]);
        if (tokenField == "value") Assert.Equal(120, (int?)upstream.Body!["expires_after"]!["seconds"]);
    }
}
