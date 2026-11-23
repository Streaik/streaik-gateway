using System.Net;
using System.Text.Json.Nodes;

namespace Streaik.Gateway.Tests;

public sealed class RealtimeProviderTests
{
    [Theory]
    [InlineData("google/gemini-2.5-flash-native-audio-preview", "X-Google-Key", "generativelanguage.googleapis.com", "/v1alpha/auth_tokens", "x-goog-api-key", "fixture", "name", "Constrained")]
    [InlineData("xai/grok-voice-latest", "X-Xai-Key", "api.x.ai", "/v1/realtime/client_secrets", "Authorization", "Bearer fixture", "value", "grok-voice-latest")]
    [InlineData("assemblyai/universal-streaming-english", "X-AssemblyAI-Key", "streaming.assemblyai.com", "/v3/token?expires_in_seconds=60", "Authorization", "fixture", "token", "speech_model=universal-streaming-english")]
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
            {"session":{"model":"openai/gpt-realtime","instructions":"be concise"},"transport":{"type":"webrtc","sdp":"offer"}}
            """, ("X-OpenAI-Key", "fixture"));
        Assert.Equal(HttpStatusCode.Created, live.StatusCode);
        Assert.True(live.Headers.CacheControl?.NoStore);
        Assert.Equal("answer", (string?)JsonNode.Parse(await GatewayTests.Success(live))!["transport"]!["sdp"]);
        using var secret = await host.PostAsync("/v1/realtime/client_secrets", """{"session":{"model":"openai/gpt-realtime"}}""", ("X-OpenAI-Key", "fixture"));
        Assert.True(secret.Headers.CacheControl?.NoStore);
        Assert.Equal("openai/gpt-realtime", (string?)JsonNode.Parse(await GatewayTests.Success(secret))!["session"]!["model"]);
        Assert.All(host.Requests, request => Assert.Equal("gpt-realtime", (string?)request.Body!["session"]!["model"]));
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
}
