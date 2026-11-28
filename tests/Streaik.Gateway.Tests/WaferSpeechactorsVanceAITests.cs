using System.Net;
using System.Text.Json.Nodes;

namespace Streaik.Gateway.Tests;

public class WaferSpeechactorsVanceAITests
{
    private static readonly (string, string) Wafer = ("X-Wafer-Key", "fixture-wafer");
    private static readonly (string, string) Speechactors = ("X-Speechactors-Key", "fixture-speechactors");
    private static readonly (string, string) Vance = ("X-VanceAI-Key", "fixture-vanceai");
    private const string NativeImage = "data:image/png;base64,AQID";

    [Fact]
    public async Task Model_discovery_normalizes_native_fields_and_lists_vanceai_tools()
    {
        await using var host = await Start(call => call.Path switch
        {
            "/v1/models" => FixtureHost.Json("""{"data":[{"id":"vendor/model","owned_by":"vendor","created":17,"wafer":{"display_name":"Fixture","description":"Nested text","context_length":4096}}]}"""),
            "/v1/voices" => FixtureHost.Json("""{"voices":[{"vid":"en-US-Ava","name":"Ava","gender":"Female"},{"vid":"en-US-Ava","locale":"en-US"}]}"""),
            "/v1/languages" => FixtureHost.Json("""{"items":[{"code":"en-US","language":"English (US)"}]}"""),
            _ => throw new InvalidOperationException(call.Path)
        });
        using var anonymous = await host.GetAsync("/v1/models");
        Assert.DoesNotContain("vanceai/", await GatewayTests.Success(anonymous));
        Assert.Empty(host.Requests);
        using var response = await host.GetAsync("/v1/models", Wafer, Speechactors, Vance);
        var models = (await Body(response))["data"]!.AsArray().ToDictionary(model => (string)model!["id"]!);
        Assert.Equal("Fixture", (string?)models["wafer/vendor/model"]!["name"]);
        Assert.Equal("Nested text", (string?)models["wafer/vendor/model"]!["description"]);
        Assert.Equal(4096, (int?)models["wafer/vendor/model"]!["context_window"]);
        Assert.Equal("Ava, Female, English (US)", (string?)models["speechactors/tts/en-US-Ava/en-US"]!["name"]);
        Assert.Equal("speech", (string?)models["speechactors/tts"]!["type"]);
        Assert.Equal(11, models.Keys.Count(id => id.StartsWith("vanceai/", StringComparison.Ordinal)));
        Assert.Equal("video", (string?)models["vanceai/video_hdr"]!["type"]);
        Assert.Equal("image", (string?)models["vanceai/remove_bg"]!["type"]);
        Assert.Equal(3, host.Requests.Count);
        foreach (var request in host.Requests)
            Assert.Equal(request.Host == "pass.wafer.ai" ? "Bearer fixture-wafer" : "Bearer fixture-speechactors", request.Headers["Authorization"]);
    }

    [Theory]
    [InlineData("/v1/chat/completions", false)]
    [InlineData("/v1/chat/completions", true)]
    [InlineData("/v1/messages", false)]
    [InlineData("/v1/messages", true)]
    public async Task Wafer_native_protocols_forward_extensions_and_streams(string path, bool stream)
    {
        await using var host = await Start(FixtureHost.Reply);
        using var response = await host.PostAsync(path, $$"""
            {"model":"wafer/vendor/model","messages":[{"role":"user","content":"hello"}],"max_tokens":32,
             "stream":{{(stream ? "true" : "false")}},"native_option":{"shape":[false,0]} }
            """, Wafer, ("anthropic-version", "2023-06-01"), ("anthropic-beta", "fixture-beta"));
        var body = await GatewayTests.Success(response);
        Assert.Contains("hello", body);
        Assert.Contains(path == "/v1/messages" && stream ? "future_content" : "future_response", body);
        var request = Assert.Single(host.Requests);
        Assert.Equal(("pass.wafer.ai", path, stream), (request.Host, request.Path, request.Stream));
        Assert.Equal("Bearer fixture-wafer", request.Headers["Authorization"]);
        Assert.Equal("vendor/model", (string?)request.Body!["model"]);
        Assert.False((bool)request.Body["native_option"]!["shape"]![0]!);
        if (path == "/v1/messages") Assert.Equal("fixture-beta", request.Headers["anthropic-beta"]);
    }

