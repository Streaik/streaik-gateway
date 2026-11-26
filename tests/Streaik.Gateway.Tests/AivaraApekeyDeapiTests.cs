using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace Streaik.Gateway.Tests;

public sealed class AivaraApekeyDeapiTests
{
    private static readonly (string, string) Aivara = ("X-Aivara-Key", "aivara-fixture");
    private static readonly (string, string) Apekey = ("X-Apekey-Key", "apekey-fixture");
    private static readonly (string, string) Deapi = ("X-Deapi-Key", "deapi-fixture");

    [Fact]
    public async Task Discovery_combines_static_catalogs_with_paginated_native_models()
    {
        await using var host = await FixtureHost.StartAsync(request => request.Path switch
        {
            "/api/v2/models?page=1" => FixtureHost.Json("""{"meta":{"last_page":2},"data":[{"slug":"image-model","name":"Image","inference_types":{"txt2img":{}}}]}"""),
            "/api/v2/models?page=2" => FixtureHost.Json("""{"data":[{"slug":"vendor/embedding","inference_types":["txt2embedding"]}]}"""),
            _ => throw new InvalidOperationException(request.Path)
        }, aivaraApekeyDeapi: true);
        using var response = await host.GetAsync("/v1/models", Aivara, Apekey, Deapi);
        var models = JsonNode.Parse(await GatewayTests.Success(response))!["data"]!.AsArray().ToDictionary(model => (string)model!["id"]!);
        Assert.Contains("aivara/deepseek-chat", models.Keys);
        Assert.Contains("apekey/accounts/fireworks/models/llama-v3p1-8b-instruct", models.Keys);
        Assert.Equal("image", (string?)models["deapi/image-model"]!["type"]);
        Assert.Equal("embedding", (string?)models["deapi/vendor/embedding"]!["type"]);
        Assert.Equal(2, host.Requests.Count);
        Assert.All(host.Requests, request => Assert.Equal("Bearer deapi-fixture", request.Headers["Authorization"]));
    }

