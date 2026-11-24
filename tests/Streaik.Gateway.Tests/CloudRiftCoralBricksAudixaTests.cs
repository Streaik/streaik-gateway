using System.Net;
using System.Net.WebSockets;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Streaik.Providers;
using Streaik.Responses;

namespace Streaik.Gateway.Tests;

public sealed class CloudRiftCoralBricksAudixaTests
{
    private static readonly (string, string) CloudRift = ("x-cloudrift-key", "cloudrift-fixture");
    private static readonly (string, string) CoralBricks = ("x-coralbricks-key", "coralbricks-fixture");
    private static readonly (string, string) Audixa = ("x-audixa-key", "audixa-fixture");
    private const string Options = """{"flag":true,"config":[1,"2",null]}""";
    private const string ChatResult = """
        {"id":"chat-1","object":"chat.completion","model":"vendor/chat","created":1,"choices":[{"index":0,"message":{"role":"assistant","content":"hello"},"finish_reason":"stop"}],"native_reply":{"quality":[true,2]}}
        """;
    private const string ChatChunk = """
        {"id":"chat-1","object":"chat.completion.chunk","model":"vendor/chat","created":1,"choices":[{"index":0,"delta":{"role":"assistant","content":"hello"},"finish_reason":"stop"}],"native_reply":{"quality":[true,2]}}
        """;
    private const string ResponseJson = """
        {"id":"resp-1","object":"response","model":"vendor/chat","created_at":1,"status":"completed","output":[],"native_reply":{"quality":[true,2]}}
        """;

    [Fact]
    public async Task Model_discovery_keeps_nested_ids_and_paginated_voice_shortcuts()
    {
        await using var host = await FixtureHost.StartAsync(request => (request.Host, request.Path) switch
        {
            ("inference.cloudrift.ai", "/v1/models") => FixtureHost.Json("""{"data":[{"id":"vendor/chat","context_length":8192,"owned_by":"vendor"}]}"""),
            ("inference.coralbricks.ai", "/v1/models") => FixtureHost.Json("""
                [{"id":"vendor/chat","max_model_len":2048,"max_output_tokens":80,"tags":["agent"],"capabilities":{"tools":true},"supported_parameters":["reasoning"]}]
                """),
            ("api.audixa.ai", "/v3/voices?limit=100&offset=0") => FixtureHost.Json("""{"voices":[{"voice_id":"ava","model":"advanced","gender":"female"}],"length":"100"}"""),
            ("api.audixa.ai", "/v3/voices?limit=100&offset=100") => FixtureHost.Json("""{"items":[{"id":42,"model":"base"},{"voice_id":"ava","model":"advanced"}]}"""),
            _ => new(HttpStatusCode.NotFound)
        }, cloudRiftCoralBricksAudixa: true);
        using var response = await host.GetAsync("/v1/models", CloudRift, CoralBricks, Audixa);
        var models = (await Json(response))["data"]!.AsArray().ToDictionary(item => (string)item!["id"]!);
        Assert.Equal(8192, (int?)models["cloudrift/vendor/chat"]!["context_window"]);
        Assert.Equal("language", (string?)models["cloudrift/vendor/chat"]!["type"]);
        var coral = models["coralbricks/vendor/chat"]!;
        Assert.Equal(2048, (int?)coral["context_window"]);
        Assert.Equal(80, (int?)coral["max_tokens"]);
        Assert.Contains("agent", coral["tags"]!.AsArray().Select(tag => (string?)tag));
        Assert.Equal("speech", (string?)models["audixa/advanced/ava"]!["type"]);
        Assert.True(models.ContainsKey("audixa/base/42"));
        Assert.True(models.ContainsKey("audixa/base"));
        Assert.True(models.ContainsKey("audixa/advanced"));
        Assert.Equal("Bearer cloudrift-fixture", Assert.Single(host.Requests, item => item.Host == "inference.cloudrift.ai").Headers["Authorization"]);
        Assert.Equal("Bearer coralbricks-fixture", Assert.Single(host.Requests, item => item.Host == "inference.coralbricks.ai").Headers["Authorization"]);
        Assert.Equal(2, host.Requests.Count(item => item.Headers.TryGetValue("x-api-key", out var key) && key == Audixa.Item2));
    }

