using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace Streaik.Gateway.Tests;

public sealed class AbliterationAIMagicxSpeechmaticsTests
{
    private static readonly (string, string) Abliteration = ("X-Abliteration-Key", "abliteration-fixture");
    private static readonly (string, string) AIMagicx = ("X-AIMagicx-Key", "magic-fixture");
    private static readonly (string, string) Speechmatics = ("X-Speechmatics-Key", "speech-fixture");

    [Fact]
    public async Task Models_are_credential_scoped_and_use_native_ids_and_modalities()
    {
        await using var host = await FixtureHost.StartAsync(request =>
        {
            Assert.Equal("GET", request.Method);
            Assert.Equal(request.Host == "www.aimagicx.com" ? "/api/v1/models" : "/v1/models", request.Path);
            var key = request.Host == "www.aimagicx.com" ? AIMagicx : Abliteration;
            AssertCredential(request, key);
            return FixtureHost.Json("""{"data":[{"id":"vendor/Model","owned_by":"fixture"}]}""");
        }, abliterationAIMagicxSpeechmatics: true);
        using var anonymous = await host.GetAsync("/v1/models");
        Assert.Empty(JsonNode.Parse(await GatewayTests.Success(anonymous))!["data"]!.AsArray());
        Assert.Empty(host.Requests);
        using var response = await host.GetAsync("/v1/models", Abliteration, AIMagicx, Speechmatics);
        var rows = JsonNode.Parse(await GatewayTests.Success(response))!["data"]!.AsArray()
            .ToDictionary(row => (string)row!["id"]!, row => row!);
        Assert.Equal(12, rows.Count);
        Assert.Equal("fixture", (string?)rows["abliteration/vendor/Model"]["owned_by"]);
        Assert.True(rows.ContainsKey("aimagicx/vendor/Model"));
        Assert.Equal("speech", (string?)rows["speechmatics/tts/voice/sarah"]["type"]);
        Assert.Contains("realtime", rows["speechmatics/linden-1"]["tags"]!.AsArray().Select(tag => (string?)tag));
        Assert.Equal(2, host.Requests.Count);
    }

