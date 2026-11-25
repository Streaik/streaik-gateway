using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Streaik.Gateway.Tests;

public sealed class AichixiaEdgeSpeakIsaacusTests
{
    private const string Chat = "/v1/chat/completions";
    private const string Messages = "/v1/messages";
    private const string Responses = "/v1/responses";
    private const string Vercel = "/api/chat";
    private const string EdgeModels = """
        {"data":[{"id":"vendor/reader","name":"Reader","supported_endpoints":["/v1/audio/speech"]},
        {"id":"vendor/listener","supported_endpoints":["/v1/audio/transcriptions"]},
        {"id":"vendor/model","supported_endpoints":["/v1/chat/completions","/v1/messages"]}]}
        """;
    private static readonly (string, string) AichixiaKey = ("X-Aichixia-Key", "chat-key");
    private static readonly (string, string) IsaacusKey = ("X-Isaacus-Key", "legal-key");

    [Fact]
    public async Task Models_keep_native_slashes_and_classify_edge_audio()
    {
        await using var host = await Start(request => request.Host == "edgespeak.test" ? FixtureHost.Json(EdgeModels)
            : FixtureHost.Json("""{"data":[{"id":"vendor/model","context_window":65536,"owned_by":"vendor","description":"Text"}]}"""));
        using var response = await host.GetAsync("/v1/models", AichixiaKey);
        var data = (await Body(response))["data"]!.AsArray().ToDictionary(item => (string)item!["id"]!);
        Assert.Equal(65536, (int?)data["aichixia/vendor/model"]!["context_window"]);
        Assert.Equal("speech", (string?)data["edgespeak/vendor/reader"]!["type"]);
        Assert.Equal("transcription", (string?)data["edgespeak/vendor/listener"]!["type"]);
        Assert.Equal("embedding", (string?)data["isaacus/kanon-2-embedder"]!["type"]);
        Assert.Equal("reranking", (string?)data["isaacus/kanon-universal-classifier"]!["type"]);
        Assert.Contains(host.Requests, request => request is { Host: "www.aichixia.xyz", Path: "/api/v1/models" }
            && request.Headers["Authorization"] == "Bearer chat-key");
        Assert.Contains(host.Requests, request => request is { Host: "edgespeak.test", Path: "/local/v1/models" }
            && !request.Headers.ContainsKey("Authorization"));
    }

    [Theory]
    [InlineData("aichixia", Chat, false, Chat, "future_response")]
    [InlineData("aichixia", Chat, true, Chat, "[DONE]")]
    [InlineData("aichixia", Messages, false, Messages, "future_content")]
    [InlineData("aichixia", Messages, true, Messages, "message_stop")]
    [InlineData("aichixia", Responses, false, Chat, "future_response")]
    [InlineData("aichixia", Vercel, true, Chat, "future_response")]
    [InlineData("edgespeak", Chat, true, Chat, "[DONE]")]
    [InlineData("edgespeak", Responses, false, Responses, "future_response")]
    [InlineData("edgespeak", Responses, true, Responses, "response.completed")]
    [InlineData("edgespeak", Messages, true, Messages, "message_stop")]
    [InlineData("edgespeak", Vercel, true, Chat, "future_response")]
    public async Task Text_protocols_use_native_endpoints_or_existing_chat_bridge(string provider, string route, bool stream, string path, string marker)
    {
        await using var host = await Start();
        var payload = GatewayTests.Request(route, $"{provider}/vendor/model", stream);
        var options = JsonNode.Parse("""{"future_option":{"levels":[2,{"enabled":true},null]},"MODEL":"wrong"}""");
        if (route == Vercel) payload["providerMetadata"] = new JsonObject { [provider] = options };
        else payload[provider] = options;
        using var response = await host.PostAsync(route, payload.ToJsonString(), AichixiaKey, ("anthropic-beta", "fixture-beta"));
        var body = await GatewayTests.Success(response);
        Assert.Contains("hello", body);
        Assert.Contains(marker, body);
        var sent = Assert.Single(host.Requests, request => request.Method == "POST");
        Assert.Equal((provider == "edgespeak" ? "/local" : "/api") + path, sent.Path);
        Assert.Equal("vendor/model", (string?)sent.Body!["model"]);
        Assert.Null(sent.Body["MODEL"]);
        Assert.True((bool)sent.Body["future_option"]!["levels"]![1]!["enabled"]!);
        if (provider == "aichixia") Assert.Equal("Bearer chat-key", sent.Headers["Authorization"]);
        else Assert.False(sent.Headers.ContainsKey("Authorization"));
        if (route == Messages) Assert.Equal("fixture-beta", sent.Headers["anthropic-beta"]);
        if (provider == "edgespeak" && route == Messages) Assert.Equal("2023-06-01", sent.Headers["anthropic-version"]);
    }