    [Theory]
    [InlineData("/v1/responses", false)]
    [InlineData("/v1/responses", true)]
    [InlineData("/api/chat", true)]
    public async Task Wafer_translated_routes_use_the_chat_bridge_and_preserve_native_metadata(string path, bool stream)
    {
        await using var host = await Start(FixtureHost.Reply);
        var request = path == "/api/chat"
            ? """{"model":"wafer/vendor/model","messages":[{"id":"m","role":"user","parts":[{"type":"text","text":"hello"}]}],"providerMetadata":{"wafer":{"native_option":{"nested":[0]}}}}"""
            : $$"""{"model":"wafer/vendor/model","input":"hello","stream":{{(stream ? "true" : "false")}},"wafer":{"native_option":{"nested":[0]} } }""";
        using var response = await host.PostAsync(path, request, Wafer);
        var body = await GatewayTests.Success(response);
        Assert.Contains("hello", body);
        Assert.Contains("future_response", body);
        var seen = Assert.Single(host.Requests);
        Assert.Equal("/v1/chat/completions", seen.Path);
        Assert.Equal(stream, seen.Stream);
        Assert.Equal(0, (int?)seen.Body!["native_option"]!["nested"]![0]);
    }

    [Fact]
    public async Task Speechactors_uses_voice_precedence_and_passes_native_options_and_safe_headers()
    {
        await using var host = await Start(_ => Audio());
        using var response = await host.PostAsync("/api/speech", """
            {"model":"speechactors/tts/en-US-Ava/en-US","text":"Speak this","voice":"primitive","language":"en-GB","outputFormat":"wav",
             "providerOptions":{"speechactors":{"vid":"override","locale":"fr-FR","text":"override","pitch":"-8",
               "speakingRate":30,"style":"calm","future":{"level":[3]},"authorization":"override"}}}
            """, Speechactors);
        var result = await Body(response);
        Assert.Equal("mp3", (string?)result["audio"]!["format"]);
        Assert.Equal("AQID", (string?)result["audio"]!["base64"]);
        var headers = result["providerMetadata"]!["speechactors"]!["headers"]!.AsObject()
            .ToDictionary(header => header.Key, header => (string?)header.Value, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("speech-request", headers["x-request-id"]);
        Assert.False(headers.ContainsKey("Set-Cookie"));
        var call = Assert.Single(host.Requests);
        Assert.Equal(("api.speechactors.com", "/v1/generate"), (call.Host, call.Path));
        Assert.Equal("Bearer fixture-speechactors", call.Headers["Authorization"]);
        Assert.Equal("Speak this", (string?)call.Body!["text"]);
        Assert.Equal("en-US-Ava", (string?)call.Body["vid"]);
        Assert.Equal("en-US", (string?)call.Body["locale"]);
        Assert.Equal(-8, (int?)call.Body["pitch"]);
        Assert.Equal(30, (int?)call.Body["speakingRate"]);
        Assert.Equal("calm", (string?)call.Body["style"]);
        Assert.Equal(3, (int?)call.Body["future"]!["level"]![0]);
        Assert.False(call.Body.AsObject().ContainsKey("authorization"));
    }

    [Fact]
    public async Task Speechactors_uses_existing_binary_sse_and_chat_routes()
    {
        await using var host = await Start(_ => Audio());
        using var native = await host.PostAsync("/v1/audio/speech", """
            {"model":"speechactors/tts","input":"hello","voice":"en-US-Ava","language":"en-US","stream_format":"sse"}
            """, Speechactors);
        var sse = await GatewayTests.Success(native);
        Assert.Contains("speech.audio.delta", sse);
        Assert.Contains("speech.audio.done", sse);
        Assert.Contains("AQID", sse);
        using var chat = await host.PostAsync("/api/chat", """
            {"model":"speechactors/tts","messages":[{"id":"m","role":"user","parts":[{"type":"text","text":"hello"}]}],
             "providerMetadata":{"speechactors":{"vid":"en-US-Ava","locale":"en-US"}}}
            """, Speechactors);
        var parts = await GatewayTests.Success(chat);
        Assert.Contains("data:audio/mpeg;base64,AQID", parts);
        Assert.Contains("speech-request", parts);
        Assert.All(host.Requests, call => Assert.Equal("en-US-Ava", (string?)call.Body!["vid"]));
    }

    [Fact]
    public async Task VanceAI_image_jobs_poll_and_fetch_results_without_leaking_transfer_details()
    {
        await using var host = await Start(call => call.Path switch
        {
            "/api/v1/jobs" => FixtureHost.Json("""{"job_id":"image-1","status":"queued"}"""),
            "/api/v1/jobs/image-1" => FixtureHost.Json("""
                {"job_id":"image-1","status":"succeeded","details":{"diagnostics":[true,0],"api_key":"fixture-vanceai",
                  "url":"https://assets.example/render.png?sig=private","echo":"Bearer fixture-vanceai"}}
                """),
            "/api/v1/jobs/image-1/result" => Redirect("https://assets.example/render.png?sig=private"),
            "/render.png?sig=private" => Media("image/png"),
            _ => throw new InvalidOperationException(call.Path)
        });
        using var response = await host.PostAsync("/api/images", """
            {"model":"vanceai/custom","prompt":"use warm tones","files":[{"type":"url","url":"https://input.example/image.png"}],
             "providerOptions":{"vanceai":{"config":{"levels":[0],"prompt":"override"},"tool":"override","output_format":"png"}}}
            """, Vance);
        var body = await GatewayTests.Success(response);
        Assert.DoesNotContain("sig=private", body);
        Assert.DoesNotContain("fixture-vanceai", body);
        var result = JsonNode.Parse(body)!;
        Assert.Equal(NativeImage, (string?)result["images"]![0]);
        Assert.True((bool)result["providerMetadata"]!["vanceai"]!["details"]!["diagnostics"]![0]!);
        var create = Assert.Single(host.Requests, call => call.Method == "POST");
        Assert.Equal("Bearer fixture-vanceai", create.Headers["Authorization"]);
        Assert.True(Guid.TryParseExact(create.Headers["Idempotency-Key"], "N", out _));
        Assert.Equal("custom", (string?)create.Body!["tool"]);
        Assert.Equal("https://input.example/image.png", (string?)create.Body["image_url"]);
        Assert.Equal("use warm tones", (string?)create.Body["config"]!["prompt"]);
        Assert.Equal(0, (int?)create.Body["config"]!["levels"]![0]);
        Assert.False(Assert.Single(host.Requests, call => call.Host == "assets.example").Headers.ContainsKey("Authorization"));
    }

    [Fact]
    public async Task VanceAI_accepts_uploaded_images_and_returns_the_existing_image_completion_event()
    {
        await using var host = await Start(call => call.Path.EndsWith("/result", StringComparison.Ordinal)
            ? Media("image/jpeg") : FixtureHost.Json("""{"job_id":"image-1","status":"succeeded"}"""));
        using var content = new MultipartFormDataContent
        {
            { new StringContent("vanceai/denoise"), "model" }, { new StringContent("true"), "stream" },
            { new ByteArrayContent([1, 2, 3]) { Headers = { ContentType = new("image/png") } }, "image", "source.png" }
        };
        using var response = await host.PostFormAsync("/v1/images/edits", content, Vance);
        var events = await GatewayTests.Success(response);
        Assert.Contains("image_edit.completed", events);
        Assert.Contains("AQID", events);
        var create = Assert.Single(host.Requests, call => call.Method == "POST");
        Assert.Contains("name=image", create.RawBody);
        Assert.Contains("filename=source.png", create.RawBody);
        Assert.Contains("denoise", create.RawBody);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task VanceAI_video_jobs_support_urls_uploads_pending_completion_and_failure(bool upload, bool fail)
    {
        var checks = 0;
        await using var host = await Start(call => call.Path switch
        {
            "/api/v1/uploads" => FixtureHost.Json("""{"upload_id":"upload-1","put_url":"https://assets.example/input?sig=private"}"""),
            "/input?sig=private" => new(HttpStatusCode.OK),
            "/api/v1/jobs" => FixtureHost.Json("""{"job_id":"video-1","status":"queued","provider_job":{"progress":0}}"""),
            "/api/v1/jobs/video-1" when Interlocked.Increment(ref checks) == 1 => FixtureHost.Json("""{"job_id":"video-1","status":"processing","progress":70}"""),
            "/api/v1/jobs/video-1" when fail => FixtureHost.Json("""{"job_id":"video-1","status":"failed","error":{"message":"Cannot decode video"}}"""),
            "/api/v1/jobs/video-1" => FixtureHost.Json("""{"job_id":"video-1","status":"succeeded","provider_job":{"finished":true}}"""),
            "/api/v1/jobs/video-1/result" => Media("video/mp4"),
            _ => throw new InvalidOperationException(call.Path)
        });
        var source = upload ? """{"type":"file","mediaType":"video/mp4","data":"AQID"}"""
            : """{"type":"url","mediaType":"video/mp4","url":"https://input.example/video.mp4"}""";
        using var start = await host.PostAsync("/api/videos", $$"""
            {"model":"vanceai/video_upscale","inputReferences":[{{source}}],"fps":60,
             "providerOptions":{"vanceai":{"config":{"scale":2,"fps":24},"fileName":"source.mp4","upload_id":"override"} } }
            """, Vance);
        var started = await Body(start);
        var operation = (string)started["operation"]!;
        Assert.StartsWith("vanceai/", operation);
        Assert.DoesNotContain("video-1", operation);
        Assert.Equal(0, (int?)started["providerMetadata"]!["vanceai"]!["provider_job"]!["progress"]);
        using var processing = await host.GetAsync("/api/videos/" + operation, Vance);
        Assert.Equal(70, (int?)(await Body(processing))["providerMetadata"]!["vanceai"]!["progress"]);
        using var completed = await host.GetAsync("/api/videos/" + operation, Vance);
        var body = await Body(completed);
        if (fail) Assert.Equal("Cannot decode video", (string?)body["error"]);
        else Assert.Equal("AQID", (string?)body["videos"]![0]!["data"]);
        var job = Assert.Single(host.Requests, call => call.Path == "/api/v1/jobs");
        Assert.Equal(60, (int?)job.Body!["config"]!["fps"]);
        Assert.Equal(2, (int?)job.Body["config"]!["scale"]);
        if (upload)
        {
            Assert.Equal("upload-1", (string?)job.Body["upload_id"]);
            var put = Assert.Single(host.Requests, call => call.Method == "PUT");
            Assert.False(put.Headers.ContainsKey("Authorization"));
            Assert.Equal("source.mp4", (string?)Assert.Single(host.Requests, call => call.Path == "/api/v1/uploads").Body!["file_name"]);
        }
        else Assert.Equal("https://input.example/video.mp4", (string?)job.Body["source_url"]);
    }

    private static Task<FixtureHost> Start(Func<FixtureHost.UpstreamRequest, HttpResponseMessage> reply)
        => FixtureHost.StartAsync(reply, configure: services => services.AddStreaikReferences(), waferSpeechactorsVanceAI: true);

    private static async Task<JsonNode> Body(HttpResponseMessage response) => JsonNode.Parse(await GatewayTests.Success(response))!;

    private static HttpResponseMessage Media(string type) => new(HttpStatusCode.OK)
        { Content = new ByteArrayContent([1, 2, 3]) { Headers = { ContentType = new(type) } } };

    private static HttpResponseMessage Audio()
    {
        var response = Media("audio/mpeg");
        response.Headers.Add("x-request-id", "speech-request");
        response.Headers.Add("Set-Cookie", "hidden");
        return response;
    }

    private static HttpResponseMessage Redirect(string location) => new(HttpStatusCode.Redirect) { Headers = { Location = new(location) } };
}