    [Theory]
    [InlineData("cloudrift", "/v1/chat/completions", false)]
    [InlineData("cloudrift", "/v1/responses", true)]
    [InlineData("cloudrift", "/v1/messages", false)]
    [InlineData("cloudrift", "/api/chat", true)]
    [InlineData("coralbricks", "/v1/chat/completions", true)]
    [InlineData("coralbricks", "/v1/messages", false)]
    [InlineData("coralbricks", "/api/chat", true)]
    public async Task Chat_routes_and_preserves_native_json_in_both_directions(string provider, string path, bool stream)
    {
        await using var host = await FixtureHost.StartAsync(request => request.Stream ? FixtureHost.Sse(ChatChunk, "[DONE]") : FixtureHost.Json(ChatResult),
            cloudRiftCoralBricksAudixa: true);
        var payload = JsonNode.Parse(path switch
        {
            "/v1/responses" => """{"input":"Hi"}""",
            "/v1/messages" => """{"messages":[{"role":"user","content":"Hi"}],"max_tokens":8}""",
            "/api/chat" => """{"messages":[{"id":"u","role":"user","parts":[{"type":"text","text":"Hi"}]}]}""",
            _ => """{"messages":[{"role":"user","content":"Hi"}]}"""
        })!.AsObject();
        payload["model"] = $"{provider}/vendor/chat";
        var native = new JsonObject { ["native_request"] = JsonNode.Parse(Options) };
        if (path == "/api/chat") payload["providerMetadata"] = new JsonObject { [provider] = native };
        else
        {
            payload["stream"] = stream;
            if (provider == "cloudrift" && path == "/v1/chat/completions") payload["native_request"] = JsonNode.Parse(Options);
            else payload[provider] = native;
        }
        using var response = await host.PostAsync(path, payload.ToJsonString(), provider == "cloudrift" ? CloudRift : CoralBricks);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        Assert.Contains("hello", body);
        Assert.Contains("native_reply", body);
        var sent = Assert.Single(host.Requests, request => request.Path == "/v1/chat/completions");
        Assert.Equal($"inference.{provider}.ai", sent.Host);
        Assert.Equal("vendor/chat", (string?)sent.Body!["model"]);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Options), sent.Body["native_request"]));
        Assert.Null(sent.Body[provider]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CoralBricks_responses_use_native_transport_but_the_gateway_stays_stateless(bool stream)
    {
        await using var host = await FixtureHost.StartAsync(request => request.Stream
            ? FixtureHost.Sse("""{"type":"response.completed","sequence_number":0,"response":RESPONSE}""".Replace("RESPONSE", ResponseJson))
            : FixtureHost.Json(ResponseJson), cloudRiftCoralBricksAudixa: true);
        using var rejected = await host.PostAsync("/v1/responses", """{"model":"coralbricks/vendor/chat","input":"Hi","background":true}""", CoralBricks);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Empty(host.Requests);
        var payload = JsonNode.Parse("""
            {"model":"coralbricks/vendor/chat","input":"Hi","coralbricks":{"native_request":{"x":[1,false]},"Store":true,"background":true,"previous_response_id":"old","conversation":"old","input":[{"type":"item_reference","id":"old"}],"model":"other","stream":false}}
            """)!.AsObject();
        payload["stream"] = stream;
        using var response = await host.PostAsync("/v1/responses", payload.ToJsonString(), CoralBricks);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        Assert.Contains("native_reply", body);
        Assert.Contains("coralbricks/vendor/chat", body);
        if (stream) Assert.Contains("response.completed", body);
        var sent = Assert.Single(host.Requests, request => request.Path == "/v1/responses");
        Assert.Equal("inference.coralbricks.ai", sent.Host);
        Assert.Equal("vendor/chat", (string?)sent.Body!["model"]);
        Assert.Equal("Hi", (string?)sent.Body["input"]);
        Assert.Equal(stream, (bool?)sent.Body["stream"]);
        Assert.False((bool?)sent.Body["store"]);
        Assert.Null(sent.Body["Store"]);
        Assert.Null(sent.Body["background"]);
        Assert.Null(sent.Body["previous_response_id"]);
        Assert.Null(sent.Body["conversation"]);
        Assert.False((bool?)sent.Body["native_request"]!["x"]![1]);
    }

    [Fact]
    public async Task CoralBricks_typed_provider_call_keeps_native_background_and_storage()
    {
        var services = new ServiceCollection().AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Providers:coralbricks:ApiKey"] = CoralBricks.Item2 }).Build()).AddCoralBricks();
        JsonNode? sent = null;
        services.AddHttpClient("coralbricks").ConfigurePrimaryHttpMessageHandler(() => new FixtureTransport(async (request, ct) =>
        {
            sent = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct));
            return FixtureHost.Json(ResponseJson);
        }));
        using var root = services.BuildServiceProvider();
        using var scope = root.CreateScope();
        var provider = scope.ServiceProvider.GetRequiredKeyedService<IResponsesProvider>("coralbricks");
        _ = await provider.CreateResponseAsync(new ResponseRequest { Model = "vendor/chat", Store = true, Background = true },
            new Dictionary<string, string>(), CancellationToken.None);
        Assert.True((bool?)sent!["store"]);
        Assert.True((bool?)sent["background"]);
    }

    [Fact]
    public async Task Audixa_polls_downloads_without_credentials_and_returns_native_metadata()
    {
        await using var host = await FixtureHost.StartAsync(request => request.Path switch
        {
            "/v3/tts" => FixtureHost.Json("""{"generation_id":"job 1","status":"IN_QUEUE"}"""),
            "/v3/tts?generation_id=job%201" => FixtureHost.Json("""
                {"status":"COMPLETED","audio_url":"https://cdn.audixa.ai/audio/test.mp3?signature=hidden","usage":{"count":4,"units":"chars"}}
                """),
            "/audio/test.mp3?signature=hidden" => new(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) },
            _ => new(HttpStatusCode.NotFound)
        }, cloudRiftCoralBricksAudixa: true);
        using var response = await host.PostAsync("/api/speech", """
            {"model":"audixa/advanced/ava","text":"Test","voice":"not-ava","language":"nl","outputFormat":"mp3","providerOptions":{"audixa":{"native_request":{"x":[1,"2",null]},"voice_id":"not-ava"}}}
            """, Audixa);
        var result = await Json(response);
        Assert.Equal("AQID", (string?)result["audio"]!["base64"]);
        Assert.Equal("audio/mpeg", (string?)result["audio"]!["mediaType"]);
        Assert.Equal(4, (int?)result["providerMetadata"]!["audixa"]!["usage"]!["count"]);
        Assert.Equal("job 1", (string?)result["providerMetadata"]!["audixa"]!["generation_id"]);
        Assert.Null(result["providerMetadata"]!["audixa"]!["audio_url"]);
        Assert.Collection(host.Requests, post =>
        {
            Assert.Equal(Audixa.Item2, post.Headers["x-api-key"]);
            Assert.Equal("advanced", (string?)post.Body!["model"]);
            Assert.Equal("ava", (string?)post.Body["voice_id"]);
            Assert.Equal("nl", (string?)post.Body["language_code"]);
            Assert.Equal("2", (string?)post.Body["native_request"]!["x"]![1]);
        }, poll => Assert.Equal(Audixa.Item2, poll.Headers["x-api-key"]), asset =>
        {
            Assert.Equal("cdn.audixa.ai", asset.Host);
            Assert.False(asset.Headers.ContainsKey("x-api-key"));
            Assert.False(asset.Headers.ContainsKey("Authorization"));
        });
    }

    [Fact]
    public async Task Audixa_native_pcm_stream_reassembles_fragments_and_keeps_completion_metadata()
    {
        Uri? loopback = null;
        var socketPayload = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = await FixtureHost.StartAsync(cloudRiftCoralBricksAudixa: true,
            configure: services => services.AddHttpClient("audixa").ConfigurePrimaryHttpMessageHandler(() => new LoopbackSocket(() => loopback!)),
            configureApp: app =>
            {
                app.UseWebSockets();
                app.MapGet("/v3/tts/stream", async (HttpContext context) =>
                {
                    if (context.Request.Query["api_key"] != Audixa.Item2) { context.Response.StatusCode = 401; return; }
                    using var socket = await context.WebSockets.AcceptWebSocketAsync();
                    var buffer = new byte[4096];
                    var read = await socket.ReceiveAsync(buffer.AsMemory(), context.RequestAborted);
                    socketPayload.SetResult(JsonNode.Parse(buffer.AsSpan(0, read.Count))!);
                    await socket.SendAsync("""{"type":"started"}"""u8.ToArray().AsMemory(), WebSocketMessageType.Text, true, context.RequestAborted);
                    await socket.SendAsync(new byte[] { 0, 0 }.AsMemory(), WebSocketMessageType.Binary, false, context.RequestAborted);
                    await socket.SendAsync(new byte[] { 0, 63 }.AsMemory(), WebSocketMessageType.Binary, true, context.RequestAborted);
                    await socket.SendAsync("""{"type":"completed","native_result":{"usage":6}}"""u8.ToArray().AsMemory(), WebSocketMessageType.Text, true, context.RequestAborted);
                    await socket.ReceiveAsync(buffer.AsMemory(), context.RequestAborted);
                });
            });
        loopback = host.Client.BaseAddress;
        using var response = await host.PostAsync("/api/speech/stream", """
            {"model":"audixa/base/ava","text":[{"type":"text","text":"Te"},{"type":"text","text":"st"}],"outputFormat":{"type":"pcm16","rate":24000},"includeRawChunks":true,"providerOptions":{"audixa":{"native_request":{"x":[1,false]}}}}
            """, Audixa);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        var frames = body.Split('\n').Where(line => line.StartsWith("data: ", StringComparison.Ordinal))
            .Select(line => JsonNode.Parse(line[6..])!).ToArray();
        var audio = Assert.Single(frames, frame => (string?)frame["type"] == "audio");
        Assert.Equal("AEA=", (string?)audio["audio"]); // Native float32 0.5 -> little-endian PCM16 16384.
        Assert.Contains(frames, frame => (string?)frame["type"] == "raw" && frame["rawValue"]?.ToJsonString() == "\"AAAAPw==\"");
        Assert.Equal(24000, (int?)audio["audioFormat"]!["rate"]);
        Assert.Equal(6, (int?)frames.Last()["providerMetadata"]!["audixa"]!["native_result"]!["usage"]);
        var payload = await socketPayload.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Test", (string?)payload["text"]);
        Assert.Equal("ava", (string?)payload["voice_id"]);
        Assert.Equal("wav", (string?)payload["audio_format"]);
        Assert.False((bool?)payload["native_request"]!["x"]![1]);
    }

    private static async Task<JsonNode> Json(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        return JsonNode.Parse(body)!;
    }

    private sealed class FixtureTransport(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }

    private sealed class LoopbackSocket(Func<Uri> address) : DelegatingHandler(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Contains(request.RequestUri!.Scheme, new[] { "wss", "https" });
            Assert.Equal("api.audixa.ai", request.RequestUri.Host);
            var target = new Uri(address(), request.RequestUri.PathAndQuery);
            request.RequestUri = new UriBuilder(target) { Scheme = "ws" }.Uri;
            return base.SendAsync(request, ct);
        }
    }
}
