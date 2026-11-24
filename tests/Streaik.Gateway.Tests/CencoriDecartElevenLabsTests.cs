using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Streaik.Gateway.Tests.FixtureHost;

namespace Streaik.Gateway.Tests;

public class CencoriDecartElevenLabsTests
{
    private static readonly (string, string) Cencori = ("X-Cencori-Key", "cencori-fixture");
    private static readonly (string, string) Decart = ("X-Decart-Key", "decart-fixture");
    private static readonly (string, string) ElevenLabs = ("X-ElevenLabs-Key", "eleven-fixture");
    private const string Chat = """{"id":"cmpl_fixture","object":"chat.completion","model":"native","created":1700000000,"choices":[{"index":0,"message":{"role":"assistant","content":"hello"},"finish_reason":"stop"}],"upstream":{"score":[2,true]}}""";
    private const string Result = """{"id":"resp_fixture","object":"response","model":"native","created_at":1700000000,"status":"completed","output":[{"id":"msg_fixture","type":"message","role":"assistant","status":"completed","content":[{"type":"output_text","text":"hello","annotations":[]}]}],"upstream":{"score":[2,true]}}""";

    [Fact]
    public async Task Discovery_uses_scoped_keys_and_lists_native_model_types()
    {
        await using var host = await StartAsync(request => request.Host switch
        {
            "api.cencori.com" => Json("""{"data":[{"id":"upstream/model","name":"Native","description":"Details","context_window":128000}]}"""),
            "api.elevenlabs.io" => Json("""[{"model_id":"eleven_v3","name":"v3","can_do_text_to_speech":true,"can_do_text_to_dialogue":true}]"""),
            _ => throw new InvalidOperationException(request.Host)
        }, cencoriDecartElevenLabs: true);
        using var response = await host.GetAsync("/v1/models", Cencori, Decart, ElevenLabs);
        var models = (await Read(response))["data"]!.AsArray().ToDictionary(x => (string)x!["id"]!);
        Assert.Equal("Details", (string?)models["cencori/upstream/model"]!["description"]);
        Assert.Equal("language", (string?)models["cencori/upstream/model"]!["type"]);
        Assert.Equal("image", (string?)models["decart/lucy-image-2"]!["type"]);
        Assert.Equal("video", (string?)models["decart/lucy-2.5"]!["type"]);
        Assert.Equal("speech", (string?)models["elevenlabs/eleven_v3/text-to-dialogue"]!["type"]);
        Assert.Equal("transcription", (string?)models["elevenlabs/scribe_v2_realtime"]!["type"]);
        Assert.Equal("Bearer cencori-fixture", host.Requests.Single(x => x.Host == "api.cencori.com").Headers["Authorization"]);
        Assert.Equal("eleven-fixture", host.Requests.Single(x => x.Host == "api.elevenlabs.io").Headers["xi-api-key"]);
        Assert.All(host.Requests, request => Assert.Equal("/v1/models", request.Path));
    }

