using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Streaik.Gateway.Tests;

public sealed class CrusoeAtlasCloudDeepdubTests
{
    private static readonly (string, string) Crusoe = ("x-crusoe-key", "fixture-crusoe");
    private static readonly (string, string) Atlas = ("x-atlascloud-key", "fixture-atlas");
    private static readonly (string, string) Deepdub = ("x-deepdub-key", "fixture-deepdub");
    private const string AtlasModels = """
        {"data":[{"model":"vendor/chat","type":"text","displayName":"Sample","contextLength":8192,"maxCompletionTokens":512},
                 {"model":"vendor/draw","type":"image"},{"model":"vendor/move","type":"video"}]}
        """;
    private const string ChatResult = """
        {"id":"chat-1","object":"chat.completion","model":"vendor/chat","created":1,"choices":[{"index":0,"message":{"role":"assistant","content":"hello"},"finish_reason":"stop"}],"native_reply":{"quality":[true,2]}}
        """;
    private const string ChatChunk = """
        {"id":"chat-1","object":"chat.completion.chunk","model":"vendor/chat","created":1,"choices":[{"index":0,"delta":{"role":"assistant","content":"hello"},"finish_reason":"stop"}],"native_reply":{"quality":[true,2]}}
        """;

    [Fact]
    public async Task Model_discovery_keeps_native_ids_types_and_crusoe_pricing()
    {
        await using var host = await FixtureHost.StartAsync(request => (request.Host, request.Path) switch
        {
            ("api.inference.crusoecloud.com", "/v1/models") => FixtureHost.Json("""
                {"data":[{"id":"vendor/chat","name":"Crusoe Chat","context_length":16384,
                "pricing":{"prompt":"0.25","completion":1.5,"input_cache_reads":"0.03"}}]}
                """),
            ("console.atlascloud.ai", "/api/v1/models") => FixtureHost.Json(AtlasModels),
            _ => new(HttpStatusCode.NotFound)
        }, crusoeAtlasCloudDeepdub: true);
        using var response = await host.GetAsync("/v1/models", Crusoe, Atlas, Deepdub);
        var models = (await Json(response))["data"]!.AsArray().ToDictionary(item => (string)item!["id"]!);
        var crusoe = models["crusoe/vendor/chat"]!;
        Assert.Equal("language", (string?)crusoe["type"]);
        Assert.Equal(16384, (int?)crusoe["context_window"]);
        Assert.Equal(0.00000025m, (decimal?)crusoe["pricing"]!["input"]);
        Assert.Equal(0.0000015m, (decimal?)crusoe["pricing"]!["output"]);
        Assert.Equal(0.00000003m, (decimal?)crusoe["pricing"]!["input_cache_read"]);
        Assert.Equal("language", (string?)models["atlascloud/vendor/chat"]!["type"]);
        Assert.Equal(8192, (int?)models["atlascloud/vendor/chat"]!["context_window"]);
        Assert.Equal(512, (int?)models["atlascloud/vendor/chat"]!["max_tokens"]);
        Assert.Equal("image", (string?)models["atlascloud/vendor/draw"]!["type"]);
        Assert.Equal("video", (string?)models["atlascloud/vendor/move"]!["type"]);
        Assert.Equal("speech", (string?)models["deepdub/dd-etts-3.0"]!["type"]);
        Assert.Equal("speech", (string?)models["deepdub/dd-etts-4.0"]!["type"]);
        Assert.Equal($"Bearer {Crusoe.Item2}", Assert.Single(host.Requests, call => call.Host == "api.inference.crusoecloud.com").Headers["Authorization"]);
        var console = Assert.Single(host.Requests, call => call.Host == "console.atlascloud.ai");
        Assert.False(console.Headers.ContainsKey("Authorization"));
        Assert.True(console.Headers.ContainsKey("User-Agent"));
    }