    [Fact]
    public async Task Isaacus_batches_in_order_and_keeps_each_raw_response()
    {
        await using var host = await Start(IsaacusReply);
        var payload = JsonSerializer.Serialize(new { model = "isaacus/kanon-2-embedder", values = Enumerable.Range(0, 129).Select(n => n.ToString()),
            providerOptions = new { isaacus = new { dimensions = (int?)null, task = "retrieval", native = new { nested = true } } } });
        using var response = await host.PostAsync("/api/embeddings", payload, IsaacusKey);
        var result = await Body(response);
        Assert.Equal(129, (int?)result["usage"]!["tokens"]);
        Assert.Equal(0, (int?)result["embeddings"]![0]![0]);
        Assert.Equal(128, (int?)result["embeddings"]![128]![0]);
        var batches = result["response"]!["body"]!["batches"]!.AsArray();
        Assert.Equal(2, batches.Count);
        Assert.Equal("0", (string?)batches[0]!["diagnostics"]!["first"]);
        Assert.Equal("128", (string?)batches[1]!["diagnostics"]!["first"]);
        var upstream = host.Requests.ToArray();
        Assert.Equal(new[] { 128, 1 }, upstream.Select(request => request.Body!["texts"]!.AsArray().Count));
        Assert.All(upstream, request =>
        {
            Assert.Equal("/v1/embeddings", request.Path);
            Assert.Equal("Bearer legal-key", request.Headers["Authorization"]);
            Assert.True(request.Body!.AsObject().ContainsKey("dimensions"));
            Assert.Null(request.Body["dimensions"]);
            Assert.True((bool)request.Body["native"]!["nested"]!);
        });
    }

    [Fact]
    public async Task Isaacus_encodes_float32_and_reranks_with_raw_metadata()
    {
        await using var host = await Start(IsaacusReply);
        using var native = await host.PostAsync("/v1/embeddings", """{"model":"isaacus/kanon-2-embedder","input":"1","encoding_format":"base64"}""", IsaacusKey);
        var embedding = await Body(native);
        Assert.Equal("AACAPwAAAD8=", (string?)embedding["data"]![0]!["embedding"]);
        Assert.Equal("isaacus/kanon-2-embedder", (string?)embedding["model"]);
        using var rank = await host.PostAsync("/api/rerank", """
            {"model":"isaacus/kanon-2-reranker","query":"contracts","documents":{"type":"text","values":["zero","one"]},"topN":1,
             "providerOptions":{"isaacus":{"top_n":9,"future":{"nested":[1]},"texts":["override"]}}}
            """, IsaacusKey);
        var reranked = await Body(rank);
        Assert.Equal(1, (int?)reranked["ranking"]![0]!["index"]);
        Assert.True((bool)reranked["providerMetadata"]!["isaacus"]!["diagnostics"]!["nested"]![0]!);
        Assert.Equal(4, (int?)reranked["response"]!["body"]!["usage"]!["input_tokens"]);
        var sent = host.Requests.Last();
        Assert.Equal("/v1/rerankings", sent.Path);
        Assert.Equal("zero", (string?)sent.Body!["texts"]![0]);
        Assert.Equal(1, (int?)sent.Body["top_n"]);
        Assert.Equal(1, (int?)sent.Body["future"]!["nested"]![0]);
        using var invalid = await host.PostAsync("/v1/embeddings", """{"model":"isaacus/kanon-2-embedder","input":[1,2]}""", IsaacusKey);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(2, host.Requests.Count);
    }

