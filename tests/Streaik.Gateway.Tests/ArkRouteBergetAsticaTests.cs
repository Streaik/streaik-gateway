using System.Net;
using System.Text.Json.Nodes;

namespace Streaik.Gateway.Tests;

public sealed class ArkRouteBergetAsticaTests
{
    private const string Chat = "/v1/chat/completions";
    private const string Responses = "/v1/responses";
    private const string Messages = "/v1/messages";
    private const string Vercel = "/api/chat";
    private const string Future = "{\"levels\":[2,{\"enabled\":true}]}";
    private static readonly (string, string) ArkRoute = ("X-ArkRoute-Key", "arkroute-key");
    private static readonly (string, string) Berget = ("X-BergetAI-Key", "bergetai-key");
    private static readonly (string, string) Astica = ("X-Astica-Key", "astica-key");

    [Fact]
    public async Task Discovery_is_key_scoped_and_preserves_native_ids_and_pricing()
    {
        await using var host = await Start(request => (request.Host, request.Path) switch
        {
            ("api.berget.ai", "/v1/models") => FixtureHost.Json("""
                {"data":[{"id":"vendor/chat","name":"chat","context_length":4096,"created":1700000000000,"pricing":{"input":2,"output":4,"unit":"per M Token"}},
                {"id":"KBLab/kb-whisper-large","pricing":{"output":3.6,"unit":"EUR / hour"}},{"id":"vendor/reranker"}]}
                """),
            ("voice.astica.ai", "/api/voice_list") => FixtureHost.Json("""
                {"voices":[{"voice":"voice-1","name":"First","use_cases":["news"]},{"id":"voice-1"},{"name":"Skip"}]}
                """),
            _ => FixtureHost.Reply(request)
        });
        using var anonymous = await host.GetAsync("/v1/models");
        Assert.Empty((await Json(anonymous))["data"]!.AsArray());
        Assert.Empty(host.Requests);
        using var response = await host.GetAsync("/v1/models", ArkRoute, Berget, Astica);
        var models = (await Json(response))["data"]!.AsArray().ToDictionary(item => (string)item!["id"]!);
        Assert.Equal("language", (string?)models["arkroute/vendor/Model"]!["type"]);
        var chat = models["bergetai/vendor/chat"]!;
        Assert.Equal(4096, (int?)chat["context_window"]);
        Assert.Equal(1700000000L, (long?)chat["created"]);
        Assert.Equal(0.000002m, Decimal(chat["pricing"]!["input"]!));
        var whisper = models["bergetai/KBLab/kb-whisper-large"]!;
        Assert.Equal("transcription", (string?)whisper["type"]);
        Assert.Equal(0.001m, Decimal(whisper["pricing"]!["output"]!));
        Assert.Equal("reranking", (string?)models["bergetai/vendor/reranker"]!["type"]);
        Assert.Equal("image", (string?)models["astica/2.0_full"]!["type"]);
        Assert.Equal("speech", (string?)models["astica/voice-1"]!["type"]);
        Assert.Equal("Bearer arkroute-key", host.Requests.Single(x => x.Host == "api.ark-route.com").Headers["Authorization"]);
        Assert.Equal("Bearer bergetai-key", host.Requests.Single(x => x.Host == "api.berget.ai").Headers["Authorization"]);
        Assert.Equal("astica-key", (string?)host.Requests.Single(x => x.Host == "voice.astica.ai").Body!["tkn"]);

        static decimal Decimal(JsonNode node) => decimal.Parse(node.ToString(), System.Globalization.CultureInfo.InvariantCulture);
    }

