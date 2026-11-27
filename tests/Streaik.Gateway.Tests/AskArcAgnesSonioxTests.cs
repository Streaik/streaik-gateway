using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace Streaik.Gateway.Tests;

public sealed class AskArcAgnesSonioxTests
{
    private static readonly (string, string) Arc = ("X-Askarc-Key", "arc-fixture");
    private static readonly (string, string) Agnes = ("X-Agnesai-Key", "agnes-fixture");
    private static readonly (string, string) Soniox = ("X-Soniox-Key", "soniox-fixture");

    [Fact]
    public async Task Discovery_respects_credentials_and_distinguishes_text_media_and_voice_models()
    {
        await using var host = await FixtureHost.StartAsync(request => (request.Host, request.Path) switch
        {
            ("api.askarc.app", "/api/v1/models") => FixtureHost.Json("""{"data":[{"id":"vendor/model","provider":"vendor","name":"Vendor Chat","contextWindow":128000}]}"""),
            ("apihub.agnes-ai.com", "/v1/models") => FixtureHost.Json("""{"data":[{"id":"general-chat"},{"id":"text-to-image"},{"id":"image-to-video","owned_by":"vendor"}]}"""),
            ("api.soniox.com", "/v1/models") => FixtureHost.Json("""{"models":[{"id":"stt-async-v4","transcription_mode":"async"},{"id":"stt-rt-v6","transcription_mode":"real_time","languages":[{"code":"en"}]}]}"""),
            ("api.soniox.com", "/v1/tts-models") => FixtureHost.Json("""{"models":[{"id":"tts-rt-v1","voices":[{"id":"demo","gender":"female"}]}]}"""),
            ("api.soniox.com", "/v1/voices?limit=100") => FixtureHost.Json("""{"voices":[],"next_page_cursor":"next+page"}"""),
            ("api.soniox.com", "/v1/voices?limit=100&cursor=next%2Bpage") => FixtureHost.Json("""{"voices":[{"id":"clone","models":[{"model":"tts-rt-v1","status":"ready"}]},{"id":"pending","models":[{"model":"tts-rt-v1","status":"pending"}]}]}"""),
            _ => throw new InvalidOperationException(request.Host + request.Path)
        }, askArcAgnesSoniox: true);
        using var anonymous = await host.GetAsync("/v1/models");
        await GatewayTests.Success(anonymous);
        Assert.Empty(host.Requests);
        using var response = await host.GetAsync("/v1/models", Arc, Agnes, Soniox);
        var models = JsonNode.Parse(await GatewayTests.Success(response))!["data"]!.AsArray().ToDictionary(model => model!["id"]!.GetValue<string>());
        Assert.Equal(128000, (int?)models["askarc/vendor/model"]!["context_window"]);
        Assert.Equal("vendor", (string?)models["askarc/vendor/model"]!["owned_by"]);
        Assert.Equal("language", (string?)models["agnesai/general-chat"]!["type"]);
        Assert.Equal("image", (string?)models["agnesai/text-to-image"]!["type"]);
        Assert.Equal("video", (string?)models["agnesai/image-to-video"]!["type"]);
        Assert.Equal("[\"realtime\"]", models["soniox/stt-rt-v6"]!["tags"]!.ToJsonString());
        Assert.Contains("soniox/tts-rt-v1/demo", models.Keys);
        Assert.Contains("soniox/tts-rt-v1/clone", models.Keys);
        Assert.DoesNotContain("soniox/tts-rt-v1/pending", models.Keys);
        Assert.All(host.Requests, request => Assert.Equal(request.Host switch
        {
            "api.askarc.app" => "Bearer arc-fixture",
            "apihub.agnes-ai.com" => "Bearer agnes-fixture",
            _ => "Bearer soniox-fixture"
        }, request.Headers["Authorization"]));
    }