    [Theory]
    [InlineData("crusoe", "/v1/chat/completions", false)]
    [InlineData("crusoe", "/v1/responses", true)]
    [InlineData("crusoe", "/v1/messages", false)]
    [InlineData("crusoe", "/api/chat", true)]
    [InlineData("atlascloud", "/v1/chat/completions", true)]
    [InlineData("atlascloud", "/v1/responses", false)]
    [InlineData("atlascloud", "/v1/messages", true)]
    [InlineData("atlascloud", "/api/chat", true)]
    public async Task Chat_adapters_preserve_raw_native_requests_and_replies(string provider, string path, bool stream)
    {
        await using var host = await FixtureHost.StartAsync(request => request.Host == "console.atlascloud.ai" ? FixtureHost.Json(AtlasModels)
            : request.Stream ? FixtureHost.Sse(ChatChunk, "[DONE]") : FixtureHost.Json(ChatResult), crusoeAtlasCloudDeepdub: true);
        var body = JsonNode.Parse(path switch
        {
            "/v1/responses" => """{"input":"Hi"}""",
            "/v1/messages" => """{"messages":[{"role":"user","content":"Hi"}],"max_tokens":8}""",
            "/api/chat" => """{"messages":[{"id":"user","role":"user","parts":[{"type":"text","text":"Hi"}]}]}""",
            _ => """{"messages":[{"role":"user","content":"Hi"}]}"""
        })!.AsObject();
        body["model"] = $"{provider}/vendor/chat";
        if (path != "/api/chat") body["stream"] = stream;
        var native = JsonNode.Parse("""{"native_request":{"nested":[true,7,null]},"model":"ignored","store":false}""")!;
        if (path == "/api/chat") body["providerMetadata"] = new JsonObject { [provider] = native };
        else if (path == "/v1/chat/completions")
        {
            body["native_request"] = native["native_request"]!.DeepClone();
            body["tools"] = new JsonArray();
        }
        else body[provider] = native;
        var key = provider == "crusoe" ? Crusoe : Atlas;
        using var response = await host.PostAsync(path, body.ToJsonString(), key);
        Assert.Contains("native_reply", await GatewayTests.Success(response));
        var request = Assert.Single(host.Requests, request => request.Path == "/v1/chat/completions");
        Assert.Equal(provider == "crusoe" ? "api.inference.crusoecloud.com" : "api.atlascloud.ai", request.Host);
        Assert.Equal($"Bearer {key.Item2}", request.Headers["Authorization"]);
        Assert.Equal("vendor/chat", (string?)request.Body!["model"]);
        Assert.True((bool?)request.Body["native_request"]!["nested"]![0]);
        Assert.Null(request.Body[provider]);
        Assert.Null(request.Body["store"]);
        if (provider == "crusoe") Assert.Null(request.Body["tools"]);
    }

    [Fact]
    public async Task Atlas_image_polls_and_routes_image_models_without_leaking_signed_outputs()
    {
        await using var host = await FixtureHost.StartAsync(request => (request.Host, request.Path) switch
        {
            ("console.atlascloud.ai", "/api/v1/models") => FixtureHost.Json(AtlasModels),
            ("api.atlascloud.ai", "/api/v1/model/generateImage") => FixtureHost.Json("""{"data":{"id":"image-job","status":"processing","quota":{"limits":[5,7]}}}"""),
            ("api.atlascloud.ai", "/api/v1/model/prediction/image-job") => FixtureHost.Json("""
                {"data":{"id":"image-job","status":"completed","outputs":["https://cdn.atlascloud.ai/image.webp?sig=test-secret"],"debug":{"steps":[10]}}}
                """),
            ("cdn.atlascloud.ai", "/image.webp?sig=test-secret") => Asset(request, "image/webp"),
            _ => new(HttpStatusCode.NotFound)
        }, crusoeAtlasCloudDeepdub: true);
        using var images = await host.PostAsync("/api/images", """
            {"model":"atlascloud/vendor/draw","prompt":"draw this","aspectRatio":"16:9",
            "providerOptions":{"atlascloud":{"prompt":"ignore","MODEL":"ignore","enable_base64_output":false,"controls":{"relight":[1,true]}}}}
            """, Atlas);
        var image = await Json(images);
        Assert.Equal("data:image/webp;base64,AQID", (string?)image["images"]![0]);
        var metadata = image["providerMetadata"]!["atlascloud"]!;
        Assert.Equal(7, (int?)metadata["submit"]!["data"]!["quota"]!["limits"]![1]);
        Assert.Equal(10, (int?)metadata["result"]!["data"]!["debug"]!["steps"]![0]);
        Assert.DoesNotContain("test-secret", image.ToJsonString());
        var submit = Assert.Single(host.Requests, request => request.Path.EndsWith("/generateImage", StringComparison.Ordinal));
        Assert.Equal("vendor/draw", (string?)submit.Body!["model"]);
        Assert.Equal("draw this", (string?)submit.Body["prompt"]);
        Assert.Equal("16:9", (string?)submit.Body["aspect_ratio"]);
        Assert.True((bool?)submit.Body["enable_base64_output"]);
        Assert.True((bool?)submit.Body["controls"]!["relight"]![1]);
        Assert.Equal($"Bearer {Atlas.Item2}", submit.Headers["Authorization"]);
        Assert.Equal($"Bearer {Atlas.Item2}", Assert.Single(host.Requests, request => request.Path.EndsWith("/image-job", StringComparison.Ordinal)).Headers["Authorization"]);

        using var chat = await host.PostAsync("/v1/responses", """{"model":"atlascloud/vendor/draw","input":"draw again"}""", Atlas);
        Assert.Contains("AQID", await GatewayTests.Success(chat));
        Assert.DoesNotContain(host.Requests, request => request.Path == "/v1/chat/completions");
    }

