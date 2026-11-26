using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace Streaik.Gateway.Tests;

public sealed class InceptionRavenResembleTests
{
    private static readonly (string, string) Inception = ("x-inceptionlabs-key", "mercury-key");
    private static readonly (string, string) Raven = ("x-raven-key", "raven-key");
    private static readonly (string, string) Resemble = ("x-resembleai-key", "resemble-key");

    [Fact]
    public async Task Models_are_credential_scoped_and_voice_discovery_follows_pages()
    {
        await using var host = await FixtureHost.StartAsync(request => (request.Host, request.Path) switch
        {
            ("api.inceptionlabs.ai", "/v1/models") => FixtureHost.Json("""{"data":[{"id":"mercury-edit/apply","created":123}]}"""),
            ("raven.graphn.ai", "/api/inference/v1/models") => FixtureHost.Json("""{"data":[{"id":"vendor/raven","owned_by":"Graphn"}]}"""),
            ("app.resemble.ai", "/api/v2/voices?page_size=1000&page=1") => FixtureHost.Json("""
                {"num_pages":2,"items":[{"uuid":"bad-voice","voice_status":"Pending","api_support":{"sync":true}}]}
                """),
            ("app.resemble.ai", "/api/v2/voices?page_size=1000&page=2") => FixtureHost.Json("""
                {"num_pages":2,"items":[{"uuid":"voice-2","name":"Narrator","voice_status":"Ready","api_support":{"sync":true}}]}
                """),
            _ => throw new InvalidOperationException($"Unexpected upstream {request.Host}{request.Path}")
        }, inceptionRavenResemble: true);
        using var anonymous = await host.GetAsync("/v1/models");
        await GatewayTests.Success(anonymous);
        Assert.Empty(host.Requests);
        using var response = await host.GetAsync("/v1/models", Inception, Raven, Resemble);
        var ids = (await Body(response))["data"]!.AsArray().Select(item => (string?)item!["id"]).ToArray();
        Assert.Contains("inceptionlabs/mercury-edit/apply", ids);
        Assert.Contains("raven/vendor/raven", ids);
        Assert.Contains("resembleai/speech-to-text", ids);
        Assert.Contains("resembleai/chatterbox-multilingual/voice-2", ids);
        Assert.DoesNotContain(ids, id => id?.Contains("bad-voice") == true);
        Assert.Equal(4, host.Requests.Count);
        foreach (var request in host.Requests)
            Assert.Equal(request.Host switch { "api.inceptionlabs.ai" => "Bearer mercury-key", "raven.graphn.ai" => "Bearer raven-key", _ => "Bearer resemble-key" },
                request.Headers["Authorization"]);
    }

    [Theory]
    [InlineData("inceptionlabs", "mercury-2", "/v1/chat/completions", false)]
    [InlineData("inceptionlabs", "mercury-edit", "/v1/edit/completions", true)]
    [InlineData("inceptionlabs", "mercury-edit/apply", "/v1/apply/completions", false)]
    [InlineData("raven", "vendor/chat", "/api/inference/v1/chat/completions", true)]
    public async Task Chat_preserves_native_fields_and_uses_selected_model_and_transport(string provider, string model, string upstream, bool stream)
    {
        await using var host = await FixtureHost.StartAsync(ChatReply, inceptionRavenResemble: true);
        using var response = await host.PostAsync("/v1/chat/completions", $$$$"""
            {"model":"{{{{provider}}}}/{{{{model}}}}","stream":{{{{(stream ? "true" : "false")}}}},"messages":[{"role":"user","content":"Hi"}],
             "{{{{provider}}}}":{"model":"wrong","stream":false,"authorization":"wrong","future":{"nested":[null,7]}}}
            """, provider == "raven" ? Raven : Inception);
        var wire = await GatewayTests.Success(response);
        Assert.Contains("hello", wire);
        Assert.Contains("future_response", wire);
        var sent = Assert.Single(host.Requests);
        Assert.Equal(upstream, sent.Path);
        Assert.Equal(model, (string?)sent.Body!["model"]);
        Assert.Equal(stream, (bool?)sent.Body["stream"]);
        Assert.Equal(7, (int?)sent.Body["future"]!["nested"]![1]);
        Assert.Null(sent.Body[provider]);
        Assert.Null(sent.Body["authorization"]);
    }

