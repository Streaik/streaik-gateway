using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace Streaik.Gateway.Tests;

public sealed class AkashArceeAsyncTests
{
    private static readonly (string, string) Akash = ("x-akashml-key", "akash-key");
    private static readonly (string, string) Arcee = ("x-arceeai-key", "arcee-key");
    private static readonly (string, string) Async = ("x-async-key", "async-key");
    private const string ArceeModels = """{"data":[{"id":"vendor/chat","name":"Chat","pricing":{"prompt":"0.000002","completion":0.000003}}]}""";
    private const string Chat = """{"id":"chat-1","object":"chat.completion","model":"vendor/chat","created":123,"choices":[{"index":0,"message":{"role":"assistant","content":"hello","native_message":{"n":1}},"finish_reason":"stop"}],"usage":{"prompt_tokens":2,"completion_tokens":1,"total_tokens":3},"native_response":{"shape":[1,2]}}""";

    [Fact]
    public async Task Model_discovery_uses_provider_credentials_and_preserves_native_identifiers_and_rates()
    {
        await using var host = await FixtureHost.StartAsync(request => request.Host switch
        {
            "api.akashml.com" => FixtureHost.Json("""{"data":[{"id":"vendor/chat","name":"Chat","max_output_length":512,"context_length":4096,"created":17,"pricing":{"input":"0.000001","output":0.00002}}]}"""),
            "api.arcee.ai" => FixtureHost.Json(ArceeModels),
            "api.async.com" when (string?)request.Body?["model_id"] != "async_pro_v1.0" => FixtureHost.Json("""{"voices":[]}"""),
            "api.async.com" when request.Body?["after"] is null => FixtureHost.Json("""{"voices":[{"voice_id":"voice-1","name":"Ada"}],"next_cursor":"second"}"""),
            "api.async.com" => FixtureHost.Json("""{"voices":[{"voice_id":"voice-1"},{"id":"voice-2"}],"next_cursor":"second"}"""),
            _ => throw new InvalidOperationException(request.Host)
        }, akashArceeAsync: true);

        using var withoutKeys = await host.GetAsync("/v1/models");
        await GatewayTests.Success(withoutKeys);
        Assert.Empty(host.Requests);
        using var response = await host.GetAsync("/v1/models", Akash, Arcee, Async);
        var listed = (await Json(response))["data"]!.AsArray().ToDictionary(item => (string)item!["id"]!);
        Assert.Equal(512, (int?)listed["akashml/vendor/chat"]!["max_tokens"]);
        Assert.Equal("image", (string?)listed["akashml/black-forest-labs/FLUX.1-schnell"]!["type"]);
        Assert.NotNull(listed["arceeai/vendor/chat"]!["pricing"]!["input"]);
        Assert.Equal("transcription", (string?)listed["async/async_asr_v1.0"]!["type"]);
        Assert.Equal("Async Pro v1.0 / Ada", (string?)listed["async/async_pro_v1.0/voice-1"]!["name"]);
        Assert.Contains("async/async_pro_v1.0/voice-2", listed.Keys);
        var akash = Assert.Single(host.Requests, call => call.Host == "api.akashml.com");
        Assert.Equal("/v1/models", akash.Path);
        Assert.Equal("Bearer akash-key", akash.Headers["Authorization"]);
        var arcee = Assert.Single(host.Requests, call => call.Host == "api.arcee.ai");
        Assert.Equal("/api/v1/models", arcee.Path);
        Assert.Equal("Bearer arcee-key", arcee.Headers["Authorization"]);
        Assert.All(host.Requests.Where(call => call.Path == "/voices"), AssertAsyncAuth);
    }