    [Theory]
    [InlineData("askarc", "/v1/chat/completions", false)]
    [InlineData("askarc", "/v1/messages", false)]
    [InlineData("askarc", "/v1/responses", true)]
    [InlineData("askarc", "/api/chat", true)]
    [InlineData("agnesai", "/v1/messages", true)]
    [InlineData("agnesai", "/api/chat", true)]
    public async Task Compatible_chat_keeps_native_fields_on_existing_protocol_bridges(string provider, string route, bool streaming)
    {
        await using var host = await FixtureHost.StartAsync(request => request.Stream
            ? FixtureHost.Sse("""
                {"id":"chat","object":"chat.completion.chunk","model":"vendor/chat","created":1,"choices":[{"index":0,"delta":{"role":"assistant","content":"Native answer"},"finish_reason":null}],"vendor_trace":{"nested":[true,null]}}
                """, """
                {"id":"chat","object":"chat.completion.chunk","model":"vendor/chat","created":1,"choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":2,"total_tokens":3}}
                """, "[DONE]")
            : FixtureHost.Json("""
                {"id":"chat","object":"chat.completion","model":"vendor/chat","created":1,"choices":[{"index":0,"message":{"role":"assistant","content":"Native answer"},"finish_reason":"stop"}],"vendor_trace":{"nested":[true,null]}}
                """), askArcAgnesSoniox: true);
        var body = GatewayTests.Request(route, $"{provider}/vendor/chat", streaming).AsObject();
        var native = JsonNode.Parse("""{"vendor_input":{"nested":["x",null]},"model":"wrong","stream":false,"authorization":"wrong"}""");
        if (route == "/api/chat") body["providerMetadata"] = new JsonObject { [provider] = native };
        else body[provider] = native;
        using var response = await host.PostAsync(route, body.ToJsonString(), Arc, Agnes);
        var wire = await GatewayTests.Success(response);
        Assert.Contains("Native answer", wire);
        Assert.Contains("vendor_trace", wire);
        var upstream = Assert.Single(host.Requests);
        Assert.Equal(provider == "askarc" ? "/api/v1/chat/completions" : "/v1/chat/completions", upstream.Path);
        Assert.Equal(provider == "askarc" ? "Bearer arc-fixture" : "Bearer agnes-fixture", upstream.Headers["Authorization"]);
        Assert.Equal(streaming, upstream.Stream);
        Assert.Equal("vendor/chat", (string?)upstream.Body!["model"]);
        Assert.Null(upstream.Body["authorization"]);
        Assert.Equal("[\"x\",null]", upstream.Body["vendor_input"]!["nested"]!.ToJsonString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Agnes_responses_use_native_endpoint_and_preserve_extension_data(bool stream)
    {
        const string result = """{"id":"resp_native","model":"vendor/chat","status":"completed","created_at":1,"output":[],"vendor_trace":{"nested":[true,null]}}""";
        await using var host = await FixtureHost.StartAsync(request => request.Stream
            ? FixtureHost.Sse($$"""{"type":"response.completed","sequence_number":0,"response":{{result}}}""")
            : FixtureHost.Json(result), askArcAgnesSoniox: true);
        using var response = await host.PostAsync("/v1/responses", $$$"""
            {"model":"agnesai/vendor/chat","input":"hello","stream":{{{(stream ? "true" : "false")}}},
             "agnesai":{"vendor_input":[null,5],"model":"wrong","store":true}}
            """, Agnes);
        Assert.Contains("vendor_trace", await GatewayTests.Success(response));
        var upstream = Assert.Single(host.Requests);
        Assert.Equal("/v1/responses", upstream.Path);
        Assert.Equal("vendor/chat", (string?)upstream.Body!["model"]);
        Assert.NotEqual(true, (bool?)upstream.Body["store"]);
        Assert.Equal("[null,5]", upstream.Body["vendor_input"]!.ToJsonString());
    }

    [Fact]
    public async Task Agnes_image_edits_keep_native_options_and_download_assets_without_credentials()
    {
        await using var host = await FixtureHost.StartAsync(request => request.Path switch
        {
            "/v1/images/generations" => FixtureHost.Json("""{"data":[{"url":"https://cdn.agnes-ai.example/image.webp"}],"native_usage":{"steps":[1,null]}}"""),
            "/image.webp" => Binary("image/webp"),
            _ => throw new InvalidOperationException(request.Path)
        }, askArcAgnesSoniox: true);
        using var response = await host.PostAsync("/v1/images/edits", """
            {"model":"agnesai/text-to-image","prompt":"add a star","stream":true,"images":[{"image_url":"data:image/png;base64,AQID"}],
             "agnesai":{"model":"wrong","extra_body":{"native":{"values":[true,null]},"response_format":"url"}}}
            """, Agnes, Soniox);
        var wire = await GatewayTests.Success(response);
        Assert.Contains("image_edit.completed", wire);
        Assert.Contains("AQID", wire);
        Assert.Contains("native_usage", wire);
        var upstream = Assert.Single(host.Requests, request => request.Host == "apihub.agnes-ai.com");
        Assert.Equal("text-to-image", (string?)upstream.Body!["model"]);
        Assert.Equal("data:image/png;base64,AQID", (string?)upstream.Body["extra_body"]!["image"]![0]);
        Assert.Equal("b64_json", (string?)upstream.Body["extra_body"]!["response_format"]);
        Assert.Equal("[true,null]", upstream.Body["extra_body"]!["native"]!["values"]!.ToJsonString());
        Assert.Empty(Assert.Single(host.Requests, request => request.Path == "/image.webp").Headers);
    }

    [Fact]
    public async Task Agnes_video_operations_keep_native_state_and_support_existing_polling_route()
    {
        var polls = 0;
        await using var host = await FixtureHost.StartAsync(request => request.Path switch
        {
            "/v1/videos" => FixtureHost.Json("""{"video_id":"ag-video","status":"queued","native_queue":{"rank":4}}"""),
            "/agnesapi?video_id=ag-video" when Interlocked.Increment(ref polls) == 1 => FixtureHost.Json("""{"status":"processing","progress":15}"""),
            "/agnesapi?video_id=ag-video" => FixtureHost.Json("""{"status":"completed","metadata":{"url":"https://cdn.agnes-ai.example/video.mp4","trace":[null,4]}}"""),
            "/video.mp4" => Binary("video/mp4"),
            _ => throw new InvalidOperationException(request.Path)
        }, configure: services => services.AddStreaikReferences(), askArcAgnesSoniox: true);
        using var response = await host.PostAsync("/api/videos", """
            {"model":"agnesai/image-to-video","prompt":"a river","resolution":"640x480","fps":24,
             "providerOptions":{"agnesai":{"num_frames":48,"extra_body":{"mode":"keyframes","image":["https://media.example/a.png","https://media.example/b.png"]}},"other":{"secret":"wrong"}}}
            """, Agnes);
        var started = JsonNode.Parse(await GatewayTests.Success(response))!;
        Assert.Equal(4, (int?)started["providerMetadata"]!["agnesai"]!["native_queue"]!["rank"]);
        var path = "/api/videos/" + (string?)started["operation"];
        using var pending = await host.GetAsync(path, Agnes);
        Assert.Contains("\"progress\":15", await GatewayTests.Success(pending));
        using var done = await host.GetAsync(path, Agnes);
        var completed = JsonNode.Parse(await GatewayTests.Success(done))!;
        Assert.Equal("completed", (string?)completed["status"]);
        Assert.Equal("AQID", (string?)completed["videos"]![0]!["data"]);
        Assert.Equal("[null,4]", completed["providerMetadata"]!["agnesai"]!["metadata"]!["trace"]!.ToJsonString());
        var start = Assert.Single(host.Requests, request => request.Path == "/v1/videos");
        Assert.Equal(640, (int?)start.Body!["width"]);
        Assert.Equal(24, (double?)start.Body["frame_rate"]);
        Assert.Equal(48, (int?)start.Body["num_frames"]);
        Assert.Equal("keyframes", (string?)start.Body["extra_body"]!["mode"]);
        Assert.Null(start.Body["other"]);
        Assert.Empty(Assert.Single(host.Requests, request => request.Path == "/video.mp4").Headers);
    }

    [Fact]
    public async Task Soniox_speech_supports_model_voice_and_native_binary_streaming()
    {
        await using var host = await FixtureHost.StartAsync(_ => Binary("application/octet-stream"), askArcAgnesSoniox: true);
        using var speech = await host.PostAsync("/api/speech", """
            {"model":"soniox/tts-rt-v1/clone","text":"hello","language":"nl","voice":"wrong","outputFormat":"wav",
             "providerOptions":{"soniox":{"sample_rate":24000,"custom":{"nested":[true,null]},"model":"wrong"}}}
            """, Soniox, Agnes);
        var body = JsonNode.Parse(await GatewayTests.Success(speech))!;
        Assert.Equal("audio/wav", (string?)body["audio"]!["mediaType"]);
        Assert.Equal("AQID", (string?)body["audio"]!["base64"]);
        using var streaming = await host.PostAsync("/v1/audio/speech", """
            {"model":"soniox/tts-rt-v1","input":"hello","voice":"demo","stream_format":"sse","soniox":{"language":"nl"}}
            """, Soniox);
        Assert.Contains("speech.audio.done", await GatewayTests.Success(streaming));
        Assert.All(host.Requests, request =>
        {
            Assert.Equal("tts-rt.soniox.com", request.Host);
            Assert.Equal("/tts", request.Path);
            Assert.Equal("Bearer soniox-fixture", request.Headers["Authorization"]);
            Assert.Equal("tts-rt-v1", (string?)request.Body!["model"]);
            Assert.Equal("nl", (string?)request.Body["language"]);
        });
        Assert.Equal("clone", (string?)host.Requests.First().Body!["voice"]);
        Assert.Equal("[true,null]", host.Requests.First().Body!["custom"]!["nested"]!.ToJsonString());
        Assert.Equal("demo", (string?)host.Requests.Last().Body!["voice"]);
    }

    [Fact]
    public async Task Soniox_transcription_cleans_up_and_retains_typed_native_metadata()
    {
        await using var host = await FixtureHost.StartAsync(SonioxTranscript, askArcAgnesSoniox: true);
        using var response = await host.PostAsync("/api/transcriptions/stream", """
            {"model":"soniox/stt-async-v4","audio":"AQID","inputAudioFormat":{"type":"wav"},"includeRawChunks":true,
             "providerOptions":{"soniox":{"language_hints":["en"],"context":{"native":[true,null]},"client_reference_id":"ref-a","file_id":"wrong"},"other":{"wrong":true}}}
            """, Soniox);
        var wire = await GatewayTests.Success(response);
        var finish = GatewayTests.Part(wire, "finish");
        Assert.Equal("hello", (string?)finish["text"]);
        Assert.Equal(1.5, (double?)finish["durationInSeconds"]);
        Assert.Equal(0.1, (double?)finish["segments"]![0]!["startSecond"]);
        Assert.Equal(2, (int?)finish["providerMetadata"]!["soniox"]!["transcript"]!["tokens"]![0]!["speaker"]);
        Assert.Contains("\"type\":\"raw\"", wire);
        var requests = host.Requests.ToArray();
        Assert.Equal(["POST /v1/files", "POST /v1/transcriptions", "GET /v1/transcriptions/transcript-1",
            "GET /v1/transcriptions/transcript-1/transcript", "DELETE /v1/transcriptions/transcript-1", "DELETE /v1/files/file-1"],
            requests.Select(item => $"{item.Method} {item.Path}").ToArray());
        Assert.Contains("ref-a", requests[0].RawBody);
        Assert.Equal("file-1", (string?)requests[1].Body!["file_id"]);
        Assert.Equal("[true,null]", requests[1].Body!["context"]!["native"]!.ToJsonString());
        Assert.Null(requests[1].Body!["other"]);
    }

    [Fact]
    public async Task Soniox_native_transcription_supports_buffered_sse_and_multipart_native_options()
    {
        await using var host = await FixtureHost.StartAsync(SonioxTranscript, askArcAgnesSoniox: true);
        using var form = new MultipartFormDataContent();
        using var audio = new ByteArrayContent([1, 2, 3]);
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(audio, "file", "input.wav");
        form.Add(new StringContent("soniox/stt-async-v4"), "model");
        form.Add(new StringContent("true"), "stream");
        form.Add(new StringContent("""{"soniox":{"enable_speaker_diarization":true,"context":{"native":[null,2]}}}"""), "providerOptions");
        using var response = await host.PostFormAsync("/v1/audio/transcriptions", form, Soniox);
        var wire = await GatewayTests.Success(response);
        Assert.Contains("transcript.text.done", wire);
        Assert.Contains("hello", wire);
        var job = Assert.Single(host.Requests, request => request is { Method: "POST", Path: "/v1/transcriptions" });
        Assert.Equal(true, (bool?)job.Body!["enable_speaker_diarization"]);
        Assert.Equal("[null,2]", job.Body["context"]!["native"]!.ToJsonString());
    }

    [Fact]
    public async Task Soniox_deletes_the_job_and_upload_after_client_cancellation()
    {
        var polled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = await FixtureHost.StartAsync(request =>
        {
            if (request is { Method: "GET", Path: "/v1/transcriptions/transcript-1" })
            {
                polled.TrySetResult();
                return FixtureHost.Json("""{"status":"processing"}""");
            }
            if (request is { Method: "DELETE", Path: "/v1/files/file-1" }) deleted.TrySetResult();
            return SonioxTranscript(request);
        }, askArcAgnesSoniox: true);
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/transcriptions")
        {
            Content = new StringContent("""{"model":"soniox/stt-async-v4","audio":"AQID","mediaType":"audio/wav"}""", System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.Add(Soniox.Item1, Soniox.Item2);
        var task = host.Client.SendAsync(request, ct.Token);
        await polled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await ct.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { using var response = await task; });
        await deleted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains(host.Requests, item => item is { Method: "DELETE", Path: "/v1/transcriptions/transcript-1" });
    }

    [Fact]
    public async Task Soniox_realtime_returns_a_single_use_token_and_never_exposes_the_long_lived_key()
    {
        await using var host = await FixtureHost.StartAsync(_ => FixtureHost.Json("""{"api_key":"short-lived","expires_at":"2030-01-01T00:00:00Z"}"""), askArcAgnesSoniox: true);
        using var response = await host.PostAsync("/api/realtime/token/soniox/stt-rt-v6", """
            {"sessionConfig":{"providerOptions":{"soniox":{"expires_in_seconds":45,"max_session_duration_seconds":120,
               "single_use":false,"usage_type":"wrong","api_key":"wrong","client_reference_id":"r1"}}}}
            """, Soniox);
        var wire = await GatewayTests.Success(response);
        var body = JsonNode.Parse(wire)!;
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("short-lived", (string?)body["token"]);
        Assert.Equal("wss://stt-rt.soniox.com/transcribe-websocket", (string?)body["url"]);
        Assert.Equal(1893456000L, (long?)body["expiresAt"]);
        Assert.DoesNotContain("soniox-fixture", wire);
        var token = Assert.Single(host.Requests);
        Assert.Equal("/v1/auth/temporary-api-key", token.Path);
        Assert.Equal("transcribe_websocket", (string?)token.Body!["usage_type"]);
        Assert.Equal(true, (bool?)token.Body["single_use"]);
        Assert.Equal(45, (int?)token.Body["expires_in_seconds"]);
        Assert.Equal(120, (int?)token.Body["max_session_duration_seconds"]);
        Assert.Null(token.Body["api_key"]);
    }

    private static HttpResponseMessage SonioxTranscript(FixtureHost.UpstreamRequest request) => (request.Method, request.Path) switch
    {
        ("POST", "/v1/files") => FixtureHost.Json("""{"id":"file-1"}"""),
        ("POST", "/v1/transcriptions") => FixtureHost.Json("""{"id":"transcript-1"}"""),
        ("GET", "/v1/transcriptions/transcript-1") => FixtureHost.Json("""{"status":"completed","audio_duration_ms":1500,"provider_value":[null,3]}"""),
        ("GET", "/v1/transcriptions/transcript-1/transcript") => FixtureHost.Json("""{"text":"hello","tokens":[{"text":"hello","start_ms":100,"end_ms":450,"language":"en","speaker":2}]}"""),
        ("DELETE", "/v1/transcriptions/transcript-1" or "/v1/files/file-1") => new(HttpStatusCode.NoContent),
        _ => throw new InvalidOperationException($"Unexpected {request.Method} {request.Path}")
    };

    private static HttpResponseMessage Binary(string mime) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent([1, 2, 3]) { Headers = { ContentType = new MediaTypeHeaderValue(mime) } }
    };
}
