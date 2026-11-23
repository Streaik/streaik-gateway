using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Streaik.Gateway.Tests;

public sealed class MediaTests
{
    private static readonly (string, string) ImageKey = ("X-ImageRouter-Key", "images-fixture");
    private const string ImageModel = "imagerouter/vendor/image";
    private const string GeminiImageRequest = """
        {"model":"google/gemini-2.5-flash-image","prompt":"simple afbeelding huis en een boom","n":1}
        """;
    private static readonly (string, string) GeminiKey = ("X-Google-Key", "gemini-fixture-key");

    [Theory]
    [InlineData("/v1/images/generations", false)]
    [InlineData("/api/images", true)]
    public async Task Google_generation_routes_send_a_Gemini_image_interaction(string path, bool vercel)
    {
        await using var host = await FixtureHost.StartAsync(_ => FixtureHost.Json("""
            {"steps":[{"type":"model_output","content":[{"type":"text","text":"Image follows."},
              {"type":"image","mime_type":"image/jpeg","data":"AQID"}]}]}
            """), liveProviders: true);
        using var response = await host.PostAsync(path, GeminiImageRequest, GeminiKey);
        var body = JsonNode.Parse(await GatewayTests.Success(response))!;
        var request = Assert.Single(host.Requests);
        Assert.Equal("generativelanguage.googleapis.com", request.Host);
        Assert.Equal("POST", request.Method);
        Assert.Equal("/v1beta/interactions", request.Path);
        Assert.Equal(GeminiKey.Item2, request.Headers["x-goog-api-key"]);
        Assert.Equal("application/json", request.Headers["Accept"]);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""
            {"model":"gemini-2.5-flash-image","input":[{"type":"text","text":"simple afbeelding huis en een boom"}],
             "response_format":{"type":"image","mime_type":"image/png"},"store":false}
            """), request.Body), request.RawBody);
        if (vercel) Assert.Equal("data:image/jpeg;base64,AQID", (string?)body["images"]![0]);
        else
        {
            Assert.Equal("AQID", (string?)body["data"]![0]!["b64_json"]);
            Assert.Equal("jpeg", (string?)body["output_format"]);
        }
    }

    [Theory]
    [InlineData("/api/images", true)]
    [InlineData("/v1/images/generations", true)]
    [InlineData("/v1/images/generations", false)]
    public async Task Google_image_rejection_detail_is_debug_only_and_never_sent_to_the_client(string path, bool debug)
    {
        using var logs = new GeminiImageLogs();
        await using var host = await FixtureHost.StartAsync(_ => FixtureHost.Json("""
            {"error":{"code":400,"message":"Fixture rejection for gemini-fixture-key\nwith detail.","status":"INVALID_ARGUMENT"}}
            """, HttpStatusCode.BadRequest), liveProviders: true, configure: services => services.AddLogging(logging => logging.AddProvider(logs)
            .AddFilter<GeminiImageLogs>("Streaik.Providers.GoogleGeminiProvider", debug ? LogLevel.Debug : LogLevel.Information)));
        using var response = await host.PostAsync(path, GeminiImageRequest, GeminiKey);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Fixture rejection", body);
        Assert.DoesNotContain(GeminiKey.Item2, body);
        if (debug)
            Assert.Contains("Fixture rejection for [redacted] with detail.", Assert.Single(logs.Messages));
        else Assert.Empty(logs.Messages);
    }

    private sealed class GeminiImageLogs : ILoggerProvider, ILogger
    {
        public System.Collections.Concurrent.ConcurrentQueue<string> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName) => this;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel == LogLevel.Debug;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) Messages.Enqueue(formatter(state, exception));
        }
        public void Dispose() { }
    }

    [Theory]
    [InlineData("/v1/chat/completions", false, "images")]
    [InlineData("/v1/chat/completions", true, "images")]
    [InlineData("/v1/responses", false, "image_generation_call")]
    [InlineData("/v1/responses", true, "response.output_item.done")]
    [InlineData("/v1/messages", false, "base64")]
    [InlineData("/v1/messages", true, "content_block_start")]
    [InlineData("/api/chat", true, "data:image/png;base64,AQID")]
    public async Task Image_route_uses_one_native_generation_and_the_requested_envelope(string path, bool stream, string marker)
    {
        await using var host = await FixtureHost.StartAsync(_ => ImageReply(), media: true);
        using var response = await host.PostAsync(path, GatewayTests.Request(path, ImageModel, stream).ToJsonString(), ImageKey);
        var body = await GatewayTests.Success(response);
        var call = Assert.Single(host.Requests);
        Assert.Equal("/v1/openai/images/generations", call.Path);
        Assert.Equal("vendor/image", call.Body!["model"]!.GetValue<string>());
        Assert.Equal("Bearer images-fixture", call.Headers["Authorization"]);
        Assert.Contains(marker, body);
        Assert.Contains(ImageModel, body);
        Assert.Equal(stream ? "text/event-stream" : "application/json", response.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData("/v1/chat/completions", """{"messages":[{"role":"user","content":[{"type":"text","text":"edit"},{"type":"image_url","image_url":{"url":"data:image/png;base64,AQID"}}]}]}""")]
    [InlineData("/v1/responses", """{"input":[{"type":"message","role":"user","content":[{"type":"input_text","text":"edit"},{"type":"input_image","image_url":"data:image/png;base64,AQID"}]}]}""")]
    [InlineData("/v1/messages", """{"max_tokens":1024,"messages":[{"role":"user","content":[{"type":"text","text":"edit"},{"type":"image","source":{"type":"base64","media_type":"image/png","data":"AQID"}}]}]}""")]
    [InlineData("/api/chat", """{"messages":[{"id":"u1","role":"user","parts":[{"type":"text","text":"edit"},{"type":"file","url":"data:image/png;base64,AQID","mediaType":"image/png"}]}]}""")]
    [InlineData("/v1/images/edits", """{"prompt":"edit","images":[{"image_url":"data:image/png;base64,AQID"}]}""")]
    [InlineData("/api/images", """{"prompt":"edit","files":[{"type":"file","mediaType":"image/png","data":"AQID"}]}""")]
    public async Task Inline_edit_reaches_same_multipart_provider_without_remote_fetch(string path, string payload)
    {
        var request = JsonNode.Parse(payload)!.AsObject();
        request["model"] = ImageModel;
        await using var host = await FixtureHost.StartAsync(_ => ImageReply(), media: true);
        using var response = await host.PostAsync(path, request.ToJsonString(), ImageKey);
        await GatewayTests.Success(response);
        var upstream = Assert.Single(host.Requests);
        Assert.Equal("/v1/openai/images/edits", upstream.Path);
        Assert.Contains("image[]", upstream.RawBody);
        Assert.Contains("vendor/image", upstream.RawBody);
        Assert.Contains("edit", upstream.RawBody);
    }

    [Fact]
    public async Task Native_image_sse_preserves_upstream_partial_and_completed_events()
    {
        await using var host = await FixtureHost.StartAsync(_ => FixtureHost.Sse(
            """{"type":"image_generation.partial_image","b64_json":"AQ==","partial_image_index":0}""",
            """{"type":"image_generation.completed","b64_json":"AQID"}"""));
        using var reply = await host.PostAsync("/v1/images/generations", """{"model":"openai/gpt-image","prompt":"draw","stream":true,"partial_images":1}""", ("X-OpenAI-Key", "image-key"));
        var sse = await GatewayTests.Success(reply);
        Assert.Contains("event: image_generation.partial_image", sse);
        Assert.Contains("event: image_generation.completed", sse);
        Assert.True(Assert.Single(host.Requests).Stream);
    }

    [Fact]
    public async Task Vercel_and_native_image_settings_bind_to_the_same_provider_request()
    {
        await using var host = await FixtureHost.StartAsync(_ => ImageReply());
        const string native = """{"model":"openai/gpt-image","prompt":"draw","size":"1024x1024","aspect_ratio":"1:1","seed":7,"n":2,"quality":"high","output_format":"svg","frame_count":3,"svg":{"model":"svg-model","reasoning":{"effort":"medium"}}}""";
        const string vercel = """{"model":"openai/gpt-image","prompt":"draw","size":"1024x1024","aspectRatio":"1:1","seed":7,"n":2,"providerOptions":{"openai":{"quality":"high","output_format":"svg","frame_count":3,"svg":{"model":"svg-model","reasoning":{"effort":"medium"}},"stream":true,"partial_images":1,"model":"other","n":5,"files":[]}}}""";
        using var first = await host.PostAsync("/v1/images/generations", native, ("X-OpenAI-Key", "image-key"));
        using var second = await host.PostAsync("/api/images", vercel, ("X-OpenAI-Key", "image-key"));
        await GatewayTests.Success(first);
        var response = JsonNode.Parse(await GatewayTests.Success(second));
        Assert.Equal("data:image/png;base64,AQID", response!["images"]![0]!.GetValue<string>());
        var requests = host.Requests.ToArray();
        Assert.Equal(2, requests.Length);
        Assert.True(JsonNode.DeepEquals(requests[0].Body, requests[1].Body));
        Assert.Equal("high", requests[1].Body!["quality"]!.GetValue<string>());
        Assert.Equal(3, requests[1].Body!["frame_count"]!.GetValue<int>());
    }

    [Fact]
    public async Task Vercel_and_native_masked_edits_send_the_same_options_and_references()
    {
        await using var host = await FixtureHost.StartAsync(_ => ImageReply());
        const string native = """{"model":"openai/gpt-image","prompt":"edit","n":1,"input_fidelity":"high","images":[{"image_url":"data:image/png;base64,AQID"}],"mask":{"image_url":"data:image/png;base64,BAUG"},"markup":{"image_url":"data:image/png;base64,BwgJ"}}""";
        const string vercel = """{"model":"openai/gpt-image","prompt":"edit","n":1,"files":[{"type":"file","mediaType":"image/png","data":"AQID"}],"mask":{"type":"file","mediaType":"image/png","data":"BAUG"},"providerOptions":{"openai":{"input_fidelity":"high","markup":{"image_url":"data:image/png;base64,BwgJ"}}}}""";
        using var first = await host.PostAsync("/v1/images/edits", native, ("X-OpenAI-Key", "image-key"));
        using var second = await host.PostAsync("/api/images", vercel, ("X-OpenAI-Key", "image-key"));
        await GatewayTests.Success(first);
        await GatewayTests.Success(second);
        var requests = host.Requests.ToArray();
        Assert.Equal(2, requests.Length);
        Assert.True(JsonNode.DeepEquals(requests[0].Body, requests[1].Body));
        Assert.Equal("data:image/png;base64,BAUG", requests[1].Body!["mask"]!["image_url"]!.GetValue<string>());
    }

    [Fact]
    public async Task Native_multipart_image_edit_preserves_mask_and_typed_options()
    {
        await using var host = await FixtureHost.StartAsync(_ => ImageReply());
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("openai/gpt-image"), "model");
        form.Add(new StringContent("edit"), "prompt");
        form.Add(new StringContent("2"), "n");
        form.Add(new StringContent("7"), "seed");
        form.Add(new StringContent("""{"model":"svg-model","reasoning":{"effort":"high"}}"""), "svg");
        form.Add(new ByteArrayContent([1, 2, 3]), "image[]", "source.png");
        form.Add(new ByteArrayContent([4, 5, 6]), "mask", "mask.png");
        using var reply = await host.PostFormAsync("/v1/images/edits", form, ("X-OpenAI-Key", "image-key"));
        await GatewayTests.Success(reply);
        var request = Assert.Single(host.Requests);
        Assert.Contains("source.png", request.RawBody);
        Assert.Contains("mask.png", request.RawBody);
        Assert.Contains("svg-model", request.RawBody);
        Assert.Contains("name=seed", request.RawBody);
    }

    [Fact]
    public async Task Video_operations_survive_a_fresh_host_and_are_credential_bound()
    {
        var keys = new EphemeralDataProtectionProvider();
        void Configure(IServiceCollection services) => services.AddStreaikReferences().AddSingleton<IDataProtectionProvider>(keys);
        string operation;
        await using (var first = await FixtureHost.StartAsync(_ => FixtureHost.Json("""{"status":"processing","fetch_result":"https://api.imagerouter.io/v1/result/123"}"""), Configure, media: true))
        {
            using var reply = await first.PostAsync("/api/videos", """{"model":"imagerouter/vendor/video","prompt":"move"}""", ImageKey);
            operation = JsonNode.Parse(await GatewayTests.Success(reply))!["operation"]!.GetValue<string>();
        }
        await using var second = await FixtureHost.StartAsync(_ => FixtureHost.Json("""{"status":"completed","data":[{"url":"https://media.example/video.mp4"}]}"""), Configure, media: true);
        using var denied = await second.GetAsync($"/api/videos/{operation}", ("X-ImageRouter-Key", "other-key"));
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        Assert.Empty(second.Requests);
        using var allowed = await second.GetAsync($"/api/videos/{operation}", ImageKey);
        Assert.Contains("https://media.example/video.mp4", await GatewayTests.Success(allowed));
        Assert.Equal("/v1/result/123", Assert.Single(second.Requests).Path);
    }

    [Fact]
    public async Task Video_rejects_off_origin_provider_poll_url_without_a_second_request()
    {
        await using var host = await FixtureHost.StartAsync(_ => FixtureHost.Json("""{"fetch_result":"https://other.example/v1/result/123"}"""),
            s => s.AddStreaikReferences().AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider()), media: true);
        using var reply = await host.PostAsync("/api/videos", """{"model":"imagerouter/vendor/video","prompt":"move"}""", ImageKey);
        Assert.Equal(HttpStatusCode.BadGateway, reply.StatusCode);
        Assert.Single(host.Requests);
    }

    [Fact]
    public async Task Deepgram_uses_token_auth_for_speech_transcription_and_realtime()
    {
        static HttpResponseMessage Reply(FixtureHost.UpstreamRequest request)
        {
            if (request.Path.StartsWith("/v1/speak?", StringComparison.Ordinal))
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) { Headers = { ContentType = new("audio/mpeg") } } };
            return request.Path.StartsWith("/v1/listen?", StringComparison.Ordinal)
                ? FixtureHost.Json("""{"metadata":{"duration":1},"results":{"channels":[{"alternatives":[{"transcript":"hello","words":[{"word":"hello","start":0,"end":1}]}]}]}}""")
                : FixtureHost.Json("""{"access_token":"temporary","expires_in":30}""");
        }
        var headers = ("X-Deepgram-Key", "deep-secret");
        await using var host = await FixtureHost.StartAsync(Reply, media: true);
        using var speech = await host.PostAsync("/api/speech/stream", """{"model":"deepgram/aura-2-asteria-en","text":[{"text":"hello"}]}""", headers);
        Assert.Contains("\"type\":\"finish\"", await GatewayTests.Success(speech));
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("deepgram/nova-3"), "model");
        form.Add(new ByteArrayContent([4, 5]) { Headers = { ContentType = new MediaTypeHeaderValue("audio/wav") } }, "file", "sample.wav");
        using var transcription = await host.PostFormAsync("/v1/audio/transcriptions", form, headers);
        Assert.Contains("hello", await GatewayTests.Success(transcription));
        using var token = await host.PostAsync("/api/realtime/token/deepgram/nova-3", "{}", headers);
        Assert.Contains("temporary", await GatewayTests.Success(token));
        Assert.True(token.Headers.CacheControl?.NoStore);
        Assert.All(host.Requests, r => Assert.Equal("Token deep-secret", r.Headers["Authorization"]));
    }

    [Fact]
    public async Task OpenRouter_keeps_nested_ids_and_native_provider_options()
    {
        await using var host = await FixtureHost.StartAsync(r => r.Path switch
        {
            "/api/v1/embeddings" => FixtureHost.Json("""{"model":"vendor/embed","data":[{"index":1,"embedding":[2]},{"index":0,"embedding":[1]}],"usage":{"total_tokens":2}}"""),
            "/api/v1/rerank" => FixtureHost.Json("""{"id":"rerank_1","results":[{"index":1,"relevance_score":0.9}],"usage":{"cost":0.01}}"""),
            _ => FixtureHost.Json("""{"id":"chat_1","model":"vendor/text","choices":[{"message":{"role":"assistant","content":"hello"}}],"usage":{"cost":0.01}}""")
        }, media: true);
        var headers = ("X-OpenRouter-Key", "router-key");
        using var chat = await host.PostAsync("/v1/chat/completions", """{"model":"openrouter/vendor/text","messages":[{"role":"user","content":"test"}],"provider":{"sort":"latency"}}""", headers);
        Assert.Contains("openrouter/vendor/text", await GatewayTests.Success(chat));
        using var embeddings = await host.PostAsync("/api/embeddings", """{"model":"openrouter/vendor/embed","values":["a","b"]}""", headers);
        Assert.Equal(1, JsonNode.Parse(await GatewayTests.Success(embeddings))!["embeddings"]![0]![0]!.GetValue<int>());
        using var rerank = await host.PostAsync("/api/rerank", """{"model":"openrouter/vendor/rank","query":"a","documents":{"type":"text","values":["b","a"]}}""", headers);
        Assert.Contains("openrouter/vendor/rank", await GatewayTests.Success(rerank));
        Assert.Equal("latency", host.Requests.First().Body!["provider"]!["sort"]!.GetValue<string>());
        Assert.All(host.Requests, r => Assert.Equal("Bearer router-key", r.Headers["Authorization"]));
    }

    private static HttpResponseMessage ImageReply() => FixtureHost.Json("""{"created":1700000000,"output_format":"png","data":[{"b64_json":"AQID"}]}""");

    [Fact]
    public async Task OpenAI_transcription_stream_preserves_native_and_vercel_terminal_events()
    {
        await using var host = await FixtureHost.StartAsync(_ => FixtureHost.Sse(
            """{"type":"transcript.text.delta","delta":"hello"}""", """{"type":"transcript.text.done","text":"hello"}"""));
        var key = ("X-OpenAI-Key", "fixture");
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("openai/gpt-4o-transcribe"), "model");
        form.Add(new StringContent("true"), "stream");
        form.Add(new ByteArrayContent([1, 2, 3]) { Headers = { ContentType = new("audio/wav") } }, "file", "fixture.wav");
        using var native = await host.PostFormAsync("/v1/audio/transcriptions", form, key);
        Assert.Contains("transcript.text.done", await GatewayTests.Success(native));
        using var vercel = await host.PostAsync("/api/transcriptions/stream", """{"model":"openai/gpt-4o-transcribe","audio":"AQID","inputAudioFormat":{"type":"wav"}}""", key);
        Assert.Equal("hello", GatewayTests.Part(await GatewayTests.Success(vercel), "finish")["text"]!.GetValue<string>());
        Assert.All(host.Requests, request => { Assert.Contains("gpt-4o-transcribe", request.RawBody); Assert.DoesNotContain("openai/gpt-4o-transcribe", request.RawBody); });
    }
}