    [Theory]
    [InlineData("/v1/chat/completions", "\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}]", false)]
    [InlineData("/v1/chat/completions", "\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}]", true)]
    [InlineData("/v1/responses", "\"input\":\"hello\"", false)]
    [InlineData("/v1/responses", "\"input\":\"hello\"", true)]
    [InlineData("/v1/messages", "\"max_tokens\":32,\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}]", false)]
    [InlineData("/v1/messages", "\"max_tokens\":32,\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}]", true)]
    public async Task Abliteration_uses_each_native_protocol(string path, string input, bool stream)
    {
        await using var host = await FixtureHost.StartAsync(abliterationAIMagicxSpeechmatics: true);
        using var response = await host.PostAsync(path, $$"""
            {"model":"abliteration/vendor/Model",{{input}},"stream":{{(stream ? "true" : "false")}},
             "abliteration":{"future_request":{"nested":[1,null]},"model":"ignore","authorization":"never-forward"} }
            """, Abliteration, AIMagicx, ("anthropic-beta", "fixture-beta"));
        var output = await GatewayTests.Success(response);
        Assert.Contains("hello", output);
        if (!stream || path == "/v1/chat/completions") Assert.Contains("future_response", output);
        var request = Assert.Single(host.Requests);
        Assert.Equal("api.abliteration.ai", request.Host);
        Assert.Equal(path, request.Path);
        Assert.Equal(stream, request.Stream);
        Assert.Equal("vendor/Model", (string?)request.Body!["model"]);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"nested":[1,null]}"""), request.Body["future_request"]));
        Assert.Null(request.Body["abliteration"]);
        Assert.Null(request.Body["authorization"]);
        AssertCredential(request, Abliteration);
        if (path == "/v1/messages")
        {
            Assert.Equal("2023-06-01", request.Headers["anthropic-version"]);
            Assert.Equal("fixture-beta", request.Headers["anthropic-beta"]);
        }
        if (stream && path == "/v1/chat/completions") Assert.True((bool)request.Body["stream_options"]!["include_usage"]!);
    }

    [Theory]
    [InlineData("abliteration", "/v1/responses")]
    [InlineData("aimagicx", "/api/v1/chat/completions")]
    public async Task Vercel_reuses_protocol_adapters_and_passes_provider_options(string provider, string path)
    {
        await using var host = await FixtureHost.StartAsync(request =>
            FixtureHost.Reply(request with { Path = request.Path.Replace("/api/v1/", "/v1/", StringComparison.Ordinal) }),
            abliterationAIMagicxSpeechmatics: true);
        using var response = await host.PostAsync("/api/chat", $$"""
            {"model":"{{provider}}/model","messages":[{"id":"u","role":"user","parts":[{"type":"text","text":"hello"}]}],
             "providerMetadata":{"{{provider}}":{"future_request":{"values":[true,42,null]} },"another":{"unrelated":true} } }
            """, Abliteration, AIMagicx);
        var output = await GatewayTests.Success(response);
        Assert.Contains("text-delta", output);
        Assert.Contains("hello", output);
        Assert.Contains("future_response", output);
        var request = Assert.Single(host.Requests);
        Assert.Equal(path, request.Path);
        Assert.True(request.Stream);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"values":[true,42,null]}"""), request.Body!["future_request"]));
        Assert.Null(request.Body["another"]);
        AssertCredential(request, provider == "aimagicx" ? AIMagicx : Abliteration);
    }

    [Theory]
    [InlineData("/v1/chat/completions", "\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}]")]
    [InlineData("/v1/responses", "\"input\":\"hello\"")]
    [InlineData("/v1/messages", "\"max_tokens\":32,\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}]")]
    public async Task AIMagicx_translates_without_an_upstream_responses_or_messages_endpoint(string path, string input)
    {
        await using var host = await FixtureHost.StartAsync(request => FixtureHost.Reply(request with { Path = "/v1/chat/completions" }),
            abliterationAIMagicxSpeechmatics: true);
        using var response = await host.PostAsync(path, $$"""
            {"model":"aimagicx/model",{{input}},"aimagicx":{"future_request":[1,null]} }
            """, AIMagicx);
        var output = await GatewayTests.Success(response);
        Assert.Contains("hello", output);
        Assert.Contains("future_response", output);
        var request = Assert.Single(host.Requests);
        Assert.Equal("www.aimagicx.com", request.Host);
        Assert.Equal("/api/v1/chat/completions", request.Path);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("[1,null]"), request.Body!["future_request"]));
        AssertCredential(request, AIMagicx);
    }

    [Fact]
    public async Task Speechmatics_streams_pcm_and_wav_with_provider_options_and_safe_response_metadata()
    {
        await using var host = await FixtureHost.StartAsync(request =>
        {
            Assert.Equal("preview.tts.speechmatics.com", request.Host);
            AssertCredential(request, Speechmatics);
            Assert.Equal("hello", (string?)request.Body!["text"]);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
            response.Content.Headers.ContentType = new("audio/wav");
            response.Headers.Add("X-Request-Id", "speech-id");
            response.Headers.TryAddWithoutValidation("Set-Cookie", "hidden=cookie");
            return response;
        }, abliterationAIMagicxSpeechmatics: true);
        using var speech = await host.PostAsync("/api/speech", """
            {"model":"speechmatics/tts","text":"hello","providerOptions":{"speechmatics":{"voice":"megan","future_request":{"n":[1,null]}}}}
            """, Speechmatics);
        var result = JsonNode.Parse(await GatewayTests.Success(speech))!;
        Assert.Equal("AQID", (string?)result["audio"]!["base64"]);
        var headers = result["providerMetadata"]!["speechmatics"]!["headers"]!.AsObject();
        Assert.Equal("speech-id", (string?)Assert.Single(headers, pair => pair.Key.Equals("X-Request-Id", StringComparison.OrdinalIgnoreCase)).Value);
        Assert.DoesNotContain(headers, pair => pair.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase));
        using var streaming = await host.PostAsync("/api/speech/stream", """
            {"model":"speechmatics/tts/voice/sarah","text":[{"text":"hel"},{"text":"lo"}],"includeRawChunks":true,
             "outputFormat":{"type":"pcm","rate":16000},"providerOptions":{"unrelated":{"private":true}}}
            """, Speechmatics);
        var frames = await GatewayTests.Success(streaming);
        Assert.Contains("audio/pcm", frames);
        Assert.Contains("AQID", frames);
        Assert.Contains("speech-id", frames);
        Assert.Contains("\"type\":\"raw\"", frames);
        using var native = await host.PostAsync("/v1/audio/speech", """
            {"model":"speechmatics/tts/voice/jack","input":"hello","response_format":"wav","stream_format":"sse"}
            """, Speechmatics);
        Assert.Contains("speech.audio.done", await GatewayTests.Success(native));
        var requests = host.Requests.ToArray();
        Assert.Equal("/generate/megan?output_format=wav_16000", requests[0].Path);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"n":[1,null]}"""), requests[0].Body!["future_request"]));
        Assert.Equal("/generate/sarah?output_format=pcm_16000", requests[1].Path);
        Assert.Null(requests[1].Body!["unrelated"]);
        Assert.Equal("/generate/jack?output_format=wav_16000", requests[2].Path);
    }

    [Fact]
    public async Task Speechmatics_polls_batch_transcription_and_retains_unmodified_transcript_metadata()
    {
        var polls = 0;
        await using var host = await FixtureHost.StartAsync(request =>
        {
            Assert.Equal("eu1.asr.api.speechmatics.com", request.Host);
            AssertCredential(request, Speechmatics);
            return request.Path switch
            {
                "/v2/jobs" => FixtureHost.Json("""{"id":"batch job"}"""),
                "/v2/jobs/batch%20job" when ++polls == 1 => FixtureHost.Json("""{"job":{"status":"running"}}"""),
                "/v2/jobs/batch%20job" => FixtureHost.Json("""{"job":{"status":"done","duration":1.5}}"""),
                "/v2/jobs/batch%20job/transcript?format=json-v2" => FixtureHost.Json(Transcript),
                _ => throw new InvalidOperationException(request.Path)
            };
        }, abliterationAIMagicxSpeechmatics: true);
        using var response = await host.PostAsync("/api/transcriptions", """
            {"model":"speechmatics/oak-1","audio":"AQID","mediaType":"audio/wav",
             "providerOptions":{"speechmatics":{"language":"en","diarization":"speaker","future":[1,null]}}}
            """, Speechmatics);
        var result = JsonNode.Parse(await GatewayTests.Success(response))!;
        Assert.Equal(2, polls);
        Assert.Equal("Hello!", (string?)result["text"]);
        Assert.Equal("multi", (string?)result["language"]);
        Assert.Equal(1.5, (double?)result["durationInSeconds"]);
        Assert.Equal(2, result["segments"]!.AsArray().Count);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Transcript), result["providerMetadata"]!["speechmatics"]));
        var submission = Assert.Single(host.Requests, request => request.Method == "POST");
        Assert.Contains("name=data_file", submission.RawBody);
        Assert.Contains("\"language\":\"multi\"", submission.RawBody);
        Assert.Contains("\"model\":\"oak-1\"", submission.RawBody);
        Assert.Contains("\"future\":[1,null]", submission.RawBody);
    }

    [Theory]
    [InlineData("json", false, "application/json", "Hello!")]
    [InlineData("verbose_json", false, "application/json", "segments")]
    [InlineData("text", false, "text/plain", "Hello!")]
    [InlineData("json", true, "text/event-stream", "transcript.text.done")]
    public async Task Speechmatics_formats_openai_transcripts(string format, bool stream, string contentType, string expected)
    {
        await using var host = await FixtureHost.StartAsync(request => request.Path switch
        {
            "/v2/jobs" => FixtureHost.Json("""{"job":{"id":"job"}}"""),
            "/v2/jobs/job" => FixtureHost.Json("""{"status":"done"}"""),
            "/v2/jobs/job/transcript?format=json-v2" => FixtureHost.Json(Transcript),
            _ => throw new InvalidOperationException(request.Path)
        }, abliterationAIMagicxSpeechmatics: true);
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("speechmatics/enhanced"), "model");
        using var audio = new ByteArrayContent([1, 2, 3]);
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(audio, "file", "audio.wav");
        form.Add(new StringContent(format), "response_format");
        if (stream) form.Add(new StringContent("true"), "stream");
        using var response = await host.PostFormAsync("/v1/audio/transcriptions", form, Speechmatics);
        Assert.Contains(expected, await GatewayTests.Success(response));
        Assert.Equal(contentType, response.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData("/v1/chat/completions", "\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}]", "\"audio\"")]
    [InlineData("/v1/responses", "\"input\":\"hello\"", "input_file")]
    [InlineData("/v1/messages", "\"max_tokens\":32,\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}]", "document")]
    [InlineData("/api/chat", "\"messages\":[{\"id\":\"u\",\"role\":\"user\",\"parts\":[{\"type\":\"text\",\"text\":\"hello\"}]}]", "\"type\":\"file\"")]
    public async Task Speechmatics_speech_reuses_each_existing_chat_audio_adapter(string path, string input, string marker)
    {
        await using var host = await FixtureHost.StartAsync(request =>
        {
            Assert.Equal("/generate/theo?output_format=wav_16000", request.Path);
            Assert.Equal("hello", (string?)request.Body!["text"]);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
            response.Content.Headers.ContentType = new("audio/wav");
            return response;
        }, abliterationAIMagicxSpeechmatics: true);
        using var response = await host.PostAsync(path, $$"""{"model":"speechmatics/tts/voice/theo",{{input}} }""", Speechmatics);
        var output = await GatewayTests.Success(response);
        Assert.Contains("AQID", output);
        Assert.Contains(marker, output);
        Assert.Single(host.Requests);
    }

    [Fact]
    public async Task Speechmatics_does_not_fetch_transcript_after_a_rejected_job_or_submit_agent_audio()
    {
        await using var host = await FixtureHost.StartAsync(request => request.Path switch
        {
            "/v2/jobs" => FixtureHost.Json("""{"id":"job"}"""),
            "/v2/jobs/job" => FixtureHost.Json("""{"job":{"status":"rejected"}}"""),
            _ => throw new InvalidOperationException(request.Path)
        }, abliterationAIMagicxSpeechmatics: true);
        const string transcript = "/api/transcriptions";
        using var failed = await host.PostAsync(transcript,
            """{"model":"speechmatics/standard","audio":"AQID","mediaType":"audio/wav"}""", Speechmatics);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, failed.StatusCode);
        using var agent = await host.PostAsync(transcript,
            """{"model":"speechmatics/linden-1","audio":"AQID","mediaType":"audio/wav"}""", Speechmatics);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, agent.StatusCode);
        Assert.Equal(2, host.Requests.Count);
    }

    [Fact]
    public async Task Speechmatics_mints_temporary_realtime_tokens_for_valid_models_and_regions()
    {
        await using var host = await FixtureHost.StartAsync(request =>
        {
            Assert.Equal("mp.speechmatics.com", request.Host);
            Assert.Equal("/v1/jwts?type=rt", request.Path);
            Assert.Equal(120, (int?)request.Body!["ttl"]);
            AssertCredential(request, Speechmatics);
            return FixtureHost.Json("""{"key_value":"fixture-jwt"}""");
        }, abliterationAIMagicxSpeechmatics: true);
        const string endpoint = "/api/realtime/token/speechmatics/linden-1";
        using var response = await host.PostAsync(endpoint, """
            {"sessionConfig":{"providerOptions":{"speechmatics":{"region":"eu","ttl":120}}}}
            """, Speechmatics);
        var result = JsonNode.Parse(await GatewayTests.Success(response))!;
        Assert.Equal("fixture-jwt", (string?)result["token"]);
        Assert.Equal("wss://eu.rt.speechmatics.com/v2/agent", (string?)result["url"]);
        Assert.True((long?)result["expiresAt"] > DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var invalid = await host.PostAsync(endpoint, """
            {"sessionConfig":{"providerOptions":{"speechmatics":{"region":"foreign.example"}}}}
            """, Speechmatics);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Single(host.Requests);
    }

    private static void AssertCredential(FixtureHost.UpstreamRequest request, (string Header, string Key) credentials)
    {
        Assert.Equal("Bearer " + credentials.Key, request.Headers["Authorization"]);
        Assert.DoesNotContain(request.Headers.Keys, name => name.StartsWith("X-", StringComparison.OrdinalIgnoreCase)
            && name.EndsWith("-Key", StringComparison.OrdinalIgnoreCase));
    }

    private const string Transcript = """
        {"metadata":{"transcription_config":{"language":"multi"}},"results":[
        {"type":"word","start_time":0,"end_time":1,"alternatives":[{"content":"Hello","speaker":"S1"}]},
        {"type":"punctuation","attaches_to":"previous","start_time":1,"end_time":1.1,"alternatives":[{"content":"!"}]}],"future":{"n":[1,null]}}
        """;
}
