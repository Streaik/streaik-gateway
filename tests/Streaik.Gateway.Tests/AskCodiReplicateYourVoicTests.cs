using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Streaik.Gateway.Tests;

public class AskCodiReplicateYourVoicTests
{
    private static readonly (string, string) AskCodi = ("X-AskCodi-Key", "fixture-codi");
    private static readonly (string, string) Replicate = ("X-Replicate-Key", "fixture-replicate");
    private static readonly (string, string) YourVoic = ("X-YourVoic-Key", "fixture-yourvoic");
    private const string PredictionPath = "/v1/models/black-forest-labs/flux-dev/predictions";
    private const string ImageResult = """
        {"status":"succeeded","id":"pred1","input":{"file":"not-for-client"},"output":{"image":"https://replicate.delivery/example.png"},"metrics":{"predict_time":0.3,"diagnostic":[null,false]}}
        """;
    private const string Transcript = """
        {"text":"heard this","language":"en","duration":1.5,"utterances":[{"text":"heard this","start":0.2,"end":1.5,"speaker":2}],"segments":[{"id":4,"start":0.2,"end":1.5,"text":"heard this"}],"words":[{"word":"heard","start":0.2,"end":0.6}],"diagnostics":{"trace":[null,0,false]}}
        """;

    [Fact]
    public async Task Model_listing_is_credential_scoped_and_advertises_only_runnable_modalities()
    {
        await using var host = await FixtureHost.StartAsync(request =>
        {
            Assert.Equal(("api.askcodi.com", "/v1/models"), (request.Host, request.Path));
            Assert.Equal("Bearer fixture-codi", request.Headers["Authorization"]);
            return FixtureHost.Reply(request);
        }, askCodiReplicateYourVoic: true);
        using var response = await host.GetAsync("/v1/models", AskCodi, Replicate, YourVoic);
        var models = (await Body(response))["data"]!.AsArray();
        Check("askcodi/vendor/Model", "language");
        Check("replicate/black-forest-labs/flux-dev", "image");
        Check("yourvoic/aura-lite", "speech");
        Check("yourvoic/lucid-multi", "transcription");
        Assert.DoesNotContain(models, model => ((string?)model?["id"])?.StartsWith("replicate/openai/") == true);
        Assert.Single(host.Requests);
        using var unauthenticated = await host.GetAsync("/v1/models");
        Assert.DoesNotContain("askcodi/", await GatewayTests.Success(unauthenticated));
        Assert.Single(host.Requests);

        void Check(string id, string type) => Assert.Equal(type, (string?)Assert.Single(models, model => (string?)model?["id"] == id)!["type"]);
    }

    [Theory]
    [InlineData("/v1/chat/completions", """{"model":"askcodi/vendor/Model","messages":[{"role":"user","content":"hello"}],"askcodi":{"native_option":[null,0,false],"model":"override","authorization":"override"}}""", false)]
    [InlineData("/v1/responses", """{"model":"askcodi/vendor/Model","input":"hello","stream":true,"askcodi":{"native_option":[null,0,false]}}""", true)]
    [InlineData("/v1/messages", """{"model":"askcodi/vendor/Model","messages":[{"role":"user","content":"hello"}],"max_tokens":32,"stream":true,"askcodi":{"native_option":[null,0,false]}}""", true)]
    [InlineData("/api/chat", """{"model":"askcodi/vendor/Model","messages":[{"id":"m","role":"user","parts":[{"type":"text","text":"hello"}]}],"providerMetadata":{"askcodi":{"native_option":[null,0,false]}}}""", true)]
    public async Task AskCodi_uses_chat_transport_and_preserves_native_fields(string route, string payload, bool stream)
    {
        await using var host = await FixtureHost.StartAsync(request =>
        {
            Assert.Equal(("api.askcodi.com", "/v1/chat/completions"), (request.Host, request.Path));
            Assert.Equal("Bearer fixture-codi", request.Headers["Authorization"]);
            Assert.Equal("vendor/Model", (string?)request.Body!["model"]);
            Assert.Equal("[null,0,false]", request.Body["native_option"]!.ToJsonString());
            Assert.False(request.Body.AsObject().ContainsKey("authorization"));
            Assert.Equal(stream, request.Stream);
            return FixtureHost.Reply(request);
        }, askCodiReplicateYourVoic: true);
        using var response = await host.PostAsync(route, payload, AskCodi, YourVoic);
        var wire = await GatewayTests.Success(response);
        Assert.Contains("hello", wire);
        Assert.Contains("future_response", wire);
        Assert.Single(host.Requests);
    }