    [Theory]
    [InlineData("arkroute", Chat, false)]
    [InlineData("arkroute", Chat, true)]
    [InlineData("arkroute", Responses, false)]
    [InlineData("arkroute", Messages, true)]
    [InlineData("arkroute", Vercel, true)]
    [InlineData("bergetai", Chat, false)]
    [InlineData("bergetai", Responses, true)]
    [InlineData("bergetai", Messages, false)]
    [InlineData("bergetai", Vercel, true)]
    public async Task Text_uses_only_native_chat_and_round_trips_provider_fields(string provider, string route, bool stream)
    {
        await using var host = await Start();
        var body = GatewayTests.Request(route, $"{provider}/vendor/model", stream);
        var options = JsonNode.Parse($$"""{"MODEL":"override","future_option":{{Future}}}""");
        if (route == Vercel) body["providerMetadata"] = new JsonObject { [provider] = options };
        else body[provider] = options;
        using var response = await host.PostAsync(route, body.ToJsonString(), provider == "arkroute" ? ArkRoute : Berget);
        var result = await GatewayTests.Success(response);
        Assert.Contains("hello", result);
        Assert.Contains("future_response", result);
        var upstream = Assert.Single(host.Requests);
        Assert.Equal(Chat, upstream.Path);
        Assert.Equal($"Bearer {provider}-key", upstream.Headers["Authorization"]);
        Assert.Equal("vendor/model", (string?)upstream.Body!["model"]);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Future), upstream.Body["future_option"]));
        Assert.Null(upstream.Body["MODEL"]);
    }

    [Fact]
    public async Task Berget_reranking_preserves_metadata_and_sorts_before_limiting()
    {
        await using var host = await Start();
        using var response = await host.PostAsync("/api/rerank", $$"""
            {"model":"bergetai/vendor/reranker","query":"needle","documents":{"type":"text","values":["one","two"]},"topN":1,
             "providerOptions":{"bergetai":{"QUERY":"override","top_n":99,"return_documents":true,"future":{{Future}} } } }
            """, Berget);
        var result = await Json(response);
        Assert.Equal(1, (int?)Assert.Single(result["ranking"]!.AsArray())!["index"]);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Future), result["providerMetadata"]!["bergetai"]!["future"]));
        Assert.Equal("vendor/reranker", (string?)result["response"]!["body"]!["model"]);
        var upstream = Assert.Single(host.Requests);
        Assert.Equal("/v1/rerank", upstream.Path);
        Assert.Equal("needle", (string?)upstream.Body!["query"]);
        Assert.Equal(1, (int?)upstream.Body["top_n"]);
        Assert.Equal(true, (bool?)upstream.Body["return_documents"]);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Future), upstream.Body["future"]));
    }

    [Fact]
    public async Task Berget_native_transcription_preserves_raw_json_and_emits_sse_on_request()
    {
        await using var host = await Start();
        foreach (var stream in new[] { false, true })
        {
            using var form = new MultipartFormDataContent();
            var input = new ByteArrayContent([1, 2, 3]);
            input.Headers.ContentType = new("audio/wav");
            form.Add(input, "file", "input.wav");
            form.Add(new StringContent("bergetai/KBLab/kb-whisper-large"), "model");
            form.Add(new StringContent(Future), "future_option");
            if (stream) form.Add(new StringContent("true"), "stream");
            using var response = await host.PostFormAsync("/v1/audio/transcriptions", form, Berget);
            var result = await GatewayTests.Success(response);
            Assert.Contains("greetings", result);
            if (stream)
            {
                Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
                Assert.Contains("transcript.text.delta", result);
                Assert.Contains("transcript.text.done", result);
            }
            else Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Future), JsonNode.Parse(result)!["future"]));
        }
        Assert.All(host.Requests, upstream => CheckTranscription(upstream, expectOptions: true));
    }

    [Theory]
    [InlineData("/api/transcriptions", """{"audio":"AQID","mediaType":"audio/wav","providerOptions":{"bergetai":{"future_option":{"levels":[2]},"model":"override"}}}""")]
    [InlineData(Chat, """{"stream":true,"bergetai":{"future_option":{"levels":[2]}},"messages":[{"role":"user","content":[{"type":"input_audio","input_audio":{"data":"AQID","format":"wav"}}]}]}""")]
    [InlineData(Responses, """{"stream":true,"input":[{"role":"user","content":[{"type":"input_file","file_data":"data:audio/wav;base64,AQID","filename":"input.wav"}]}]}""")]
    [InlineData(Messages, """{"max_tokens":32,"messages":[{"role":"user","content":[{"type":"document","source":{"type":"base64","data":"AQID","media_type":"audio/wav"}}]}]}""")]
    [InlineData(Vercel, """{"messages":[{"id":"u","role":"user","parts":[{"type":"file","url":"data:audio/wav;base64,AQID","mediaType":"audio/wav"}]}]}""")]
    public async Task Berget_transcription_routes_preserve_provider_metadata(string route, string input)
    {
        await using var host = await Start();
        var body = JsonNode.Parse(input)!.AsObject();
        body["model"] = "bergetai/KBLab/kb-whisper-large";
        using var response = await host.PostAsync(route, body.ToJsonString(), Berget);
        var result = await GatewayTests.Success(response);
        Assert.Contains("greetings", result);
        Assert.Contains("future", result);
        CheckTranscription(Assert.Single(host.Requests), expectOptions: route is "/api/transcriptions" or Chat);
    }

    [Fact]
    public async Task Astica_generation_preserves_options_and_assets_are_downloaded_without_credentials()
    {
        await using var host = await Start();
        using var vercel = await host.PostAsync("/api/images", $$"""
            {"model":"astica/2.0_full","prompt":"new image","seed":42,"n":2,"providerOptions":{"astica":{"tkn":"override","modelVersion":"wrong","generate_quality":"HIGH","future":{{Future}} } } }
            """, Astica);
        var result = await Json(vercel);
        Assert.Contains("data:image/png;base64,AQID", result["images"]!.ToJsonString());
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Future), result["providerMetadata"]!["astica"]!["future"]));
        Assert.Single(result["warnings"]!.AsArray());
        using var native = await host.PostAsync("/v1/images/generations", """{"model":"astica/2.0_full","prompt":"new image","stream":true}""", Astica);
        var stream = await GatewayTests.Success(native);
        Assert.Contains("event: image_generation.completed", stream);
        Assert.Contains("AQID", stream);
        using var web = await host.PostAsync(Vercel, GatewayTests.Request(Vercel, "astica/2.0_full", true).ToJsonString(), Astica);
        Assert.Contains("data:image/png;base64,AQID", await GatewayTests.Success(web));
        var design = host.Requests.First();
        Assert.Equal("design.astica.ai", design.Host);
        Assert.Equal("2.0_full", (string?)design.Body!["modelVersion"]);
        Assert.Equal("astica-key", (string?)design.Body["tkn"]);
        Assert.Equal("high", (string?)design.Body["generate_quality"]);
        Assert.Equal(42, (int?)design.Body["seed"]);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Future), design.Body["future"]));
        Assert.All(host.Requests.Where(x => x.Method == "GET"), download =>
        {
            Assert.Equal("assets.astica.ai", download.Host);
            Assert.Null(download.Body);
            Assert.DoesNotContain("Authorization", download.Headers.Keys);
            Assert.DoesNotContain("X-Astica-Key", download.Headers.Keys);
        });
    }

    [Fact]
    public async Task Astica_buffered_and_streaming_speech_use_voice_from_model_and_preserve_raw_metadata()
    {
        await using var host = await Start();
        using var speech = await host.PostAsync("/api/speech", $$"""
            {"model":"astica/voice-1","text":"hello","voice":"ignored","outputFormat":"mp3","providerOptions":{"astica":{"tkn":"bad","future":{{Future}} } } }
            """, Astica);
        var result = await Json(speech);
        Assert.Equal("audio/wav", (string?)result["audio"]!["mediaType"]);
        Assert.Equal("wav", (string?)result["audio"]!["format"]);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Future), result["providerMetadata"]!["astica"]!["future"]));
        using var streaming = await host.PostAsync("/api/speech/stream", $$"""
            {"model":"astica/voice-1","text":[{"text":"hel"},{"text":"lo"}],"includeRawChunks":true,
             "providerOptions":{"astica":{"future":{{Future}},"stream":false} } }
            """, Astica);
        var events = await GatewayTests.Success(streaming);
        Assert.Contains("\"type\":\"raw\"", events);
        Assert.Contains("\"type\":\"finish\"", events);
        Assert.Contains("AQID", events);
        using var native = await host.PostAsync("/v1/audio/speech", """{"model":"astica/voice-1","input":"hello","stream_format":"sse"}""", Astica);
        Assert.Contains("speech.audio.done", await GatewayTests.Success(native));
        using var messages = await host.PostAsync(Messages, GatewayTests.Request(Messages, "astica/voice-1", true).ToJsonString(), Astica);
        Assert.Contains("data:audio/wav;base64,AQID", await GatewayTests.Success(messages));
        Assert.All(host.Requests, upstream =>
        {
            Assert.Equal("voice.astica.ai", upstream.Host);
            Assert.Equal("/api/tts", upstream.Path);
            Assert.Equal("voice-1", (string?)upstream.Body!["voice"]);
            Assert.Equal("astica-key", (string?)upstream.Body["tkn"]);
        });
        var upstream = host.Requests.ToArray();
        Assert.Equal("hello", (string?)upstream[1].Body!["text"]);
        Assert.Equal(true, (bool?)upstream[1].Body!["stream"]);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Future), upstream[1].Body!["future"]));
    }

    [Fact]
    public async Task Astica_rejects_unsupported_edits_and_invalid_upstream_asset_hosts()
    {
        await using var host = await Start(_ => FixtureHost.Json("""{"output":"https://unknown.example/output.png"}"""));
        using var edit = await host.PostAsync("/v1/images/edits", """
            {"model":"astica/2.0_full","prompt":"edit","images":[{"image_url":"data:image/png;base64,AQID"}]}
            """, Astica);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, edit.StatusCode);
        Assert.Empty(host.Requests);
        using var generation = await host.PostAsync("/v1/images/generations", """{"model":"astica/2.0_full","prompt":"draw"}""", Astica);
        Assert.Equal(HttpStatusCode.BadGateway, generation.StatusCode);
        Assert.Single(host.Requests);
    }

    private static async Task<JsonNode> Json(HttpResponseMessage response) => JsonNode.Parse(await GatewayTests.Success(response))!;

    private static Task<FixtureHost> Start(Func<FixtureHost.UpstreamRequest, HttpResponseMessage>? reply = null) => FixtureHost.StartAsync(reply ?? Reply,
        arkRouteBergetAstica: true);

    private static HttpResponseMessage Reply(FixtureHost.UpstreamRequest request) => request.Path switch
    {
        "/v1/audio/transcriptions" => FixtureHost.Json($$"""{"text":"greetings","language":"sv","future":{{Future}}}"""),
        "/v1/rerank" => FixtureHost.Json($$"""
            {"id":"rank","model":"vendor/reranker","data":[{"index":0,"relevance_score":0.1},{"index":1,"relevance_score":0.9}],"future":{{Future}}}
            """),
        "/generate_image" => FixtureHost.Json($$"""{"status":"success","output":"https://assets.astica.ai/sample.png","future":{{Future}}}"""),
        "/sample.png" => AudioOrImage("image/png"),
        "/api/tts" when (bool?)request.Body!["stream"] == true => AudioOrImage("audio/mpeg"),
        "/api/tts" => FixtureHost.Json($$"""{"status":"success","audio_b64":"AQID","engine":"v1","future":{{Future}}}"""),
        _ => FixtureHost.Reply(request)
    };

    private static HttpResponseMessage AudioOrImage(string type)
    {
        var content = new ByteArrayContent([1, 2, 3]);
        content.Headers.ContentType = new(type);
        return new(HttpStatusCode.OK) { Content = content };
    }

    private static void CheckTranscription(FixtureHost.UpstreamRequest upstream, bool expectOptions)
    {
        Assert.Equal("api.berget.ai", upstream.Host);
        Assert.Equal("/v1/audio/transcriptions", upstream.Path);
        Assert.Equal("Bearer bergetai-key", upstream.Headers["Authorization"]);
        Assert.Contains("KBLab/kb-whisper-large", upstream.RawBody);
        Assert.DoesNotContain("bergetai/KBLab", upstream.RawBody);
        Assert.DoesNotContain("override", upstream.RawBody);
        Assert.DoesNotContain("name=stream", upstream.RawBody);
        if (expectOptions) Assert.Contains("future_option", upstream.RawBody);
    }
}