    [Theory]
    [InlineData("akashml", false)]
    [InlineData("akashml", true)]
    [InlineData("arceeai", false)]
    [InlineData("arceeai", true)]
    public async Task Chat_and_chat_stream_keep_nested_provider_metadata(string provider, bool stream)
    {
        await using var host = await FixtureHost.StartAsync(call => call.Path.EndsWith("/models", StringComparison.Ordinal)
            ? FixtureHost.Json(ArceeModels) : call.Stream ? FixtureHost.Sse(
                """{"id":"chat-1","object":"chat.completion.chunk","model":"vendor/chat","choices":[{"index":0,"delta":{"content":"hello","native_delta":{"flag":true}},"finish_reason":"stop"}]}""",
                """{"id":"chat-1","object":"chat.completion.chunk","model":"vendor/chat","choices":[],"usage":{"prompt_tokens":2,"completion_tokens":1,"total_tokens":3},"native_response":{"shape":[1,2]}}""",
                "[DONE]") : FixtureHost.Json(Chat), akashArceeAsync: true);
        using var response = await host.PostAsync("/v1/chat/completions", $$"""
            {"model":"{{provider}}/vendor/chat","messages":[{"role":"user","content":"question"}],"store":false,"stream":{{stream.ToString().ToLowerInvariant()}},"native_option":{"shape":[3,4]} }
            """, provider == "akashml" ? Akash : Arcee);
        var payload = await GatewayTests.Success(response);
        Assert.Contains("native_response", payload);
        Assert.Contains(stream ? "native_delta" : "native_message", payload);
        Assert.Contains($"{provider}/vendor/chat", payload);
        if (stream) Assert.Contains("[DONE]", payload);
        if (provider == "arceeai") Assert.Contains("estimated", payload);
        var chat = Assert.Single(host.Requests, request => request.Path.EndsWith("/chat/completions", StringComparison.Ordinal));
        Assert.Equal(provider == "akashml" ? "/v1/chat/completions" : "/api/v1/chat/completions", chat.Path);
        Assert.Equal("vendor/chat", (string?)chat.Body!["model"]);
        Assert.Equal(4, (int?)chat.Body["native_option"]!["shape"]![1]);
        Assert.False(chat.Body.AsObject().ContainsKey("store"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Akash_Messages_use_native_path_headers_and_extensions(bool stream)
    {
        const string message = """{"type":"message","id":"msg-1","model":"vendor/chat","role":"assistant","content":[{"type":"text","text":"hello"}],"stop_reason":"end_turn","usage":{"input_tokens":2,"output_tokens":1},"native_response":{"flag":true}}""";
        await using var host = await FixtureHost.StartAsync(call => call.Stream ? FixtureHost.Sse(
            """{"type":"message_stop","native_response":{"flag":true}}""") : FixtureHost.Json(message), akashArceeAsync: true);
        using var response = await host.PostAsync("/v1/messages", $$"""
            {"model":"akashml/vendor/chat","messages":[{"role":"user","content":"question"}],"max_tokens":32,"stream":{{stream.ToString().ToLowerInvariant()}},"native_option":{"x":[1]} }
            """, Akash, ("anthropic-version", "2023-06-01"), ("anthropic-beta", "fixture-beta"));
        Assert.Contains("native_response", await GatewayTests.Success(response));
        var call = Assert.Single(host.Requests);
        Assert.Equal("/anthropic/v1/messages", call.Path);
        Assert.Equal("2023-06-01", call.Headers["anthropic-version"]);
        Assert.Equal("fixture-beta", call.Headers["anthropic-beta"]);
        Assert.Equal(1, (int?)call.Body!["native_option"]!["x"]![0]);
    }

    [Fact]
    public async Task Translated_Akash_Responses_use_chat_without_requiring_native_Responses()
    {
        await using var host = await FixtureHost.StartAsync(_ => FixtureHost.Json(Chat), akashArceeAsync: true);
        using var response = await host.PostAsync("/v1/responses", """{"model":"akashml/vendor/chat","input":"question"}""", Akash);
        Assert.Contains("hello", await GatewayTests.Success(response));
        Assert.Equal("/v1/chat/completions", Assert.Single(host.Requests).Path);
    }

    [Fact]
    public async Task Akash_images_route_through_media_adapters_and_keep_extensions()
    {
        const string model = "akashml/black-forest-labs/FLUX.1-schnell";
        await using var host = await FixtureHost.StartAsync(call => call.Path.EndsWith("/edits", StringComparison.Ordinal)
            ? FixtureHost.Json("""{"created":17,"output_format":"png","data":[{"b64_json":"AQID"}]}""")
            : FixtureHost.Json("""{"created":17,"output_format":"png","data":[{"b64_json":"AQID"},{"b64_json":"BAUG"}],"native_image":{"seed":4}}"""),
            akashArceeAsync: true);
        using var generated = await host.PostAsync("/api/images", $$"""
            {"model":"{{model}}","prompt":"draw","providerOptions":{"akashml":{"native_config":{"steps":3} } } }
            """, Akash);
        var result = await Json(generated);
        Assert.Equal(2, result["images"]!.AsArray().Count);
        Assert.Equal(4, (int?)result["providerMetadata"]!["akashml"]!["native_image"]!["seed"]);
        var generate = Assert.Single(host.Requests);
        Assert.Equal("/v1/images/generations", generate.Path);
        Assert.Equal(3, (int?)generate.Body!["native_config"]!["steps"]);
        using var streamedEdit = await host.PostAsync("/v1/images/edits", $$"""
            {"model":"{{model}}","prompt":"edit","images":[{"image_url":"data:image/png;base64,AQID"}],"stream":true,"native_config":{"steps":4} }
            """, Akash);
        var edit = await GatewayTests.Success(streamedEdit);
        Assert.Contains("image_edit.completed", edit);
        Assert.Contains("AQID", edit);
        var editCall = Assert.Single(host.Requests, call => call.Path == "/v1/images/edits");
        Assert.Contains("image_url", editCall.RawBody);
        Assert.Contains("{\"steps\":4}", editCall.RawBody);
    }

    [Theory]
    [InlineData("https://cdn.akashml.com/image.png", true)]
    [InlineData("https://untrusted.example/image.png", false)]
    public async Task Akash_only_fetches_trusted_assets_without_forwarding_authentication(string url, bool trusted)
    {
        await using var host = await FixtureHost.StartAsync(request =>
        {
            if (request.Method == "POST") return FixtureHost.Json($$"""{"data":[{"url":"{{url}}"}]}""");
            var image = new ByteArrayContent([1, 2, 3]);
            image.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            return new(HttpStatusCode.OK) { Content = image };
        }, akashArceeAsync: true);
        using var response = await host.PostAsync("/v1/images/generations", """
            {"model":"akashml/black-forest-labs/FLUX.1-schnell","prompt":"draw"}
            """, Akash);
        if (!trusted)
        {
            Assert.False(response.IsSuccessStatusCode);
            Assert.Single(host.Requests);
            return;
        }
        Assert.Equal("AQID", (string?)(await Json(response))["data"]![0]!["b64_json"]);
        var asset = Assert.Single(host.Requests, request => request.Method == "GET");
        Assert.Equal("cdn.akashml.com", asset.Host);
        Assert.False(asset.Headers.ContainsKey("Authorization"));
        Assert.False(asset.Headers.ContainsKey(Akash.Item1));
    }

    [Fact]
    public async Task Async_speech_and_stream_apply_voice_shortcut_and_raw_native_options()
    {
        await using var host = await FixtureHost.StartAsync(_ => Audio([1, 2, 3]), akashArceeAsync: true);
        using var batch = await host.PostAsync("/api/speech", """
            {"model":"async/async_pro_v1.0/voice-1","text":"hello","voice":"ignored","providerOptions":{"async":{"model_id":"ignored","voice":{"tune":{"x":2}},"output_format":{"sample_rate":16000},"experiment":{"values":[4]}}}}
            """, Async);
        var result = await Json(batch);
        Assert.Equal("voice-1", (string?)result["providerMetadata"]!["async"]!["voice"]);
        var speech = Assert.Single(host.Requests);
        AssertAsyncAuth(speech);
        Assert.Equal("/text_to_speech", speech.Path);
        Assert.Equal("async_pro_v1.0", (string?)speech.Body!["model_id"]);
        Assert.Equal("voice-1", (string?)speech.Body["voice"]!["id"]);
        Assert.Equal(2, (int?)speech.Body["voice"]!["tune"]!["x"]);
        Assert.Equal(16000, (int?)speech.Body["output_format"]!["sample_rate"]);
        Assert.Equal(4, (int?)speech.Body["experiment"]!["values"]![0]);

        using var streaming = await host.PostAsync("/v1/audio/speech", """
            {"model":"async/async_pro_v1.0","input":"hello","voice":"voice-2","response_format":"pcm","stream_format":"sse"}
            """, Async);
        var frames = await GatewayTests.Success(streaming);
        Assert.Contains("AQID", frames);
        Assert.Contains("speech.audio.done", frames);
        var stream = Assert.Single(host.Requests, call => call.Path == "/text_to_speech/streaming");
        Assert.Equal("raw", (string?)stream.Body!["output_format"]!["container"]);
        AssertAsyncAuth(stream);
    }

    [Fact]
    public async Task Async_transcription_preserves_raw_metadata_and_native_batch_stream_framing()
    {
        await using var host = await FixtureHost.StartAsync(_ => FixtureHost.Json("""
            {"text":"hello","language":"en","duration":1.25,"native":{"words":[{"score":0.9}]}}
            """), akashArceeAsync: true);
        using var response = await host.PostAsync("/api/transcriptions", """
            {"model":"async/async_asr_v1.0","audio":"AQID","mediaType":"audio/wav","providerOptions":{"async":{"language":"en","model_id":"ignored","extension":{"terms":["streaik"]}}}}
            """, Async);
        var result = await Json(response);
        Assert.Equal("hello", (string?)result["text"]);
        Assert.Equal(0.9m, (decimal?)result["providerMetadata"]!["async"]!["native"]!["words"]![0]!["score"]);
        var transcript = Assert.Single(host.Requests);
        AssertAsyncAuth(transcript);
        Assert.Equal("/speech_to_text", transcript.Path);
        Assert.Contains("async_asr_v1.0", transcript.RawBody);
        Assert.DoesNotContain("ignored", transcript.RawBody);
        Assert.Contains("{\"terms\":[\"streaik\"]}", transcript.RawBody);

        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent([1, 2, 3]);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(file, "file", "speech.wav");
        form.Add(new StringContent("async/async_asr_v1.0"), "model");
        form.Add(new StringContent("true"), "stream");
        using var streamed = await host.PostFormAsync("/v1/audio/transcriptions", form, Async);
        var frames = await GatewayTests.Success(streamed);
        Assert.Contains("transcript.text.delta", frames);
        Assert.Contains("transcript.text.done", frames);
    }

    [Fact]
    public async Task Async_quota_marker_is_an_error_not_audio()
    {
        await using var host = await FixtureHost.StartAsync(_ => Audio("--ERROR:QUOTA_EXCEEDED--"u8.ToArray()), akashArceeAsync: true);
        using var response = await host.PostAsync("/v1/audio/speech", """
            {"model":"async/async_flash_v1.5/voice-1","input":"hello","stream_format":"sse"}
            """, Async);
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
    }

    private static async Task<JsonNode> Json(HttpResponseMessage response) => JsonNode.Parse(await GatewayTests.Success(response))!;

    private static HttpResponseMessage Audio(byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return new(HttpStatusCode.OK) { Content = content };
    }

    private static void AssertAsyncAuth(FixtureHost.UpstreamRequest request)
    {
        Assert.Equal("async-key", request.Headers["x-api-key"]);
        Assert.Equal("v1", request.Headers["version"]);
        Assert.False(request.Headers.ContainsKey("Authorization"));
    }
}