    [Fact]
    public async Task Cencori_native_chat_and_responses_preserve_extensions_and_adapt_messages_and_vercel()
    {
        await using var host = await StartAsync(request => (request.Path, request.Stream) switch
        {
            ("/v1/chat/completions", false) => Json(Chat),
            ("/v1/chat/completions", true) => Sse("""{"id":"chunk","object":"chat.completion.chunk","created":1700000000,"model":"native","choices":[{"index":0,"delta":{"content":"hello"},"finish_reason":"stop"}],"upstream":{"stream":true}}""", "[DONE]"),
            ("/v1/responses", false) => Json(Result),
            ("/v1/responses", true) => Sse($$"""{"type":"response.completed","response":{{Result}}}"""),
            _ => throw new InvalidOperationException(request.Path)
        }, cencoriDecartElevenLabs: true);
        using var chat = await host.PostAsync("/v1/chat/completions", """{"model":"cencori/native","messages":[{"role":"user","content":"hi"}],"options":{"nested":[true,3]}}""", Cencori, Decart);
        Assert.Equal(2, (int?)(await Read(chat))["upstream"]!["score"]![0]);
        var upstream = host.Requests.Single();
        Assert.Equal("native", (string?)upstream.Body!["model"]);
        Assert.True((bool?)upstream.Body["options"]!["nested"]![0]);
        Assert.Equal("Bearer cencori-fixture", upstream.Headers["Authorization"]);
        Assert.False(upstream.Headers.ContainsKey(Decart.Item1));
        using var chatStream = await host.PostAsync("/v1/chat/completions", """{"model":"cencori/native","messages":[],"stream":true}""", (Cencori.Item1, "other-key"));
        Assert.Contains("\"upstream\":{\"stream\":true}", await GatewayTests.Success(chatStream));
        Assert.Equal("Bearer other-key", host.Requests.Last().Headers["Authorization"]);
        using var response = await host.PostAsync("/v1/responses", """{"model":"cencori/native","input":"hi","options":{"raw":[1,false]}}""", Cencori);
        Assert.Equal(2, (int?)(await Read(response))["upstream"]!["score"]![0]);
        Assert.False((bool?)host.Requests.Last().Body!["options"]!["raw"]![1]);
        using var messages = await host.PostAsync("/v1/messages", """{"model":"cencori/native","max_tokens":30,"messages":[{"role":"user","content":"hi"}]}""", Cencori);
        Assert.Equal("hello", (string?)(await Read(messages))["content"]![0]!["text"]);
        using var vercel = await host.PostAsync("/api/chat", """
            {"id":"turn","model":"cencori/native","messages":[{"id":"u","role":"user","parts":[{"type":"text","text":"hi"}]}],
             "providerMetadata":{"cencori":{"model":"override","Store":true,"options":{"raw":[true,7]}},"decart":{"private":true}}}
            """, Cencori);
        var ui = await GatewayTests.Success(vercel);
        Assert.Contains("\"score\":[2,true]", ui);
        Assert.Equal("native", (string?)host.Requests.Last().Body!["model"]);
        Assert.Equal(7, (int?)host.Requests.Last().Body!["options"]!["raw"]![1]);
        Assert.Null(host.Requests.Last().Body!["Store"]);
        Assert.Null(host.Requests.Last().Body!["private"]);
        using var missing = await host.PostAsync("/v1/chat/completions", """{"model":"cencori/native","messages":[]}""", Decart);
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
    }