    [Fact]
    public async Task Replicate_polls_by_id_and_fetches_assets_without_credentials()
    {
        await using var host = await FixtureHost.StartAsync(request => request.Path switch
        {
            PredictionPath => FixtureHost.Json("""{"status":"processing","id":"pred1","urls":{"get":"https://untrusted.example/pred1"}}"""),
            "/v1/predictions/pred1" => FixtureHost.Json(ImageResult),
            "/example.png" => Asset(request),
            _ => throw new InvalidOperationException(request.Path)
        }, askCodiReplicateYourVoic: true);
        using var response = await host.PostAsync("/api/images", """
            {"model":"replicate/black-forest-labs/flux-dev","prompt":"cloud","size":"768x512","n":2,"seed":0,
             "providerOptions":{"replicate":{"preferWaitSeconds":100,"cancelAfter":"45s","native":{"values":[1,null]},"inputOverrides":{"width":640,"model":"override","authorization":"override"}}}}
            """, Replicate, AskCodi);
        var body = await Body(response);
        Assert.Equal("data:image/png;base64,AQID", (string?)body["images"]![0]);
        Assert.Equal("n", (string?)body["warnings"]![0]!["feature"]);
        Assert.Equal("[null,false]", body["providerMetadata"]!["replicate"]!["prediction"]!["metrics"]!["diagnostic"]!.ToJsonString());
        Assert.DoesNotContain("not-for-client", body.ToJsonString());
        var create = Assert.Single(host.Requests, request => request.Method == "POST");
        Assert.Equal("wait=60", create.Headers["Prefer"]);
        Assert.Equal("45s", create.Headers["Cancel-After"]);
        var input = create.Body!["input"]!.AsObject();
        Assert.Equal(640, (int?)input["width"]);
        Assert.Equal(512, (int?)input["height"]);
        Assert.Equal(0, (int?)input["seed"]);
        Assert.Equal("[1,null]", input["native"]!["values"]!.ToJsonString());
        Assert.False(input.ContainsKey("model"));
        Assert.False(input.ContainsKey("authorization"));
        var api = host.Requests.Where(request => request.Host == "api.replicate.com").ToArray();
        Assert.Equal(2, api.Length);
        Assert.All(api, request => Assert.Equal("Bearer fixture-replicate", request.Headers["Authorization"]));
    }

