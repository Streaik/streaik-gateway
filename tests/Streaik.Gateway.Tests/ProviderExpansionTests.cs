using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace Streaik.Gateway.Tests;

public sealed class ProviderExpansionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Fireworks_chat_uses_native_compatibility_route(bool stream)
    {
        await using var host = await FixtureHost.StartAsync(request =>
        {
            Assert.Equal("api.fireworks.ai", request.Host);
            Assert.Equal("/inference/v1/chat/completions", request.Path);
            Assert.Equal("accounts/fireworks/models/chat", (string?)request.Body!["model"]);
            Assert.Equal("Bearer fireworks-test", request.Headers["Authorization"]);
            Assert.Null(request.Body["store"]);
            return FixtureHost.Reply(request with { Path = "/v1/chat/completions" });
        }, decisions: true);
        using var response = await host.PostAsync("/v1/chat/completions", $$"""
            {"model":"fireworks/accounts/fireworks/models/chat","messages":[{"role":"user","content":"Hello"}],"stream":{{(stream ? "true" : "false")}}}
            """, ("x-fireworks-key", "fireworks-test"));
        var output = await GatewayTests.Success(response);
        Assert.Contains("hello", output);
        Assert.Contains("fireworks/accounts/fireworks/models/chat", output);
        if (stream) Assert.Contains("[DONE]", output);
    }

    [Theory]
    [InlineData("openrouter/org/video-generator", "openrouter", "/api/v1/videos", "/api/v1/videos/video_1", "/api/v1/videos/video_1/content?index=0")]
    [InlineData("google/veo-3.1-generate-preview", "google", "/v1beta/models/veo-3.1-generate-preview:predictLongRunning",
        "/v1beta/models/veo-3.1-generate-preview/operations/video_1", "/v1beta/files/video_1:download?alt=media")]
    public async Task Video_providers_start_poll_and_return_content(string model, string provider, string start, string poll, string download)
    {
        var google = provider == "google";
        var polls = 0;
        await using var host = await FixtureHost.StartAsync(request =>
        {
            Assert.Equal(google ? "generativelanguage.googleapis.com" : "openrouter.ai", request.Host);
            Assert.Equal(google ? "fixture" : "Bearer fixture", request.Headers[google ? "x-goog-api-key" : "Authorization"]);
            if (request.Path == start)
            {
                Assert.Equal("POST", request.Method);
                Assert.Equal("A windmill", (string?)(google ? request.Body!["instances"]![0]!["prompt"] : request.Body!["prompt"]));
                return FixtureHost.Json(google ? """{"name":"models/veo-3.1-generate-preview/operations/video_1"}""" : """{"id":"video_1"}""");
            }
            if (request.Path == poll)
            {
                if (++polls == 1) return FixtureHost.Json(google ? """{"done":false}""" : """{"status":"in_progress"}""");
                return FixtureHost.Json(google
                    ? """{"done":true,"response":{"generateVideoResponse":{"generatedSamples":[{"video":{"uri":"https://generativelanguage.googleapis.com/v1beta/files/video_1:download?alt=media"}}]}}}"""
                    : """{"status":"completed","model":"org/video-generator"}""");
            }
            Assert.Equal(download, request.Path);
            return Binary("video/mp4");
        }, configure: services => services.AddStreaikReferences(), media: !google, liveProviders: google);
        var key = ($"x-{provider}-key", "fixture");
        using var created = await host.PostAsync("/api/videos", $$"""{"model":"{{model}}","prompt":"A windmill"}""", key);
        var operation = (string)JsonNode.Parse(await GatewayTests.Success(created))!["operation"]!;
        Assert.StartsWith(provider + "/", operation);
        using var pending = await host.GetAsync($"/api/videos/{operation}", key);
        Assert.Equal("pending", (string?)JsonNode.Parse(await GatewayTests.Success(pending))!["status"]);
        using var completed = await host.GetAsync($"/api/videos/{operation}", key);
        var result = JsonNode.Parse(await GatewayTests.Success(completed))!;
        Assert.Equal("completed", (string?)result["status"]);
        Assert.Equal("AQID", (string?)result["videos"]![0]!["data"]);
    }

    [Theory]
    [InlineData(false, "models/")]
    [InlineData(true, "google/")]
    [InlineData(true, "models/google/")]
    public async Task Google_catalog_ID_starts_and_polls_prompt_only_Omni_video(bool useFallback, string upstreamPrefix)
    {
        const string nativeModel = "gemini-omni-1.2-flash";
        const string prompt = "A windmill";
        var upstreamModel = upstreamPrefix + nativeModel;
        await using var host = await FixtureHost.StartAsync(request => (request.Method, request.Path) switch
        {
            ("GET", "/v1beta/models?pageSize=1000") => FixtureHost.Json(useFallback ? """{"models":[]}""" : $$"""
                {"models":[{"name":"{{upstreamModel}}","supportedGenerationMethods":["generateContent"]}]}
                """),
            ("GET", "/v1beta/openai/models") when useFallback => FixtureHost.Json($$"""{"data":[{"id":"{{upstreamModel}}","type":"language"}]}"""),
            ("GET", "/v1beta/agents?page_size=1000") => FixtureHost.Json("""{"agents":[]}"""),
            ("POST", "/v1beta/interactions") => StartOmni(request),
            ("GET", "/v1beta/interactions/omni-fixture") => FixtureHost.Json("""
                {"status":"completed","model":"gemini-omni-1.2-flash","steps":[{"type":"model_output","content":[{"type":"video","data":"AQID","mime_type":"video/mp4"}]}]}
                """),
            _ => throw new InvalidOperationException($"Unexpected Google request: {request.Method} {request.Path}")
        }, configure: services => services.AddStreaikReferences(), liveProviders: true);

        var key = ("x-google-key", "fixture");
        using var listed = await host.GetAsync("/v1/models", key);
        var models = JsonNode.Parse(await GatewayTests.Success(listed))!["data"]!.AsArray();
        var video = Assert.Single(models, entry => (string?)entry?["type"] == "video")!;
        var model = (string)video["id"]!;
        Assert.Equal("google/" + nativeModel, model);
        Assert.Contains(models, entry => (string?)entry?["type"] == "language" && (string?)entry?["id"] == model);

        using var created = await host.PostAsync("/api/videos", $$"""{"model":"{{model}}","prompt":"{{prompt}}"}""", key);
        var operation = (string)JsonNode.Parse(await GatewayTests.Success(created))!["operation"]!;
        Assert.StartsWith("google/", operation);

        using var completed = await host.GetAsync($"/api/videos/{operation}", key);
        var result = JsonNode.Parse(await GatewayTests.Success(completed))!;
        Assert.Equal("completed", (string?)result["status"]);
        Assert.Equal("AQID", (string?)result["videos"]![0]!["data"]);

        static HttpResponseMessage StartOmni(FixtureHost.UpstreamRequest request)
        {
            Assert.Equal(nativeModel, (string?)request.Body!["model"]);
            Assert.Equal("video", (string?)request.Body["response_format"]!["type"]);
            Assert.True((bool?)request.Body["background"]);
            var input = Assert.Single(request.Body["input"]!.AsArray())!;
            Assert.Equal("text", (string?)input["type"]);
            Assert.Equal(prompt, (string?)input["text"]);
            return FixtureHost.Json("""{"id":"omni-fixture","status":"in_progress"}""");
        }
    }

    [Fact]
    public async Task OpenRouter_images_speech_and_transcription_use_existing_gateway_contracts()
    {
        await using var host = await FixtureHost.StartAsync(request => request.Path switch
        {
            "/api/v1/images" => FixtureHost.Json("""{"created":1,"data":[{"b64_json":"AQID"}]}"""),
            "/api/v1/audio/speech" => Binary("audio/mpeg"),
            "/api/v1/audio/transcriptions" => Transcript(request),
            _ => throw new InvalidOperationException("Unexpected OpenRouter request.")
        }, media: true);
        var key = ("x-openrouter-key", "fixture");
        using var images = await host.PostAsync("/v1/images/generations", """{"model":"openrouter/org/art","prompt":"A windmill"}""", key);
        Assert.Contains("AQID", await GatewayTests.Success(images));
        using var speech = await host.PostAsync("/v1/audio/speech", """{"model":"openrouter/org/voice","voice":"example","input":"Hello"}""", key);
        Assert.Equal(HttpStatusCode.OK, speech.StatusCode);
        Assert.Equal("audio/mpeg", speech.Content.Headers.ContentType?.MediaType);
        using var transcript = await host.PostAsync("/api/transcriptions", """{"model":"openrouter/org/stt","audio":"AQID","mediaType":"audio/wav"}""", key);
        Assert.Contains("Hello there", await GatewayTests.Success(transcript));

        static HttpResponseMessage Transcript(FixtureHost.UpstreamRequest request)
        {
            Assert.Equal("wav", (string?)request.Body!["input_audio"]!["format"]);
            Assert.Equal("AQID", (string?)request.Body["input_audio"]!["data"]);
            return FixtureHost.Json("""{"text":"Hello there","language":"en","duration":1.2}""");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Gladia_uploads_transcribes_maps_results_and_deletes_the_job(bool multipart)
    {
        const string id = "89fb9f3f-3436-4819-b516-1645afec9fd0";
        await using var host = await FixtureHost.StartAsync(request =>
        {
            Assert.Equal("api.gladia.io", request.Host);
            Assert.Equal("gladia-test", request.Headers["x-gladia-key"]);
            Assert.False(request.Headers.ContainsKey("Authorization"));
            return (request.Method, request.Path) switch
            {
                ("POST", "/v2/upload") => Upload(request),
                ("POST", "/v2/pre-recorded") => Start(request),
                ("GET", "/v2/pre-recorded/" + id) => FixtureHost.Json("""
                    {"status":"done","result":{"transcription":{"full_transcript":"Bonjour","languages":["fr"],"utterances":[{"text":"Bonjour","start":0.2,"end":0.9,"speaker":0}]},"metadata":{"audio_duration":1.25,"billing_time":15}}}
                    """),
                ("DELETE", "/v2/pre-recorded/" + id) => new(HttpStatusCode.NoContent),
                _ => throw new InvalidOperationException("Unexpected Gladia request.")
            };
        }, gladia: true);

        var key = ("x-gladia-key", "gladia-test");
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("gladia/solaria-3"), "model");
        form.Add(new StringContent("verbose_json"), "response_format");
        form.Add(new StringContent("fr"), "language");
        var file = new ByteArrayContent([1, 2, 3]);
        file.Headers.ContentType = new("audio/wav");
        form.Add(file, "file", "clip.wav");
        using var response = multipart ? await host.PostFormAsync("/v1/audio/transcriptions", form, key)
            : await host.PostAsync("/api/transcriptions", """{"model":"gladia/solaria-3","audio":"AQID","mediaType":"audio/wav","providerOptions":{"gladia":{"language":"fr","diarization":true}}}""", key);
        var result = JsonNode.Parse(await GatewayTests.Success(response))!;
        Assert.Equal("Bonjour", (string?)result["text"]);
        Assert.Equal("fr", (string?)result["language"]);
        Assert.Equal(1.25, (double)(result[multipart ? "duration" : "durationInSeconds"]!));
        var segment = Assert.IsType<JsonObject>(Assert.Single(result["segments"]!.AsArray()));
        Assert.Equal(multipart ? ["end", "id", "start", "text"] : new[] { "endSecond", "startSecond", "text" },
            segment.Select(entry => entry.Key).Order(StringComparer.Ordinal));
        if (multipart) Assert.Null(result["providerMetadata"]);
        else
        {
            Assert.Equal(0, (int?)result["providerMetadata"]!["gladia"]!["utterances"]![0]!["speaker"]);
            Assert.Equal(0.000666666m, (decimal?)result["providerMetadata"]!["gateway"]!["cost"]);
        }
        Assert.Equal(new[] { "POST", "POST", "GET", "DELETE" }, host.Requests.Select(x => x.Method));

        static HttpResponseMessage Upload(FixtureHost.UpstreamRequest request)
        {
            Assert.Contains("audio", request.RawBody);
            return FixtureHost.Json("""{"audio_url":"https://api.gladia.io/audio/file"}""");
        }
        static HttpResponseMessage Start(FixtureHost.UpstreamRequest request)
        {
            Assert.Equal("solaria-3", (string?)request.Body!["model"]);
            Assert.Equal("fr", (string?)request.Body["language_config"]!["languages"]![0]);
            return FixtureHost.Json($$"""{"id":"{{id}}","result_url":"https://ignored.example/not-used"}""");
        }
    }

    [Fact]
    public void Vercel_transcription_segment_has_only_the_locked_properties()
    {
        var properties = typeof(Streaik.Vercel.Models.TranscriptionSegment).GetProperties()
            .Select(property => property.Name).Order(StringComparer.Ordinal);
        Assert.Equal(new[] { "EndSecond", "StartSecond", "Text" }, properties);
    }

    [Fact]
    public async Task Gladia_live_catalog_and_session_only_allow_Solaria_1()
    {
        await using var host = await FixtureHost.StartAsync(request =>
        {
            Assert.Equal("/v2/live", request.Path);
            Assert.Equal("wav/pcm", (string?)request.Body!["encoding"]);
            return FixtureHost.Json("""{"id":"live-1","url":"wss://api.gladia.io/v2/live?token=ephemeral"}""");
        }, gladia: true);
        var key = ("x-gladia-key", "gladia-test");
        using var models = await host.GetAsync("/v1/models", key);
        var data = JsonNode.Parse(await GatewayTests.Success(models))!["data"]!.AsArray();
        Assert.Contains(data, x => (string?)x?["id"] == "gladia/solaria-1" && (string?)x?["tags"]?[0] == "realtime");
        Assert.Contains(data, x => (string?)x?["id"] == "gladia/solaria-3" && x?["tags"] is null);
        using var invalid = await host.PostAsync("/api/realtime/token/gladia/solaria-3", "{}", key);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var session = await host.PostAsync("/api/realtime/token/gladia/solaria-1", "{}", key);
        Assert.Contains("wss://api.gladia.io/", await GatewayTests.Success(session));
        Assert.Single(host.Requests);
    }

    private static HttpResponseMessage Binary(string mediaType)
    {
        var result = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
        result.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        return result;
    }
}