    [Theory]
    [InlineData("aivara", "/v1/chat/completions", false)]
    [InlineData("aivara", "/v1/responses", true)]
    [InlineData("apekey", "/v1/chat/completions", true)]
    [InlineData("apekey", "/v1/messages", false)]
    [InlineData("apekey", "/api/chat", true)]
    public async Task Chat_adapters_use_native_paths_and_keep_nested_extensions(string provider, string path, bool stream)
    {
        await using var host = await FixtureHost.StartAsync(request => request.Stream
            ? FixtureHost.Sse("""
                {"id":"req","object":"chat.completion.chunk","created":1,"model":"vendor/model","choices":[{"index":0,"delta":{"role":"assistant","content":"Hello from upstream"},"finish_reason":null}],"native_trace":{"details":[null,7]}}
                """, """
                {"id":"req","object":"chat.completion.chunk","created":1,"model":"vendor/model","choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":2,"total_tokens":3}}
                """, "[DONE]")
            : FixtureHost.Json("""
                {"id":"req","object":"chat.completion","created":1,"model":"vendor/model","choices":[{"index":0,"message":{"role":"assistant","content":"Hello from upstream"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":2,"total_tokens":3},"native_trace":{"details":[null,7]}}
                """), aivaraApekeyDeapi: true);
        const string extensions = """{"custom":{"values":[true,null]},"model":"override","stream":false,"x-api-key":"override"}""";
        var body = JsonNode.Parse(path switch
        {
            "/v1/responses" => """{"input":"hello"}""",
            "/v1/messages" => """{"max_tokens":128,"messages":[{"role":"user","content":[{"type":"text","text":"hello"},{"type":"text","text":"again"}]}]}""",
            "/api/chat" => """{"messages":[{"id":"user","role":"user","parts":[{"type":"text","text":"hello"}]}]}""",
            _ => """{"messages":[{"role":"user","content":[{"type":"text","text":"hello"}]}]}"""
        })!.AsObject();
        body["model"] = provider + "/vendor/model";
        body["stream"] = stream;
        if (path == "/api/chat") body["providerMetadata"] = new JsonObject { [provider] = JsonNode.Parse(extensions) };
        else body[provider] = JsonNode.Parse(extensions);
        using var response = await host.PostAsync(path, body.ToJsonString(), provider == "aivara" ? Aivara : Apekey);
        var wire = await GatewayTests.Success(response);
        Assert.Contains("Hello from upstream", wire);
        Assert.Contains("native_trace", wire);
        var sent = Assert.Single(host.Requests);
        Assert.Equal(provider == "aivara" ? "developer.aivara-ai.com" : "apekey.ai", sent.Host);
        Assert.Equal(provider == "aivara" ? "/api/v1/ai-gateway" : "/v1/chat/completions", sent.Path);
        Assert.Equal(provider == "aivara" ? "aivara-fixture" : "Bearer apekey-fixture", sent.Headers[provider == "aivara" ? "X-API-Key" : "Authorization"]);
        Assert.Equal("vendor/model", (string?)sent.Body!["model"]);
        Assert.Equal(stream, sent.Stream);
        Assert.Null(sent.Body["x-api-key"]);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"values":[true,null]}"""), sent.Body["custom"]));
        var content = sent.Body["messages"]![0]!["content"];
        if (path == "/v1/chat/completions") Assert.IsType<JsonArray>(content);
        if (provider == "apekey" && path != "/v1/chat/completions") Assert.Contains("hello", (string)content!);
    }

    [Fact]
    public async Task Deapi_routes_image_model_by_catalog_type_and_keeps_native_status_metadata()
    {
        await using var host = await FixtureHost.StartAsync(request => request.Path switch
        {
            "/api/v2/models?page=1" => FixtureHost.Json("""{"data":[{"slug":"unpredictable-slug","inference_type":"txt2img"}]}"""),
            "/api/v2/images/generations" => FixtureHost.Json("""{"data":{"request_id":"image-1"}}"""),
            "/api/v2/jobs/image-1" => FixtureHost.Json("""{"data":{"status":"done","result":{"result_url":"https://assets.deapi.ai/item.png?sig=secret","native":{"values":[1,null]}}}}"""),
            "/item.png?sig=secret" => Binary("image/png"),
            _ => throw new InvalidOperationException(request.Path)
        }, aivaraApekeyDeapi: true);
        using var response = await host.PostAsync("/api/chat", """
            {"model":"deapi/unpredictable-slug","messages":[{"id":"u","role":"user","parts":[{"type":"text","text":"a fox"}]}],
             "providerMetadata":{"deapi":{"width":768,"seed":17,"custom":{"values":[true,null]},"model":"override","authorization":"override"}}}
            """, Deapi);
        var wire = await GatewayTests.Success(response);
        Assert.Contains("native", wire);
        Assert.DoesNotContain("sig=secret", wire);
        var sent = Assert.Single(host.Requests, request => request.Path == "/api/v2/images/generations");
        Assert.Equal("unpredictable-slug", (string?)sent.Body!["model"]);
        Assert.Equal(17, (int?)sent.Body["seed"]);
        Assert.Equal(768, (int?)sent.Body["width"]);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"values":[true,null]}"""), sent.Body["custom"]));
        Assert.Null(sent.Body["authorization"]);
        var asset = Assert.Single(host.Requests, request => request.Host == "assets.deapi.ai");
        Assert.Empty(asset.Headers);
    }

    [Fact]
    public async Task Deapi_native_media_maps_multipart_and_buffers_image_completion()
    {
        await using var host = await FixtureHost.StartAsync(request => request.Path switch
        {
            "/api/v2/images/edits" => FixtureHost.Json("""{"request_id":"edit-1"}"""),
            "/api/v2/jobs/edit-1" => FixtureHost.Json("""{"data":{"status":"done","result_url":"https://assets.deapi.ai/edit.png","provider_counter":9}}"""),
            "/edit.png" => Binary("image/png"),
            "/api/v2/audio/speech" => FixtureHost.Json("""{"request_id":"audio-1"}"""),
            "/api/v2/jobs/audio-1" => FixtureHost.Json("""{"data":{"status":"done","result_url":"https://assets.deapi.ai/speech.mp3","provider_counter":10}}"""),
            "/speech.mp3" => Binary("audio/mpeg"),
            "/api/v2/audio/transcriptions" => FixtureHost.Json("""{"request_id":"transcript-1"}"""),
            "/api/v2/jobs/transcript-1" => FixtureHost.Json("""{"data":{"status":"done","result":{"text":"hello","voice_events":[null,7]}}}"""),
            _ => throw new InvalidOperationException(request.Path)
        }, aivaraApekeyDeapi: true);
        using var edit = await host.PostAsync("/v1/images/edits", """
            {"model":"deapi/edit","prompt":"add a star","stream":true,"images":[{"image_url":"data:image/png;base64,AQID"}],"deapi":{"steps":8,"image":"override"}}
            """, Deapi);
        var edited = await GatewayTests.Success(edit);
        Assert.Contains("image_edit.completed", edited);
        Assert.Contains("provider_counter", edited);
        var submitted = Assert.Single(host.Requests, request => request.Path == "/api/v2/images/edits");
        Assert.Contains("name=image", submitted.RawBody);
        Assert.Contains("name=steps", submitted.RawBody);
        Assert.DoesNotContain("override", submitted.RawBody);

        using var speech = await host.PostAsync("/api/speech", """
            {"model":"deapi/speaker","text":"hello","voice":"demo","language":"es","outputFormat":"mp3","providerOptions":{"deapi":{"extra":[1,2]}}}
            """, Deapi);
        Assert.Contains("provider_counter", await GatewayTests.Success(speech));
        var speechBody = Assert.Single(host.Requests, request => request.Path == "/api/v2/audio/speech").RawBody;
        Assert.Contains("name=lang", speechBody);
        Assert.Contains("name=extra", speechBody);

        using var transcription = await host.PostAsync("/api/transcriptions", """
            {"model":"deapi/listener","audio":"https://example.org/interview.wav","mediaType":"audio/wav","providerOptions":{"deapi":{"include_ts":true}}}
            """, Deapi);
        var transcript = await GatewayTests.Success(transcription);
        Assert.Contains("hello", transcript);
        Assert.Contains("voice_events", transcript);
        var form = Assert.Single(host.Requests, request => request.Path == "/api/v2/audio/transcriptions").RawBody;
        Assert.Contains("name=source_url", form);
        Assert.DoesNotContain("name=source_file", form);

        using var videoTranscript = await host.PostAsync("/api/transcriptions", """
            {"model":"deapi/listener","audio":"data:video/mp4;base64,AQID","mediaType":"video/mp4"}
            """, Deapi);
        Assert.Contains("voice_events", await GatewayTests.Success(videoTranscript));
        var upload = host.Requests.Last(request => request.Path == "/api/v2/audio/transcriptions").RawBody;
        Assert.Contains("name=source_file", upload);
        Assert.Contains("video/mp4", upload);
    }

    [Fact]
    public async Task Deapi_video_transitions_from_pending_to_done_and_downloads_without_auth()
    {
        var polls = 0;
        await using var host = await FixtureHost.StartAsync(request => request.Path switch
        {
            "/api/v2/videos/generations" => FixtureHost.Json("""{"data":{"request_id":"video-1","status":"queued","native_queue":9}}"""),
            "/api/v2/jobs/video-1" when Interlocked.Increment(ref polls) == 1 => FixtureHost.Json("""{"data":{"status":"running","progress":31}}"""),
            "/api/v2/jobs/video-1" => FixtureHost.Json("""{"data":{"status":"done","result_url":"https://assets.deapi.ai/video.mp4"}}"""),
            "/video.mp4" => Binary("video/mp4"),
            _ => throw new InvalidOperationException(request.Path)
        }, configure: services => services.AddStreaikReferences(), aivaraApekeyDeapi: true);
        using var start = await host.PostAsync("/api/videos", """
            {"model":"deapi/video-model","prompt":"a river","resolution":"640x480","providerOptions":{"deapi":{"frames":32,"model":"override"}}}
            """, Deapi);
        var started = JsonNode.Parse(await GatewayTests.Success(start))!;
        Assert.Equal(9, (int?)started["providerMetadata"]?["deapi"]?["native_queue"]);
        var job = "/api/videos/" + (string)started["operation"]!;
        using var pending = await host.GetAsync(job, Deapi);
        Assert.Contains("progress", await GatewayTests.Success(pending));
        using var done = await host.GetAsync(job, Deapi);
        Assert.Contains("AQID", await GatewayTests.Success(done));
        var sent = Assert.Single(host.Requests, request => request.Path == "/api/v2/videos/generations");
        Assert.Equal(640, (int?)sent.Body!["width"]);
        Assert.Equal(32, (int?)sent.Body["frames"]);
        Assert.Empty(Assert.Single(host.Requests, request => request.Path == "/video.mp4").Headers);
    }

    [Fact]
    public async Task Deapi_embeddings_preserve_native_options_and_response_extensions()
    {
        await using var host = await FixtureHost.StartAsync(_ => FixtureHost.Json("""
            {"object":"list","model":"embed","data":[{"object":"embedding","index":0,"embedding":[0.1,0.2]}],"usage":{"total_tokens":1,"prompt_tokens":1},"provider_metric":{"nested":[1,null]}}
            """), aivaraApekeyDeapi: true);
        using var response = await host.PostAsync("/v1/embeddings", """
            {"model":"deapi/embed","input":"hi","deapi":{"custom":{"normalize":true},"input":"override"}}
            """, Deapi);
        Assert.Equal("deapi/embed", (string?)JsonNode.Parse(await GatewayTests.Success(response))?["model"]);
        var sent = Assert.Single(host.Requests);
        Assert.Equal("/v1/embeddings", sent.Path);
        Assert.Equal("hi", (string?)sent.Body!["input"]);
        Assert.True((bool?)sent.Body["custom"]?["normalize"]);

        using var vercel = await host.PostAsync("/api/embeddings", """
            {"model":"deapi/embed","values":["hi"],"providerOptions":{"deapi":{"custom":{"normalize":true}}}}
            """, Deapi);
        var output = JsonNode.Parse(await GatewayTests.Success(vercel));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"nested":[1,null]}"""), output?["response"]?["body"]?["provider_metric"]));
    }

    [Theory]
    [InlineData("{\"status\":\"error\",\"error_message\":\"cannot generate\"}")]
    [InlineData("{\"status\":\"done\",\"result_url\":\"https://example.org/untrusted.png\"}")]
    public async Task Deapi_failed_jobs_or_untrusted_assets_do_not_download(string status)
    {
        await using var host = await FixtureHost.StartAsync(request => request.Path switch
        {
            "/api/v2/images/generations" => FixtureHost.Json("""{"request_id":"image-1"}"""),
            "/api/v2/jobs/image-1" => FixtureHost.Json("{\"data\":" + status + "}"),
            _ => throw new InvalidOperationException(request.Path)
        }, aivaraApekeyDeapi: true);
        using var result = await host.PostAsync("/v1/images/generations", """{"model":"deapi/image","prompt":"a fox"}""", Deapi);
        Assert.Equal(HttpStatusCode.BadGateway, result.StatusCode);
        Assert.Equal(2, host.Requests.Count);
    }

    [Fact]
    public async Task Deapi_job_poll_stops_when_gateway_request_is_cancelled()
    {
        var firstPoll = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = await FixtureHost.StartAsync(request =>
        {
            if (request.Path == "/api/v2/images/generations") return FixtureHost.Json("""{"request_id":"image-1"}""");
            firstPoll.TrySetResult();
            return FixtureHost.Json("""{"data":{"status":"running"}}""");
        }, aivaraApekeyDeapi: true);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/images/generations")
        {
            Content = new StringContent("""{"model":"deapi/image","prompt":"a fox"}""", System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.Add(Deapi.Item1, Deapi.Item2);
        var sending = host.Client.SendAsync(request, cancellation.Token);
        await firstPoll.Task.WaitAsync(cancellation.Token);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await sending);
        await Task.Delay(TimeSpan.FromSeconds(2.2));
        Assert.Single(host.Requests, item => item.Path == "/api/v2/jobs/image-1");
    }

    private static HttpResponseMessage Binary(string mime) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent([1, 2, 3]) { Headers = { ContentType = new MediaTypeHeaderValue(mime) } }
    };
}