    [Fact]
    public async Task Replicate_edits_uploaded_images_and_frames_completion_with_metadata()
    {
        await using var host = await FixtureHost.StartAsync(request => request.Path == PredictionPath ? FixtureHost.Json(ImageResult) : Asset(request),
            askCodiReplicateYourVoic: true);
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("replicate/black-forest-labs/flux-dev"), "model");
        form.Add(new StringContent("cloud"), "prompt");
        form.Add(new StringContent("true"), "stream");
        form.Add(new StringContent("""{"replicate":{"inputOverrides":{"extra":{"values":[true,null]}}}}"""), "providerOptions");
        form.Add(Bytes("image/png"), "image", "test.png");
        using var response = await host.PostFormAsync("/v1/images/edits", form, Replicate);
        var wire = await GatewayTests.Success(response);
        Assert.Contains("image_edit.completed", wire);
        Assert.Contains("providerMetadata", wire);
        Assert.Contains("AQID", wire);
        var input = Assert.Single(host.Requests, request => request.Method == "POST").Body!["input"]!;
        Assert.Equal("data:image/png;base64,AQID", (string?)input["file"]);
        Assert.Equal("[true,null]", input["extra"]!["values"]!.ToJsonString());
    }

    [Theory]
    [InlineData("/v1/chat/completions", """{"model":"replicate/black-forest-labs/flux-dev","messages":[{"role":"user","content":"cloud"}]}""")]
    [InlineData("/v1/responses", """{"model":"replicate/black-forest-labs/flux-dev","input":"cloud"}""")]
    [InlineData("/v1/messages", """{"model":"replicate/black-forest-labs/flux-dev","messages":[{"role":"user","content":"cloud"}],"max_tokens":32}""")]
    [InlineData("/api/chat", """{"model":"replicate/black-forest-labs/flux-dev","messages":[{"id":"m","role":"user","parts":[{"type":"text","text":"cloud"}]}]}""")]
    public async Task Replicate_images_route_through_existing_chat_adapters(string route, string payload)
    {
        await using var host = await FixtureHost.StartAsync(request => request.Path == PredictionPath ? FixtureHost.Json(ImageResult) : Asset(request),
            askCodiReplicateYourVoic: true);
        using var response = await host.PostAsync(route, payload, Replicate);
        var wire = await GatewayTests.Success(response);
        Assert.Contains("AQID", wire);
        Assert.Contains("predict_time", wire);
        Assert.Equal(2, host.Requests.Count);
    }

    [Fact]
    public async Task Replicate_stops_polling_when_the_caller_cancels()
    {
        var polled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = await FixtureHost.StartAsync(request =>
        {
            if (request.Path == "/v1/predictions/pred1") polled.TrySetResult();
            return FixtureHost.Json("""{"status":"processing","id":"pred1"}""");
        }, askCodiReplicateYourVoic: true);
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var message = new HttpRequestMessage(HttpMethod.Post, "/v1/images/generations");
        message.Headers.Add(Replicate.Item1, Replicate.Item2);
        message.Content = new StringContent("""{"model":"replicate/black-forest-labs/flux-dev","prompt":"cloud"}""", Encoding.UTF8, "application/json");
        var response = host.Client.SendAsync(message, ct.Token);
        await polled.Task.WaitAsync(ct.Token);
        await ct.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await response);
        Assert.Equal(2, host.Requests.Count);
    }

    [Fact]
    public async Task Unsupported_modalities_and_failed_predictions_do_not_call_unrelated_endpoints()
    {
        await using var host = await FixtureHost.StartAsync(request => request.Path == PredictionPath
            ? FixtureHost.Json("""{"status":"failed","id":"pred1","error":"fixture-replicate","output":"https://replicate.delivery/example.png"}""")
            : throw new InvalidOperationException(request.Path), askCodiReplicateYourVoic: true);
        using var badModel = await host.PostAsync("/v1/images/generations", """{"model":"replicate/openai/gpt-5","prompt":"hi"}""", Replicate);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badModel.StatusCode);
        using var unsupported = await host.PostAsync("/v1/embeddings", """{"model":"askcodi/m","input":"hi"}""", AskCodi);
        Assert.False(unsupported.IsSuccessStatusCode);
        Assert.Empty(host.Requests);
        using var failed = await host.PostAsync("/v1/images/generations", """{"model":"replicate/black-forest-labs/flux-dev","prompt":"hi"}""", Replicate);
        Assert.Equal(HttpStatusCode.BadGateway, failed.StatusCode);
        Assert.DoesNotContain("fixture-replicate", await failed.Content.ReadAsStringAsync());
        Assert.Single(host.Requests);
    }

    [Fact]
    public async Task YourVoic_speech_forwards_typed_options_and_only_safe_response_headers()
    {
        await using var host = await FixtureHost.StartAsync(request =>
        {
            AssertYourVoic(request, "tts/generate");
            Assert.Equal("aura-prime", (string?)request.Body!["model"]);
            Assert.Equal("read this", (string?)request.Body["text"]);
            Assert.Equal("wav", (string?)request.Body["format"]);
            Assert.Equal("en", (string?)request.Body["language"]);
            Assert.Equal(-0.25, (double?)request.Body["pitch"]);
            Assert.Equal("[null,0,false]", request.Body["diagnostic"]!.ToJsonString());
            Assert.Null(request.Body["authorization"]);
            var reply = new HttpResponseMessage(HttpStatusCode.OK) { Content = Bytes("audio/wav") };
            reply.Headers.Add("X-Request-Id", "tts1");
            reply.Headers.Add("Set-Cookie", "private=value");
            return reply;
        }, askCodiReplicateYourVoic: true);
        using var response = await host.PostAsync("/api/speech", """
            {"model":"yourvoic/aura-prime","text":"read this","voice":"voice-a","language":"en","outputFormat":"wave","speed":1.2,
             "providerOptions":{"yourvoic":{"model":"wrong","authorization":"wrong","pitch":-0.25,"diagnostic":[null,0,false]}}}
            """, YourVoic);
        var wire = await GatewayTests.Success(response);
        var result = JsonNode.Parse(wire)!;
        Assert.Equal("wav", (string?)result["audio"]!["format"]);
        Assert.Equal("AQID", (string?)result["audio"]!["base64"]);
        Assert.Contains("tts1", result["providerMetadata"]!["yourvoic"]!.ToJsonString());
        Assert.DoesNotContain("private=value", wire);
    }

    [Theory]
    [InlineData("/v1/audio/speech", """{"model":"yourvoic/rapid-max","input":"hello","stream_format":"sse"}""", "speech.audio.done")]
    [InlineData("/v1/chat/completions", """{"model":"yourvoic/rapid-max","messages":[{"role":"user","content":"hello"}],"modalities":["audio"],"audio":{"format":"mp3","voice":"v"}}""", "AQID")]
    [InlineData("/v1/responses", """{"model":"yourvoic/rapid-max","input":"hello"}""", "AQID")]
    [InlineData("/v1/messages", """{"model":"yourvoic/rapid-max","messages":[{"role":"user","content":"hello"}],"max_tokens":32}""", "AQID")]
    [InlineData("/api/chat", """{"model":"yourvoic/rapid-max","messages":[{"id":"m","role":"user","parts":[{"type":"text","text":"hello"}]}]}""", "AQID")]
    public async Task YourVoic_speech_routes_through_native_and_chat_adapters(string route, string payload, string expected)
    {
        await using var host = await FixtureHost.StartAsync(request =>
        {
            AssertYourVoic(request, "tts/generate");
            Assert.Equal("hello", (string?)request.Body!["text"]);
            return new(HttpStatusCode.OK) { Content = Bytes("audio/mpeg") };
        }, askCodiReplicateYourVoic: true);
        using var response = await host.PostAsync(route, payload, YourVoic);
        Assert.Contains(expected, await GatewayTests.Success(response));
        Assert.Single(host.Requests);
    }

    [Fact]
    public async Task YourVoic_lucid_preserves_multipart_options_and_segment_diagnostics()
    {
        await using var host = await FixtureHost.StartAsync(request =>
        {
            AssertYourVoic(request, "stt/lucid/transcribe");
            Assert.Contains("lucid-multi", request.RawBody);
            Assert.Contains("diarize", request.RawBody);
            Assert.Contains("false", request.RawBody);
            Assert.Contains("{\"nested\":[0,null]}", request.RawBody);
            Assert.DoesNotContain("ignored prompt", request.RawBody);
            return FixtureHost.Json(Transcript);
        }, askCodiReplicateYourVoic: true);
        using var response = await host.PostAsync("/api/transcriptions", """
            {"model":"yourvoic/lucid-multi","audio":"AQID","mediaType":"audio/wav",
             "providerOptions":{"yourvoic":{"diarize":false,"context":{"nested":[0,null]},"prompt":"ignored prompt"}}}
            """, YourVoic);
        var result = await Body(response);
        Assert.Equal("speaker_2: heard this", (string?)result["segments"]![0]!["text"]);
        Assert.Equal(1.5, (double?)result["durationInSeconds"]);
        Assert.Equal("prompt", (string?)result["warnings"]![0]!["feature"]);
        Assert.Equal("[null,0,false]", result["providerMetadata"]!["yourvoic"]!["body"]!["diagnostics"]!["trace"]!.ToJsonString());
    }

    [Theory]
    [InlineData("/v1/chat/completions", """{"model":"yourvoic/lucid-multi","messages":[{"role":"user","content":[{"type":"input_audio","input_audio":{"data":"AQID","format":"wav"}}]}],"yourvoic":{"diarize":true}}""")]
    [InlineData("/v1/responses", """{"model":"yourvoic/lucid-multi","input":[{"role":"user","content":[{"type":"input_file","file_data":"data:audio/wav;base64,AQID","filename":"input.wav"}]}],"yourvoic":{"diarize":true}}""")]
    [InlineData("/v1/messages", """{"model":"yourvoic/lucid-multi","messages":[{"role":"user","content":[{"type":"document","source":{"type":"base64","media_type":"audio/wav","data":"AQID"}}]}],"max_tokens":32,"yourvoic":{"diarize":true}}""")]
    [InlineData("/api/chat", """{"model":"yourvoic/lucid-multi","messages":[{"id":"m","role":"user","parts":[{"type":"file","mediaType":"audio/wav","url":"data:audio/wav;base64,AQID"}]}],"providerMetadata":{"yourvoic":{"diarize":true}}}""")]
    public async Task YourVoic_transcription_routes_inline_audio_with_scoped_options(string route, string payload)
    {
        await using var host = await FixtureHost.StartAsync(request =>
        {
            AssertYourVoic(request, "stt/lucid/transcribe");
            Assert.Contains("diarize", request.RawBody);
            Assert.Contains("true", request.RawBody);
            return FixtureHost.Json(Transcript);
        }, askCodiReplicateYourVoic: true);
        using var response = await host.PostAsync(route, payload, YourVoic);
        var wire = await GatewayTests.Success(response);
        Assert.Contains("heard this", wire);
        Assert.Contains("diagnostics", wire);
        Assert.Single(host.Requests);
    }

    [Theory]
    [InlineData("cipher-fast", "stt/cipher/transcribe", "verbose_json", false, "\"words\"")]
    [InlineData("cipher-max", "stt/cipher/transcribe", "vtt", false, "WEBVTT")]
    [InlineData("new-transcriber", "stt/transcribe", "json", true, "transcript.text.done")]
    public async Task YourVoic_native_transcription_routes_and_preserves_formats(string model, string endpoint, string format, bool stream, string expected)
    {
        await using var host = await FixtureHost.StartAsync(request =>
        {
            AssertYourVoic(request, endpoint);
            Assert.Contains(model, request.RawBody);
            Assert.Contains("word,segment", request.RawBody);
            Assert.Contains("domain terms", request.RawBody);
            return format == "vtt" ? new(HttpStatusCode.OK) { Content = new StringContent("WEBVTT\n", Encoding.UTF8, "text/vtt") }
                : FixtureHost.Json(Transcript);
        }, askCodiReplicateYourVoic: true);
        using var form = new MultipartFormDataContent();
        form.Add(Bytes("audio/wav"), "file", "input.wav");
        form.Add(new StringContent("yourvoic/" + model), "model");
        form.Add(new StringContent(format), "response_format");
        form.Add(new StringContent(stream ? "true" : "false"), "stream");
        form.Add(new StringContent("word"), "timestamp_granularities[]");
        form.Add(new StringContent("segment"), "timestamp_granularities[]");
        form.Add(new StringContent("domain terms"), "prompt");
        using var response = await host.PostFormAsync("/v1/audio/transcriptions", form, YourVoic);
        Assert.Contains(expected, await GatewayTests.Success(response));
    }

    private static void AssertYourVoic(FixtureHost.UpstreamRequest request, string endpoint)
    {
        Assert.Equal(("yourvoic.com", "/api/v1/" + endpoint), (request.Host, request.Path));
        Assert.Equal("fixture-yourvoic", request.Headers["X-API-Key"]);
        Assert.False(request.Headers.ContainsKey("Authorization"));
    }

    private static HttpResponseMessage Asset(FixtureHost.UpstreamRequest request)
    {
        Assert.Equal(("replicate.delivery", "/example.png"), (request.Host, request.Path));
        Assert.Empty(request.Headers);
        return new(HttpStatusCode.OK) { Content = Bytes("image/png") };
    }

    private static ByteArrayContent Bytes(string type) => new([1, 2, 3]) { Headers = { ContentType = new MediaTypeHeaderValue(type) } };
    private static async Task<JsonNode> Body(HttpResponseMessage response) => JsonNode.Parse(await GatewayTests.Success(response))!;
}