    [Fact]
    public async Task Raven_responses_uses_chat_adapter_with_native_metadata()
    {
        await using var host = await FixtureHost.StartAsync(ChatReply, inceptionRavenResemble: true);
        using var response = await host.PostAsync("/v1/responses", """
            {"model":"raven/vendor/chat","input":"Hi","raven":{"future":{"nested":[null,7]}}}
            """, Raven);
        var body = await Body(response);
        Assert.Contains("hello", body.ToJsonString());
        Assert.Contains("future_response", body.ToJsonString());
        var sent = Assert.Single(host.Requests);
        Assert.Equal("/api/inference/v1/chat/completions", sent.Path);
        Assert.Equal(7, (int?)sent.Body!["future"]!["nested"]![1]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Mercury_decide_maps_noul_and_retains_native_response(bool sdk)
    {
        await using var host = await FixtureHost.StartAsync(request =>
        {
            Assert.Equal("/v1/decisions", request.Path);
            Assert.Equal("Bearer mercury-key", request.Headers["Authorization"]);
            var id = sdk ? "flag" : "question-0";
            Assert.Equal("noul", (string?)request.Body!["questions"]![id]!["type"]);
            if (sdk) Assert.Equal("text", (string?)request.Body["state"]![0]!["content"]![0]!["type"]);
            return FixtureHost.Json($$$"""
                {"model":"mercury-decide","answers":{"{{{id}}}":{"type":"noul","noul":0.75}},"usage":{"input_tokens":5,"output_tokens":1},"future":{"keep":true}}
                """);
        }, inceptionRavenResemble: true);
        using var response = await host.PostAsync(sdk ? "/api/decisions" : "/v1/decisions", sdk ? """
            {"model":"inceptionlabs/mercury-decide","stateFormat":"sdk","state":[{"type":"text","text":"Evidence"}],"questions":{"flag":{"type":"boolean","instructions":"Flag?"}}}
            """ : """
            {"model":"inceptionlabs/mercury-decide","input":"Evidence","questions":[{"type":"predicate","name":"flag","instructions":"Flag?"}]}
            """, Inception);
        var result = await Body(response);
        Assert.Equal(0.75, (double?)(sdk ? result["answers"]!["flag"] : result["answers"]![0])!["probability"]);
        Assert.True((bool?)result["providerMetadata"]!["inceptionlabs"]!["future"]!["keep"]);
    }

    [Fact]
    public async Task Mercury_decide_maps_choice_values_score_levels_and_inline_images()
    {
        await using var host = await FixtureHost.StartAsync(request =>
        {
            var questions = request.Body!["questions"]!;
            var image = questions["question-0"]!["instructions"]![1]!;
            Assert.Equal("image_url", (string?)image["type"]);
            Assert.Equal("data:image/png;base64,AQID", (string?)image["image_url"]!["url"]);
            Assert.True((bool?)questions["question-0"]!["criteria"]!["option-0"]!["value"]);
            var levels = questions["question-1"]!["criteria"]!;
            return FixtureHost.Json($$$"""
                {"model":"mercury-decide","usage":{"input_tokens":8,"output_tokens":2},"answers":{
                   "question-0":{"type":"choice","confidence":0.75,"choice":"option-0","probabilities":{"option-0":0.75,"option-1":0.25}},
                   "question-1":{"type":"score","confidence":0.75,"score":0.75,"probabilities":{"0":0.25,"1":0.75},"legend":{"0":{{{levels[0]}}},"1":{{{levels[1]}}}} } } }
                """);
        }, inceptionRavenResemble: true);
        using var response = await host.PostAsync("/v1/decisions", """
            {"model":"inceptionlabs/mercury-decide","input":"Evidence","questions":[
              {"type":"choice","name":"flag","instructions":[{"type":"input_text","text":"Choose"},{"type":"input_image","image_url":"data:image/png;base64,AQID"}],
               "choices":[{"value":true,"description":"Yes"},{"value":"true","description":"Literal"}]},
              {"type":"score","name":"rating","instructions":"Rate","levels":[{"label":"low","description":"Low"},{"label":"high","description":"High"}]}]}
            """, Inception);
        var result = await Body(response);
        Assert.True((bool?)result["answers"]![0]!["choice"]);
        Assert.Equal(0.75, (double?)result["answers"]![1]!["score"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Raven_embeddings_preserve_native_options_and_response_fields(bool sdk)
    {
        await using var host = await FixtureHost.StartAsync(request =>
        {
            Assert.Equal(("raven.graphn.ai", "/api/inference/v1/embeddings"), (request.Host, request.Path));
            Assert.Equal("Bearer raven-key", request.Headers["Authorization"]);
            Assert.Equal("vendor/embed", (string?)request.Body!["model"]);
            Assert.Equal("hello", (string?)request.Body["input"]![0]);
            Assert.True((bool?)request.Body["future"]!["keep"]);
            return FixtureHost.Json("""
                {"model":"vendor/embed","data":[{"index":0,"embedding":[0.25]}],"usage":{"prompt_tokens":1,"total_tokens":1},"future_result":{"v":[null,5]}}
                """);
        }, inceptionRavenResemble: true);
        using var response = await host.PostAsync(sdk ? "/api/embeddings" : "/v1/embeddings", sdk ? """
            {"model":"raven/vendor/embed","values":["hello"],"providerOptions":{"raven":{"input":["wrong"],"future":{"keep":true}}}}
            """ : """
            {"model":"raven/vendor/embed","input":["hello"],"raven":{"input":["wrong"],"future":{"keep":true}}}
            """, Raven);
        var result = await Body(response);
        if (sdk) Assert.Contains("future_result", result.ToJsonString());
        else
        {
            Assert.Equal(0.25, (double?)result["data"]![0]!["embedding"]![0]);
            Assert.DoesNotContain("future_result", result.ToJsonString()); // The OpenAI embedding response has a fixed shape.
        }
    }

    [Theory]
    [InlineData("""[{"index":1,"score":0.5,"future":true}]""")]
    [InlineData("""{"id":"rank-1","results":[{"index":1,"relevance_score":0.5,"future":true}]}""")]
    public async Task Raven_rerank_accepts_both_upstream_shapes(string payload)
    {
        await using var host = await FixtureHost.StartAsync(request =>
        {
            Assert.Equal("/api/inference/v1/rerank", request.Path);
            Assert.Equal("needle", (string?)request.Body!["query"]);
            Assert.Equal("second", (string?)request.Body["documents"]![1]);
            Assert.Equal(1, (int?)request.Body["top_n"]);
            Assert.True((bool?)request.Body["native"]![0]);
            Assert.Null(request.Body["authorization"]);
            return FixtureHost.Json(payload);
        }, inceptionRavenResemble: true);
        using var response = await host.PostAsync("/api/rerank", """
            {"model":"raven/vendor/rank","query":"needle","documents":{"type":"object","values":["first",{"text":"second"}]},"topN":1,
              "providerOptions":{"raven":{"query":"wrong","top_n":9,"authorization":"wrong","native":[true,null]}}}
            """, Raven);
        var body = await Body(response);
        Assert.Equal(0.5, (double?)body["ranking"]![0]!["relevanceScore"]);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(payload), body["providerMetadata"]!["raven"]));
    }

    [Fact]
    public async Task Resemble_speech_keeps_native_metadata_without_duplicating_audio()
    {
        await using var host = await FixtureHost.StartAsync(request =>
        {
            Assert.Equal(("f.cluster.resemble.ai", "/synthesize"), (request.Host, request.Path));
            Assert.Equal("Bearer resemble-key", request.Headers["Authorization"]);
            Assert.Equal("chatterbox", (string?)request.Body!["model"]);
            Assert.Equal("voice-1", (string?)request.Body["voice_uuid"]);
            Assert.Equal("Hi", (string?)request.Body["data"]);
            Assert.Equal("mp3", (string?)request.Body["output_format"]);
            Assert.True((bool?)request.Body["future"]!["keep"]);
            return FixtureHost.Json("""{"success":true,"audio_content":"AQID","output_format":"mp3","diagnostics":{"samples":[1,null]}}""");
        }, inceptionRavenResemble: true);
        using var response = await host.PostAsync("/api/speech", """
            {"model":"resembleai/chatterbox/voice-1","text":"Hi","outputFormat":"mp3",
             "providerOptions":{"resembleai":{"data":"wrong","output_format":"wav","future":{"keep":true}}}}
            """, Resemble);
        var body = await Body(response);
        Assert.Equal("AQID", (string?)body["audio"]!["base64"]);
        Assert.Equal(1, (int?)body["providerMetadata"]!["resembleai"]!["diagnostics"]!["samples"]![0]);
        Assert.Null(body["providerMetadata"]!["resembleai"]!["audio_content"]);
    }

    [Theory]
    [InlineData("/v1/responses", """{"model":"resembleai/chatterbox/voice-1","input":"Hi"}""")]
    [InlineData("/v1/messages", """{"model":"resembleai/chatterbox/voice-1","max_tokens":64,"messages":[{"role":"user","content":"Hi"}]}""")]
    [InlineData("/api/chat", """{"model":"resembleai/chatterbox/voice-1","messages":[{"id":"u","role":"user","parts":[{"type":"text","text":"Hi"}]}]}""")]
    public async Task Resemble_speech_routes_through_existing_chat_file_adapters(string path, string request)
    {
        await using var host = await FixtureHost.StartAsync(sent =>
        {
            Assert.Equal("/synthesize", sent.Path);
            Assert.Equal("Hi", (string?)sent.Body!["data"]);
            return FixtureHost.Json("""{"success":true,"audio_content":"AQID","output_format":"wav","native_metric":7}""");
        }, inceptionRavenResemble: true);
        using var response = await host.PostAsync(path, request, Resemble);
        var wire = await GatewayTests.Success(response);
        Assert.Contains("AQID", wire);
        Assert.Contains("native_metric", wire);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Resemble_WAV_stream_uses_existing_SSE_framing(bool sdk)
    {
        await using var host = await FixtureHost.StartAsync(request =>
        {
            Assert.Equal("/stream", request.Path);
            Assert.Equal("audio/wav", request.Headers["Accept"]);
            Assert.Equal("voice-1", (string?)request.Body!["voice_uuid"]);
            Assert.Equal("Hello", (string?)request.Body["data"]);
            Assert.Null(request.Body["model"]);
            Assert.True((bool?)request.Body["future"]!["keep"]);
            if (sdk) Assert.Equal(16000, (int?)request.Body["sample_rate"]);
            var audio = new ByteArrayContent([1, 2, 3]);
            audio.Headers.ContentType = new("audio/wav");
            return new(HttpStatusCode.OK) { Content = audio };
        }, inceptionRavenResemble: true);
        using var response = await host.PostAsync(sdk ? "/api/speech/stream" : "/v1/audio/speech", sdk ? """
            {"model":"resembleai/chatterbox/voice-1","text":[{"text":"Hel"},{"text":"lo","providerOptions":{"resembleai":{"sample_rate":8}}}],
             "outputFormat":{"type":"wav","rate":16000},"includeRawChunks":true,"providerOptions":{"resembleai":{"future":{"keep":true}}}}
            """ : """
            {"model":"resembleai/chatterbox/voice-1","input":"Hello","response_format":"wav","stream_format":"sse","future":{"keep":true}}
            """, Resemble);
        var wire = await GatewayTests.Success(response);
        Assert.Contains("AQID", wire);
        Assert.Contains(sdk ? "\"type\":\"finish\"" : "speech.audio.done", wire);
        if (sdk) Assert.Contains("\"type\":\"raw\"", wire);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Resemble_transcription_submits_multipart_polls_and_maps_existing_response(bool sdk)
    {
        await using var host = await FixtureHost.StartAsync(request => request.Path switch
        {
            "/api/v2/speech-to-text" => FixtureHost.Json("""{"success":true,"item":{"uuid":"job-1"}}"""),
            "/api/v2/speech-to-text/job-1" => FixtureHost.Json("""
                {"success":true,"item":{"status":"completed","text":"hello","duration_seconds":1.5,
                   "words":[{"text":"hello","start_time":0.1,"end_time":1.2,"speaker_id":"speaker-1"}],"future":{"keep":true}}}
                """),
            _ => throw new InvalidOperationException(request.Path)
        }, inceptionRavenResemble: true);
        if (sdk)
        {
            using var response = await host.PostAsync("/api/transcriptions", """
                {"model":"resembleai/speech-to-text","mediaType":"audio/wav","audio":"AQID",
                 "providerOptions":{"resembleai":{"query":"alias","native":{"keep":true},"file":"wrong","authorization":"wrong"}}}
                """, Resemble);
            var result = await Body(response);
            Assert.Equal("speaker-1: hello", (string?)result["segments"]![0]!["text"]);
            Assert.True((bool?)result["providerMetadata"]!["resembleai"]!["item"]!["future"]!["keep"]);
        }
        else
        {
            using var form = new MultipartFormDataContent();
            using var audio = new ByteArrayContent([1, 2, 3]);
            audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            form.Add(audio, "file", "sample.wav");
            form.Add(new StringContent("resembleai/speech-to-text"), "model");
            form.Add(new StringContent("true"), "stream");
            form.Add(new StringContent("alias"), "query");
            using var response = await host.PostFormAsync("/v1/audio/transcriptions", form, Resemble);
            var wire = await GatewayTests.Success(response);
            Assert.Contains("transcript.text.done", wire);
            Assert.Contains("hello", wire);
        }
        var sent = host.Requests.ToArray();
        Assert.Equal(["POST", "GET"], sent.Select(x => x.Method));
        Assert.All(sent, x => Assert.Equal("Bearer resemble-key", x.Headers["Authorization"]));
        Assert.Contains("alias", sent[0].RawBody);
        Assert.DoesNotContain("wrong", sent[0].RawBody);
    }

    private static HttpResponseMessage ChatReply(FixtureHost.UpstreamRequest request) => request.Stream
        ? FixtureHost.Sse("""
            {"id":"chunk","object":"chat.completion.chunk","model":"native","created":1,"choices":[{"index":0,"delta":{"role":"assistant","content":"hello"},"finish_reason":"stop"}],"future_response":{"x":null}}
            """, "[DONE]")
        : FixtureHost.Json("""
            {"id":"chat","object":"chat.completion","model":"native","created":1,"choices":[{"index":0,"message":{"role":"assistant","content":"hello"},"finish_reason":"stop"}],"future_response":{"x":null}}
            """);

    private static async Task<JsonNode> Body(HttpResponseMessage response) => JsonNode.Parse(await GatewayTests.Success(response))!;
}