    [Fact]
    public async Task Decart_image_edit_forwards_multipart_and_reuses_gateway_stream_framing()
    {
        await using var host = await StartAsync(_ => Binary("image/png"), cencoriDecartElevenLabs: true);
        using var edit = await host.PostAsync("/api/images", """
            {"model":"decart/lucy-image-2","prompt":"edit","size":"480x832","seed":17,"n":2,
             "files":[{"type":"file","mediaType":"image/png","data":"AQID"}],
             "providerOptions":{"decart":{"enhance_prompt":true,"future":{"a":[1,false]},"data":"not-file"}}}
            """, Decart);
        var result = await Read(edit);
        Assert.Equal("data:image/png;base64,AQID", (string?)result["images"]![0]);
        Assert.Equal("n", (string?)result["warnings"]![0]!["feature"]);
        var request = host.Requests.Single();
        Assert.Equal("/v1/generate/lucy-image-2", request.Path);
        Assert.Equal("decart-fixture", request.Headers["X-API-KEY"]);
        Assert.Contains("480p", request.RawBody);
        Assert.Contains("17", request.RawBody);
        Assert.Contains("{\"a\":[1,false]}", request.RawBody);
        Assert.DoesNotContain("not-file", request.RawBody);

        using var streamed = await host.PostAsync("/v1/images/edits", """
            {"model":"decart/lucy-image-latest","prompt":"edit","stream":true,"images":[{"image_url":"data:image/png;base64,AQID"}]}
            """, Decart);
        Assert.Contains("image_edit.completed", await GatewayTests.Success(streamed));
        using var unsupported = await host.PostAsync("/v1/images/generations", """{"model":"decart/lucy-image-2","prompt":"generate"}""", Decart);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unsupported.StatusCode);
        Assert.Equal(2, host.Requests.Count);
    }

    [Fact]
    public async Task Decart_video_uses_protected_job_reference_and_returns_raw_metadata_and_content()
    {
        int polls = 0;
        await using var host = await StartAsync(request => request.Path switch
        {
            "/v1/jobs/lucy-2.5" => Json("""{"job_id":"job-42","queue":{"priority":3}}"""),
            "/v1/jobs/job-42" => ++polls switch
            {
                1 => Json("""{"status":"processing","progress":{"fraction":0.5}}"""),
                2 => Json("""{"status":"completed","attributes":{"seed":42}}"""),
                _ => Json("""{"status":"failed","error":{"code":"test_failure"}}""")
            },
            "/v1/jobs/job-42/content" => Binary("video/mp4"),
            _ => throw new InvalidOperationException(request.Path)
        }, configure: services => services.AddStreaikReferences(), cencoriDecartElevenLabs: true);
        using var start = await host.PostAsync("/api/videos", """
            {"model":"decart/lucy-2.5","resolution":"480p","seed":42,
             "inputReferences":[{"type":"file","mediaType":"video/mp4","data":"AQID"},{"type":"file","mediaType":"image/png","data":"AQID"}],
             "providerOptions":{"decart":{"self_anchor":true,"prompt":"ignored","future":[{"x":1}]}}}
            """, Decart);
        var started = await Read(start);
        var operation = (string)started["operation"]!;
        Assert.StartsWith("decart/", operation);
        Assert.DoesNotContain("job-42", operation);
        Assert.Equal(3, (int?)started["providerMetadata"]!["decart"]!["queue"]!["priority"]);
        Assert.Contains("reference_image", host.Requests.Single().RawBody);
        Assert.Contains("[{\"x\":1}]", host.Requests.Single().RawBody);
        Assert.DoesNotContain("ignored", host.Requests.Single().RawBody);
        using var pending = await host.GetAsync("/api/videos/" + operation, Decart);
        Assert.Equal(0.5, (double?)(await Read(pending))["providerMetadata"]!["decart"]!["progress"]!["fraction"]);
        using var complete = await host.GetAsync("/api/videos/" + operation, Decart);
        var completed = await Read(complete);
        Assert.Equal("AQID", (string?)completed["videos"]![0]!["data"]);
        Assert.Equal("decart/lucy-2.5", (string?)completed["response"]!["modelId"]);
        using var failure = await host.GetAsync("/api/videos/" + operation, Decart);
        Assert.Equal("test_failure", (string?)(await Read(failure))["providerMetadata"]!["decart"]!["error"]!["code"]);
        using var tampered = await host.GetAsync("/api/videos/decart/job-42", Decart);
        Assert.Equal(HttpStatusCode.NotFound, tampered.StatusCode);
        Assert.Equal(5, host.Requests.Count);
    }

    [Theory]
    [InlineData("eleven_v3", "/v1/text-to-speech/voice-1?output_format=mp3_44100_128", "text", "hello")]
    [InlineData("eleven_v3/text-to-dialogue", "/v1/text-to-dialogue?output_format=mp3_44100_128", "inputs", null)]
    [InlineData("eleven_text_to_sound_v2", "/v1/sound-generation?output_format=mp3_44100_128", "text", "hello")]
    [InlineData("music_v2", "/v1/music?output_format=auto", "prompt", "hello")]
    public async Task ElevenLabs_speech_routes_preserve_native_options(string model, string path, string field, string? text)
    {
        await using var host = await StartAsync(_ => Binary("audio/mpeg"), cencoriDecartElevenLabs: true);
        using var response = await host.PostAsync("/v1/audio/speech", $$$"""
            {"model":"elevenlabs/{{{model}}}","input":"hello","voice":"voice-1", "model_id":"not-model",
             "inputs":[{"text":"line","voice_id":"voice-2"}], "future":{"settings":[1,true]}}
            """, ElevenLabs);
        Assert.Equal(new byte[] { 1, 2, 3 }, await response.Content.ReadAsByteArrayAsync());
        var upstream = host.Requests.Single();
        Assert.Equal(path, upstream.Path);
        Assert.Equal("eleven-fixture", upstream.Headers["xi-api-key"]);
        Assert.Equal(model.Split('/')[0], (string?)upstream.Body!["model_id"]);
        Assert.True((bool?)upstream.Body["future"]!["settings"]![1]);
        Assert.Equal(text ?? "line", text is null ? (string?)upstream.Body[field]![0]!["text"] : (string?)upstream.Body[field]);
    }

    [Fact]
    public async Task ElevenLabs_stream_keeps_audio_and_alignment_in_existing_contract_fields()
    {
        await using var host = await StartAsync(request => request.Path.Contains("with-timestamps", StringComparison.Ordinal)
            ? Sse("""{"audio_base64":"AQID","alignment":{"characters":["a"]},"future":{"flag":true}}""", "[DONE]")
            : Binary("audio/mpeg"), cencoriDecartElevenLabs: true);
        using var stream = await host.PostAsync("/api/speech/stream", """
            {"model":"elevenlabs/eleven_v3","voice":"voice-1","text":[{"text":"abc"}],"speed":1.2,
             "providerOptions":{"elevenlabs":{"with_timestamps":true,"voice_settings":{"speed":0.8,"style":0.5}}}}
            """, ElevenLabs);
        var body = await GatewayTests.Success(stream);
        Assert.Contains("\"audio\":\"AQID\"", body);
        Assert.Contains("\"alignment\":{\"characters\":[\"a\"]}", body);
        Assert.Contains("\"future\":{\"flag\":true}", body);
        Assert.DoesNotContain("audio_base64", body);
        Assert.Equal(1.2, (double?)host.Requests.Single().Body!["voice_settings"]!["speed"]);

        using var standard = await host.PostAsync("/v1/audio/speech", """{"model":"elevenlabs/music_v1","input":"song","stream_format":"sse"}""", ElevenLabs);
        var events = await GatewayTests.Success(standard);
        Assert.Contains("speech.audio.delta", events);
        Assert.Contains("speech.audio.done", events);
        Assert.StartsWith("/v1/music/stream?", host.Requests.Last().Path);
    }

    [Fact]
    public async Task ElevenLabs_keeps_inline_alignment_and_handles_detailed_multipart_music()
    {
        await using var host = await StartAsync(request => request.Path.Contains("/music/detailed/stream", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = DetailedMusic() }
            : Json("""{"audio_base64":"AQID","alignment":{"characters":["a"]}}"""), cencoriDecartElevenLabs: true);
        using var speech = await host.PostAsync("/api/speech", """
            {"model":"elevenlabs/eleven_v3","voice":"voice-1","text":"a",
             "providerOptions":{"elevenlabs":{"with_timestamps":true}}}
            """, ElevenLabs);
        var result = await Read(speech);
        Assert.Equal("a", (string?)result["providerMetadata"]!["elevenlabs"]!["alignment"]!["characters"]![0]);
        Assert.Null(result["providerMetadata"]!["elevenlabs"]!["audio_base64"]);

        using var music = await host.PostAsync("/api/speech/stream", """
            {"model":"elevenlabs/music_v2","text":[{"text":"song"}],
             "providerOptions":{"elevenlabs":{"detailed_stream":true,"with_waveform_visual":true}}}
            """, ElevenLabs);
        var body = await GatewayTests.Success(music);
        Assert.Contains("\"audio\":\"AQID\"", body);
        Assert.Contains("\"waveform\":[0.1,0.2]", body);
        Assert.True((bool?)host.Requests.Last().Body!["with_waveform_visual"]);

        static HttpContent DetailedMusic()
        {
            var form = new MultipartContent("mixed");
            form.Add(new StringContent("""{"waveform":[0.1,0.2]}""", Encoding.UTF8, "application/json"));
            var audio = new ByteArrayContent([1, 2, 3]);
            audio.Headers.ContentType = new MediaTypeHeaderValue("audio/mpeg");
            form.Add(audio);
            return form;
        }
    }

    [Fact]
    public async Task ElevenLabs_transcription_preserves_raw_metadata_and_mints_realtime_token_without_leaking_key()
    {
        await using var host = await StartAsync(request => request.Path.StartsWith("/v1/speech-to-text", StringComparison.Ordinal)
            ? Json("""{"text":"hello","language_code":"en","duration":0.5,"words":[{"text":"hello","start":0.1,"end":0.5,"speaker_id":"a"}],"new_field":{"nested":[true,4]}}""")
            : Json("""{"token":"temporary"}"""), cencoriDecartElevenLabs: true);
        using var transcription = await host.PostAsync("/api/transcriptions", """
            {"model":"elevenlabs/scribe_v2","audio":"AQID","mediaType":"audio/wav",
             "providerOptions":{"elevenlabs":{"enable_logging":false,"language":"en","model_id":"override","future":{"list":[2]}}}}
            """, ElevenLabs);
        var result = await Read(transcription);
        Assert.Equal("hello", (string?)result["text"]);
        Assert.Equal(4, (int?)result["providerMetadata"]!["elevenlabs"]!["new_field"]!["nested"]![1]);
        var request = host.Requests.Single();
        Assert.Equal("/v1/speech-to-text?enable_logging=false", request.Path);
        Assert.Contains("scribe_v2", request.RawBody);
        Assert.DoesNotContain("override", request.RawBody);
        Assert.Contains("{\"list\":[2]}", request.RawBody);

        using var form = new MultipartFormDataContent { { new StringContent("elevenlabs/scribe_v1"), "model" }, { new StringContent("true"), "stream" } };
        var audio = new ByteArrayContent([1, 2, 3]);
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(audio, "file", "clip.wav");
        using var native = await host.PostFormAsync("/v1/audio/transcriptions", form, ElevenLabs);
        Assert.Contains("transcript.text.done", await GatewayTests.Success(native));

        using var token = await host.PostAsync("/api/realtime/token/elevenlabs/scribe_v2_realtime", "{}", ElevenLabs);
        Assert.Contains("no-store", token.Headers.CacheControl!.ToString());
        var lease = await Read(token);
        Assert.Equal("temporary", (string?)lease["token"]);
        Assert.StartsWith("wss://api.elevenlabs.io/v1/speech-to-text/realtime?model_id=scribe_v2_realtime", (string?)lease["url"]);
        Assert.DoesNotContain("eleven-fixture", lease.ToJsonString());
        Assert.Equal("/v1/single-use-token/realtime_scribe", host.Requests.Last().Path);
    }

    [Fact]
    public async Task Cancellation_reaches_the_provider_before_speech_stream_headers()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var aborted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = await StartAsync(configure: services => services.AddHttpClient("elevenlabs")
            .ConfigurePrimaryHttpMessageHandler(() => new WaitForCancellationHandler(started, aborted)), cencoriDecartElevenLabs: true);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/speech/stream")
        {
            Content = new StringContent("""{"model":"elevenlabs/eleven_v3","voice":"ok","text":[{"text":"hello"}]}""", Encoding.UTF8, "application/json")
        };
        request.Headers.Add(ElevenLabs.Item1, ElevenLabs.Item2);
        var response = host.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation.Token);
        await started.Task.WaitAsync(cancellation.Token);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await response);
        await aborted.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static async Task<JsonNode> Read(HttpResponseMessage response) => JsonNode.Parse(await GatewayTests.Success(response))!;

    private static HttpResponseMessage Binary(string contentType)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return response;
    }

    private sealed class WaitForCancellationHandler(TaskCompletionSource started, TaskCompletionSource aborted) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var registration = cancellationToken.Register(() => aborted.TrySetResult());
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Expected request cancellation.");
        }
    }
}