    [Fact]
    public async Task Atlas_video_uses_protected_operations_and_preserves_native_job_state()
    {
        var polls = 0;
        await using var host = await FixtureHost.StartAsync(request => (request.Host, request.Path) switch
        {
            ("api.atlascloud.ai", "/api/v1/model/generateVideo") => FixtureHost.Json("""{"data":{"id":"video-job","status":"queued","queue":[2]}}"""),
            ("api.atlascloud.ai", "/api/v1/model/prediction/video-job") => Interlocked.Increment(ref polls) switch
            {
                1 => FixtureHost.Json("""{"data":{"id":"video-job","status":"processing","progress":25}}"""),
                2 => FixtureHost.Json("""{"data":{"id":"video-job","status":"succeeded","outputs":["https://cdn.atlascloud.ai/clip.mp4?sig=test-secret"],"detail":{"fps":30}}}"""),
                _ => FixtureHost.Json("""{"data":{"id":"video-job","status":"failed","error":{"code":"job_error"}}}""")
            },
            ("cdn.atlascloud.ai", "/clip.mp4?sig=test-secret") => Asset(request, "video/mp4"),
            _ => new(HttpStatusCode.NotFound)
        }, configure: services => services.AddStreaikReferences(), crusoeAtlasCloudDeepdub: true);
        using var started = await host.PostAsync("/api/videos", """
            {"model":"atlascloud/vendor/move","duration":5,"fps":30,"image":{"type":"url","url":"https://example.org/reference.png"},
            "providerOptions":{"atlascloud":{"model":"ignore","duration":99,"config":{"flags":[true,null]}}}}
            """, Atlas);
        var start = await Json(started);
        var operation = (string)start["operation"]!;
        Assert.StartsWith("atlascloud/", operation);
        Assert.DoesNotContain("video-job", operation);
        Assert.Equal(2, (int?)start["providerMetadata"]!["atlascloud"]!["data"]!["queue"]![0]);
        Assert.Equal("fps", (string?)start["warnings"]![0]!["feature"]);
        var submit = Assert.Single(host.Requests);
        Assert.Equal("vendor/move", (string?)submit.Body!["model"]);
        Assert.Equal("https://example.org/reference.png", (string?)submit.Body["image"]);
        Assert.Null(submit.Body["prompt"]);
        Assert.Equal(5, (int?)submit.Body["duration"]);
        Assert.True((bool?)submit.Body["config"]!["flags"]![0]);
        using var pending = await host.GetAsync($"/api/videos/{operation}", Atlas);
        Assert.Equal("pending", (string?)(await Json(pending))["status"]);
        using var completed = await host.GetAsync($"/api/videos/{operation}", Atlas);
        var result = await Json(completed);
        Assert.Equal("completed", (string?)result["status"]);
        Assert.Equal("AQID", (string?)result["videos"]![0]!["data"]);
        Assert.Equal(30, (int?)result["providerMetadata"]!["atlascloud"]!["data"]!["detail"]!["fps"]);
        Assert.DoesNotContain("test-secret", result.ToJsonString());
        using var failed = await host.GetAsync($"/api/videos/{operation}", Atlas);
        Assert.Equal("job_error", (string?)(await Json(failed))["providerMetadata"]!["atlascloud"]!["data"]!["error"]!["code"]);
        Assert.All(host.Requests.Where(request => request.Host == "api.atlascloud.ai"),
            request => Assert.Equal($"Bearer {Atlas.Item2}", request.Headers["Authorization"]));
    }