    [Fact]
    public async Task EdgeSpeak_supports_wav_zip_and_speech_events_with_native_options()
    {
        await using var host = await Start();
        using var native = await host.PostAsync("/v1/audio/speech", """
            {"model":"edgespeak/vendor/reader/builtin:lee","input":"hello","stream_format":"sse"}
            """, ("X-EdgeSpeak-Key", "local-key"));
        var nativeEvents = await GatewayTests.Success(native);
        Assert.Contains("speech.audio.delta", nativeEvents);
        Assert.Contains("input_tokens", nativeEvents);
        var first = host.Requests.First();
        Assert.Equal("Bearer local-key", first.Headers["Authorization"]);
        Assert.Equal("vendor/reader", (string?)first.Body!["model"]);
        Assert.Equal("builtin:lee", (string?)first.Body["voice"]);

        using var speech = await host.PostAsync("/api/speech/stream", """
            {"model":"edgespeak/vendor/reader","text":[{"text":"hel","voice":"user:lee"},{"text":"lo","voice":"user:lee"}],
             "voice":"builtin:lee","language":"nl","outputFormat":{"type":"wav","rate":24000},"includeRawChunks":true,
             "providerOptions":{"edgespeak":{"speech_hint":{"levels":[1,null]}}}}
            """);
        var events = await GatewayTests.Success(speech);
        Assert.Contains("rawValue", events);
        Assert.Contains("diagnostics", events);
        var streamed = host.Requests.Last();
        Assert.Equal("hello", (string?)Assert.Single(streamed.Body!["segments"]!.AsArray())!["input"]);
        Assert.Null(streamed.Body["input"]);
        Assert.Equal("nl", (string?)streamed.Body["language"]);
        Assert.Equal(24000, (int?)streamed.Body["sample_rate"]);

        using var zip = await host.PostAsync("/api/speech", """
            {"model":"edgespeak/m-a-p/YuE2-3B","text":"hello","providerOptions":{"edgespeak":{"voice":"builtin:music",
             "output_container":"zip","music":{"planning_mode":"structured"}}}}
            """);
        var package = await Body(zip);
        Assert.Equal("application/zip", (string?)package["audio"]!["mediaType"]);
        Assert.Equal("1", host.Requests.Last().Headers["buffered-speech"]);
        Assert.Equal("structured", (string?)host.Requests.Last().Body!["music"]!["planning_mode"]);

        using var wav = await host.PostAsync("/v1/audio/speech", """{"model":"edgespeak/vendor/reader","input":"hello","voice":"builtin:lee"}""");
        wav.EnsureSuccessStatusCode();
        Assert.Equal("audio/wav", wav.Content.Headers.ContentType?.MediaType);
        Assert.Equal(new byte[] { 1, 2, 3 }, await wav.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task EdgeSpeak_transcribes_native_multipart_and_vercel_audio_with_metadata()
    {
        await using var host = await Start();
        using var form = new MultipartFormDataContent();
        var audio = new ByteArrayContent([1, 2, 3]);
        audio.Headers.ContentType = new("audio/wav");
        form.Add(audio, "file", "sample.wav");
        form.Add(new StringContent("edgespeak/vendor/listener"), "model");
        form.Add(new StringContent("diarized_json"), "response_format");
        form.Add(new StringContent("voice-detect"), "diarization_model");
        form.Add(new StringContent("word"), "timestamp_granularities[]");
        using var native = await host.PostFormAsync("/v1/audio/transcriptions", form);
        var diarized = await Body(native);
        Assert.Equal("transcribe", (string?)diarized["task"]);
        Assert.Equal("speaker-1", (string?)diarized["segments"]![0]!["speaker"]);
        Assert.Equal("0", (string?)diarized["segments"]![0]!["id"]);
        Assert.Contains("timestamp_granularities[]", host.Requests.First().RawBody);
        Assert.Contains("voice-detect", host.Requests.First().RawBody);
        using var sdk = await host.PostAsync("/api/transcriptions", """
            {"model":"edgespeak/vendor/listener","audio":"AQID","mediaType":"audio/wav"}
            """);
        var result = await Body(sdk);
        Assert.Equal("hello", (string?)result["text"]);
        Assert.Equal("speaker-1", (string?)result["providerMetadata"]!["edgespeak"]!["segments"]![0]!["speaker"]);
        using var stream = await host.PostAsync("/api/transcriptions/stream", """
            {"model":"edgespeak/vendor/listener","audio":"AQID","inputAudioFormat":{"type":"audio/wav"},"includeRawChunks":true}
            """);
        var streamBody = await GatewayTests.Success(stream);
        Assert.Contains("rawValue", streamBody);
        Assert.Contains("diagnostics", streamBody);
        using var vercel = await host.PostAsync(Vercel, """
            {"model":"edgespeak/vendor/listener","messages":[{"id":"u","role":"user","parts":[{"type":"file","mediaType":"audio/wav","url":"data:audio/wav;base64,AQID"}]}]}
            """);
        var vercelBody = await GatewayTests.Success(vercel);
        Assert.Contains("text-delta", vercelBody);
        Assert.Contains("diagnostics", vercelBody);
        Assert.Contains(host.Requests, request => request.Path == "/local/v1/models");
        using var invalid = await host.PostAsync("/api/transcriptions/stream", """
            {"model":"edgespeak/vendor/listener","audio":"AQID","inputAudioFormat":{"type":"audio/wav"},
             "providerOptions":{"edgespeak":{"timestamp_granularities":["word"]}}}
            """);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(5, host.Requests.Count);
    }

    [Fact]
    public async Task EdgeSpeak_vercel_speech_uses_discovered_model_and_preserves_finish_metadata()
    {
        await using var host = await Start();
        var payload = GatewayTests.Request(Vercel, "edgespeak/vendor/reader/user:lee", true);
        payload["temperature"] = 0.6;
        using var response = await host.PostAsync(Vercel, payload.ToJsonString());
        var body = await GatewayTests.Success(response);
        Assert.Contains("data:audio/wav;base64,AQID", body);
        Assert.Contains("diagnostics", body);
        var requests = host.Requests.ToArray();
        Assert.Equal(new[] { "/local/v1/models", "/local/v1/audio/speech" }, requests.Select(request => request.Path));
        Assert.Equal("user:lee", (string?)requests[1].Body!["voice"]);
        Assert.Equal(0.6, (double?)requests[1].Body!["temperature"]);
        Assert.True(requests[1].Stream);
    }

    private static Task<FixtureHost> Start(Func<FixtureHost.UpstreamRequest, HttpResponseMessage>? reply = null)
        => FixtureHost.StartAsync(reply ?? Reply, configureHost: host => host.Configuration["Providers:edgespeak:Endpoint"] = "https://edgespeak.test/local/v1/",
            aichixiaEdgeSpeakIsaacus: true);

    private static HttpResponseMessage Reply(FixtureHost.UpstreamRequest request)
    {
        var path = request.Path[(request.Host == "edgespeak.test" ? "/local".Length : "/api".Length)..];
        if (request.Host == "edgespeak.test")
        {
            switch (path)
            {
                case "/v1/models": return FixtureHost.Json(EdgeModels);
                case "/v1/audio/speech" when request.Stream:
                    return FixtureHost.Sse("""{"type":"speech.audio.delta","audio":"AQID"}""",
                        """{"type":"speech.audio.done","usage":{"input_tokens":3},"diagnostics":{"nested":[1,true]}}""");
                case "/v1/audio/speech":
                    var audio = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
                    audio.Content.Headers.ContentType = new MediaTypeHeaderValue((string?)request.Body?["output_container"] == "zip" ? "application/zip" : "audio/wav");
                    return audio;
                case "/v1/audio/transcriptions" when request.Stream:
                    return FixtureHost.Sse("""{"type":"transcript.text.delta","delta":"hello"}""",
                        """{"type":"transcript.text.done","text":"hello","diagnostics":{"nested":[1,null]}}""");
                case "/v1/audio/transcriptions":
                    return FixtureHost.Json("""{"text":"hello","language":"en","duration":1,"segments":[{"text":"hello","start":0,"end":1,"speaker":"speaker-1"}]}""");
            }
        }
        return FixtureHost.Reply(request with { Path = path });
    }

    private static HttpResponseMessage IsaacusReply(FixtureHost.UpstreamRequest request)
    {
        if (request.Path == "/v1/rerankings")
            return FixtureHost.Json("""{"results":[{"index":1,"score":0.75}],"usage":{"input_tokens":4},"diagnostics":{"nested":[true,null]}}""");
        var texts = request.Body!["texts"]!.AsArray();
        var embeddings = texts.Select((text, index) => new { index, embedding = new[] { float.Parse((string)text!, System.Globalization.CultureInfo.InvariantCulture), 0.5f } }).Reverse();
        return FixtureHost.Json(JsonSerializer.Serialize(new { embeddings, usage = new { input_tokens = texts.Count }, diagnostics = new { first = (string?)texts[0] } }));
    }

    private static async Task<JsonNode> Body(HttpResponseMessage response) => JsonNode.Parse(await GatewayTests.Success(response))!;
}