    [Theory]
    [InlineData("https://cdn.atlascloud.ai.example.com/file.png")]
    [InlineData("http://cdn.atlascloud.ai/file.png")]
    public async Task Atlas_rejects_untrusted_asset_urls_without_fetching_them(string target)
    {
        await using var host = await FixtureHost.StartAsync(_ => FixtureHost.Json(new JsonObject
        {
            ["status"] = "completed", ["outputs"] = new JsonArray(JsonValue.Create(target))
        }.ToJsonString()), crusoeAtlasCloudDeepdub: true);
        using var response = await host.PostAsync("/v1/images/generations", """{"model":"atlascloud/vendor/draw","prompt":"draw"}""", Atlas);
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Single(host.Requests);
    }

    [Fact]
    public async Task Atlas_image_polling_stops_when_the_client_cancels()
    {
        var polled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = await FixtureHost.StartAsync(request =>
        {
            if (request.Path.EndsWith("prediction/image-job", StringComparison.Ordinal)) polled.TrySetResult();
            return FixtureHost.Json("""{"data":{"id":"image-job","status":"processing"}}""");
        }, crusoeAtlasCloudDeepdub: true);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/images/generations")
        {
            Content = new StringContent("""{"model":"atlascloud/vendor/draw","prompt":"draw"}""", Encoding.UTF8, "application/json")
        };
        request.Headers.Add(Atlas.Item1, Atlas.Item2);
        var call = host.Client.SendAsync(request, cts.Token);
        await polled.Task.WaitAsync(cts.Token);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        Assert.Equal(2, host.Requests.Count);
    }

    [Fact]
    public async Task Deepdub_buffered_audio_uses_explicit_voice_and_locale_and_preserves_provider_options()
    {
        await using var host = await FixtureHost.StartAsync(request => Audio(request, "audio/ogg"), crusoeAtlasCloudDeepdub: true);
        using var response = await host.PostAsync("/api/speech", """
            {"model":"deepdub/dd-etts-4.0","text":"Hello","voice":"chosen","language":"en-GB","outputFormat":"opus","speed":1.15,
            "providerOptions":{"deepdub":{"region":"eu","model":"ignore","targetText":"ignore","locale":"fr-FR","voicePromptId":"ignore",
            "format":"mp3","accent":{"presets":["x",true,null]}}}}
            """, Deepdub);
        var speech = await Json(response);
        Assert.Equal("AQID", (string?)speech["audio"]!["base64"]);
        Assert.Equal("audio/ogg", (string?)speech["audio"]!["mediaType"]);
        var responseHeaders = speech["providerMetadata"]!["deepdub"]!["headers"]!.AsObject();
        Assert.Equal("tts-fixture", (string?)Assert.Single(responseHeaders, header => header.Key.Equals("x-request-id", StringComparison.OrdinalIgnoreCase)).Value);
        Assert.DoesNotContain("Set-Cookie", speech.ToJsonString(), StringComparison.OrdinalIgnoreCase);
        var sent = Assert.Single(host.Requests);
        Assert.Equal("restapi.eu.deepdub.ai", sent.Host);
        Assert.Equal("dd-etts-4.0", (string?)sent.Body!["model"]);
        Assert.Equal("Hello", (string?)sent.Body["targetText"]);
        Assert.Equal("en-GB", (string?)sent.Body["locale"]);
        Assert.Equal("chosen", (string?)sent.Body["voicePromptId"]);
        Assert.Equal("opus", (string?)sent.Body["format"]);
        Assert.Equal(1.15, (double?)sent.Body["tempo"]);
        Assert.True((bool?)sent.Body["accent"]!["presets"]![1]);
        Assert.Null(sent.Body["region"]);
    }

    [Fact]
    public async Task Deepdub_streams_openai_and_vercel_with_raw_chunks_and_per_part_options()
    {
        await using var host = await FixtureHost.StartAsync(request => Audio(request, null), crusoeAtlasCloudDeepdub: true);
        using var native = await host.PostAsync("/v1/audio/speech", """
            {"model":"deepdub/dd-etts-4.0","input":"Hello","stream_format":"sse",
            "deepdub":{"locale":"en-US","voicePromptId":"voice","temperature":0.7}}
            """, Deepdub);
        var plain = await GatewayTests.Success(native);
        Assert.Contains("speech.audio.delta", plain);
        Assert.Contains("AQID", plain);
        Assert.Contains("speech.audio.done", plain);
        using var sdk = await host.PostAsync("/api/speech/stream", """
            {"model":"deepdub/dd-etts-3.0","text":[{"type":"text","text":"He","providerOptions":{"deepdub":{"temperature":0.7}}},{"type":"text","text":"llo"}],
            "outputFormat":{"type":"mulaw","rate":8000},"includeRawChunks":true,
            "providerOptions":{"deepdub":{"locale":"en-US","voicePromptId":"voice","temperature":0.7,"sampleRate":16000}}}
            """, Deepdub);
        var stream = await GatewayTests.Success(sdk);
        Assert.Contains("\"type\":\"raw\"", stream);
        Assert.Contains("AQID", stream);
        Assert.Contains("audio/basic", stream);
        Assert.Contains("tts-fixture", stream);
        Assert.Contains("\"type\":\"finish\"", stream);
        Assert.All(host.Requests, request =>
        {
            Assert.Equal("restapi.deepdub.ai", request.Host);
            Assert.Equal("Hello", (string?)request.Body!["targetText"]);
            Assert.Equal(0.7, (double?)request.Body["temperature"]);
        });
        Assert.Equal(8000, (int?)host.Requests.Last().Body!["sampleRate"]);
    }

    [Fact]
    public async Task Deepdub_reuses_chat_audio_adapter_and_rejects_mixed_voice_streams_before_authenticating()
    {
        await using var host = await FixtureHost.StartAsync(request => Audio(request, null), crusoeAtlasCloudDeepdub: true);
        using var chat = await host.PostAsync("/v1/chat/completions", """
            {"model":"deepdub/dd-etts-4.0","modalities":["audio"],"messages":[{"role":"user","content":"Hello"}],
            "audio":{"voice":{"id":"voice-id"},"format":"mp3","locale":"en-US"}}
            """, Deepdub);
        Assert.Contains("AQID", await GatewayTests.Success(chat));
        Assert.Equal("voice-id", (string?)Assert.Single(host.Requests).Body!["voicePromptId"]);
        using var bad = await host.PostAsync("/api/speech/stream", """
            {"model":"deepdub/dd-etts-4.0","language":"en-US","text":[{"text":"a","voice":"one"},{"text":"b","voice":"two"}]}
            """, Deepdub);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
        Assert.Single(host.Requests);
    }

    private static HttpResponseMessage Asset(FixtureHost.UpstreamRequest request, string mime)
    {
        Assert.False(request.Headers.ContainsKey("Authorization"));
        Assert.False(request.Headers.ContainsKey("x-api-key"));
        return Binary(mime);
    }

    private static HttpResponseMessage Audio(FixtureHost.UpstreamRequest request, string? mime)
    {
        Assert.Equal("/api/v1/tts", request.Path);
        Assert.Equal(Deepdub.Item2, request.Headers["x-api-key"]);
        Assert.False(request.Headers.ContainsKey("Authorization"));
        var response = Binary(mime);
        response.Headers.TryAddWithoutValidation("x-request-id", "tts-fixture");
        response.Headers.TryAddWithoutValidation("Set-Cookie", "fixture=private");
        return response;
    }

    private static HttpResponseMessage Binary(string? mime)
    {
        var content = new ByteArrayContent([1, 2, 3]);
        if (mime is not null) content.Headers.ContentType = new(mime);
        return new(HttpStatusCode.OK) { Content = content };
    }

    private static async Task<JsonNode> Json(HttpResponseMessage response) => JsonNode.Parse(await GatewayTests.Success(response))!;
}
