using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace Streaik.Gateway.Tests;

public sealed class TetrateUpstageTypecastTests
{
    private static readonly (string, string) Tetrate = ("x-tetrate-key", "tetrate-fixture");
    private static readonly (string, string) Upstage = ("x-upstage-key", "upstage-fixture");
    private static readonly (string, string) Typecast = ("x-typecast-key", "typecast-fixture");

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Model_discovery_uses_the_right_credentials_and_keeps_native_model_ids(bool array)
    {
        const string models = """[{"id":"vendor/model","owned_by":"vendor","created":123,"context_window":65536,"max_output_tokens":512,"input_price":"4e-7","output_price":0.000001,"cached_price":"0.0000001","caching_price":"0.0000002"}]""";
        await using var host = await FixtureHost.StartAsync(request => request.Host switch
        {
            "api.router.tetrate.ai" => FixtureHost.Json(array ? models : $$"""{"data":{{models}}}"""),
            "api.typecast.ai" => FixtureHost.Json("""[{"voice_id":"v1","voice_name":"Ada","age":"young_adult","gender":"female","use_cases":["narration"],"models":[{"version":"ssfm-v30"},{"version":"ssfm-v30"},{"version":"ssfm-v21"}]},{"id":"invalid"}]"""),
            _ => throw new InvalidOperationException(request.Host)
        }, tetrateUpstageTypecast: true);

        using var noKeys = await host.GetAsync("/v1/models");
        Assert.DoesNotContain("upstage/", await GatewayTests.Success(noKeys));
        Assert.Empty(host.Requests);
        using var keyed = await host.GetAsync("/v1/models", Tetrate, Typecast, Upstage);
        var listed = (await Json(keyed))["data"]!.AsArray().ToDictionary(node => (string)node!["id"]!);
        var tetrate = listed["tetrate/vendor/model"]!;
        Assert.Equal(123L, (long?)tetrate["created"]);
        Assert.Equal(65536, (int?)tetrate["context_window"]);
        Assert.Equal(512, (int?)tetrate["max_tokens"]);
        Assert.NotNull(tetrate["pricing"]!["input_cache_read"]);
        Assert.NotNull(tetrate["pricing"]!["input_cache_write"]);
        Assert.Equal("language", (string?)listed["upstage/solar-pro4"]!["type"]);
        Assert.Equal("speech", (string?)listed["typecast/ssfm-v30/v1"]!["type"]);
        Assert.Contains("young adult", (string)listed["typecast/ssfm-v21/v1"]!["name"]!);
        Assert.Equal(2, host.Requests.Count);
        var tetrateCall = Assert.Single(host.Requests, request => request.Host == "api.router.tetrate.ai");
        Assert.Equal("/v1/models", tetrateCall.Path);
        Assert.Equal("Bearer tetrate-fixture", tetrateCall.Headers["Authorization"]);
        var typecastCall = Assert.Single(host.Requests, request => request.Host == "api.typecast.ai");
        Assert.Equal("/v2/voices", typecastCall.Path);
        Assert.Equal("typecast-fixture", typecastCall.Headers["X-Api-Key"]);
        Assert.False(typecastCall.Headers.ContainsKey("Authorization"));
    }

    [Theory]
    [InlineData("tetrate", "/v1/chat/completions", "/v1/chat/completions", false)]
    [InlineData("tetrate", "/v1/chat/completions", "/v1/chat/completions", true)]
    [InlineData("tetrate", "/v1/responses", "/v1/responses", false)]
    [InlineData("tetrate", "/v1/responses", "/v1/responses", true)]
    [InlineData("tetrate", "/v1/messages", "/v1/messages", false)]
    [InlineData("tetrate", "/v1/messages", "/v1/messages", true)]
    [InlineData("tetrate", "/api/chat", "/v1/chat/completions", true)]
    [InlineData("upstage", "/v1/chat/completions", "/v1/chat/completions", false)]
    [InlineData("upstage", "/v1/chat/completions", "/v1/chat/completions", true)]
    [InlineData("upstage", "/v1/responses", "/v2/responses", false)]
    [InlineData("upstage", "/v1/responses", "/v2/responses", true)]
    [InlineData("upstage", "/v1/messages", "/v1/chat/completions", false)]
    [InlineData("upstage", "/v1/messages", "/v1/chat/completions", true)]
    [InlineData("upstage", "/api/chat", "/v1/chat/completions", true)]
    public async Task Text_protocols_preserve_native_json_and_use_the_expected_backend(string provider, string route, string path, bool stream)
    {
        await using var host = await FixtureHost.StartAsync(request => FixtureHost.Reply(request with
        {
            Path = request.Path == "/v2/responses" ? "/v1/responses" : request.Path
        }), tetrateUpstageTypecast: true);
        var payload = JsonNode.Parse(route switch
        {
            "/v1/responses" => """{"input":"Hi"}""",
            "/v1/messages" => """{"max_tokens":20,"messages":[{"role":"user","content":"Hi"}]}""",
            "/api/chat" => """{"messages":[{"id":"u","role":"user","parts":[{"type":"text","text":"Hi"}]}]}""",
            _ => """{"messages":[{"role":"user","content":"Hi"}]}"""
        })!.AsObject();
        payload["model"] = $"{provider}/vendor/model";
        var future = JsonNode.Parse("""{"flag":[true,null,3]}""");
        var translatedMessages = provider == "upstage" && route == "/v1/messages";
        if (route == "/api/chat")
            payload["providerMetadata"] = new JsonObject { [provider] = new JsonObject { ["native_future"] = future, ["model"] = "do-not-route" } };
        else
        {
            payload["stream"] = stream;
            if (!translatedMessages) payload["native_future"] = future;
        }
        using var response = await host.PostAsync(route, payload.ToJsonString(), Tetrate, Upstage, Typecast,
            ("anthropic-version", "2023-06-01"), ("X-Tetrate-Trace", "trace"), ("X-Tetrate-Token", "filtered"));
        var wire = await GatewayTests.Success(response);
        Assert.Contains("hello", wire);
        if (route != "/v1/messages" || !stream || provider == "upstage") Assert.Contains("future_response", wire);
        else Assert.Contains("future_content", wire);
        if (stream) Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        var sent = Assert.Single(host.Requests);
        Assert.Equal(path, sent.Path);
        Assert.Equal(provider == "tetrate" ? "api.router.tetrate.ai" : "api.upstage.ai", sent.Host);
        Assert.Equal($"Bearer {provider}-fixture", sent.Headers["Authorization"]);
        Assert.Equal(stream, sent.Stream);
        Assert.Equal("vendor/model", (string?)sent.Body!["model"]);
        if (!translatedMessages) Assert.True(JsonNode.DeepEquals(future, sent.Body["native_future"]));
        if (provider == "tetrate" && path == "/v1/messages") Assert.Equal("2023-06-01", sent.Headers["anthropic-version"]);
        Assert.False(sent.Headers.ContainsKey("X-Typecast-Key"));
        Assert.False(sent.Headers.ContainsKey("X-Tetrate-Token"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Upstage_decisions_map_question_types_and_keep_the_raw_SystemOne_result(bool native)
    {
        await using var host = await FixtureHost.StartAsync(request =>
        {
            var questions = request.Body!["questions"]!.AsObject();
            var ids = questions.Select(x => x.Key).ToArray();
            var choices = questions[ids[1]]!["criteria"]!.AsObject().Select(x => x.Key).ToArray();
            var levels = questions[ids[2]]!["criteria"]!.AsArray();
            var response = FixtureHost.Json($$$"""
                {"model":"solar-decide-rev","answers":{
                 "{{{ids[0]}}}":{"type":"noul","noul":0.75},
                 "{{{ids[1]}}}":{"type":"choice","choice":"{{{choices[1]}}}","confidence":0.9,"probabilities":{"{{{choices[0]}}}":0.2,"{{{choices[1]}}}":0.8}},
                 "{{{ids[2]}}}":{"type":"score","score":1.5,"confidence":0.8,"probabilities":{"0":0,"1":0.5,"2":0.5},"legend":{"0":{{{levels[0]!.ToJsonString()}}},"1":{{{levels[1]!.ToJsonString()}}},"2":{{{levels[2]!.ToJsonString()}}} } } },
                 "usage":{"input_tokens":7,"output_tokens":3},"native_extra":{"nested":[null,8]}}
                """);
            response.Headers.Add("X-Request-Id", "decision-1");
            response.Headers.TryAddWithoutValidation("Set-Cookie", "not-forwarded");
            return response;
        }, tetrateUpstageTypecast: true);

        var payload = native ? """
            {"model":"upstage/solar-decide","input":"Evidence","questions":[
             {"type":"predicate","name":"flag","instructions":"Flag?"},
             {"type":"choice","name":"pick","instructions":"Pick","choices":[{"value":"low"},{"value":true}]},
             {"type":"score","name":"score","instructions":"Rate","levels":[{"label":"low"},{"label":"mid"},{"label":"high"}]}]}
            """ : """
            {"model":"upstage/solar-decide","state":"Evidence","questions":{
             "flag":{"type":"boolean","instructions":"Flag?"},
             "pick":{"type":"choice","instructions":"Pick","criteria":{"low":"Low","high":"High"}},
             "score":{"type":"score","instructions":"Rate","criteria":["low","mid","high"]}},
             "headers":{"x-upstage-trace":"trace","Authorization":"not-forwarded"}}
            """;
        using var response = await host.PostAsync(native ? "/v1/decisions" : "/api/decisions", payload, Upstage, Typecast);
        var result = await Json(response);
        Assert.Equal(8, (int?)result["providerMetadata"]!["upstage"]!["native_extra"]!["nested"]![1]);
        if (native)
        {
            var answers = result["answers"]!.AsArray();
            Assert.Equal(0.75, (double?)answers[0]!["probability"]);
            Assert.True((bool?)answers[1]!["choice"]);
            Assert.Equal(0.75, (double?)answers[2]!["score"]);
            Assert.Equal(10, (int?)result["usage"]!["total_tokens"]);
        }
        else
        {
            Assert.Equal(0.75, (double?)result["answers"]!["flag"]!["probability"]);
            Assert.Equal("high", (string?)result["answers"]!["pick"]!["choice"]);
            Assert.Equal(1.5, (double?)result["answers"]!["score"]!["score"]);
            Assert.Equal("decision-1", (string?)result["response"]!["id"]);
            Assert.Null(result["response"]!["headers"]!["Set-Cookie"]);
        }
        var sent = Assert.Single(host.Requests);
        Assert.Equal("/v1/systemone", sent.Path);
        Assert.Equal("Bearer upstage-fixture", sent.Headers["Authorization"]);
        Assert.Equal("Evidence", (string?)sent.Body!["state"]);
        Assert.Equal("noul", (string?)sent.Body["questions"]![native ? "question-0" : "flag"]!["type"]);
        if (!native) Assert.Equal("trace", sent.Headers["x-upstage-trace"]);
    }

    [Fact]
    public async Task Upstage_decisions_reject_invalid_options_and_malformed_provider_answers()
    {
        await using var host = await FixtureHost.StartAsync(_ => FixtureHost.Json("""
            {"model":"solar-decide","answers":{"flag":{"type":"noul","noul":1.1}},"usage":{"input_tokens":2,"output_tokens":0}}
            """), tetrateUpstageTypecast: true);
        const string body = """{"model":"upstage/solar-decide","state":"x","questions":{"flag":{"type":"boolean","instructions":"Check"}}}""";
        using var noKey = await host.PostAsync("/api/decisions", body, Tetrate);
        Assert.Equal(HttpStatusCode.Unauthorized, noKey.StatusCode);
        var withOption = JsonNode.Parse(body)!.AsObject();
        withOption["providerOptions"] = JsonNode.Parse("""{"upstage":{"model":"injected"}}""");
        using var badInput = await host.PostAsync("/api/decisions", withOption.ToJsonString(), Upstage);
        Assert.Equal(HttpStatusCode.BadRequest, badInput.StatusCode);
        Assert.Empty(host.Requests);
        using var malformed = await host.PostAsync("/api/decisions", body, Upstage);
        Assert.Equal(HttpStatusCode.BadGateway, malformed.StatusCode);
        Assert.Single(host.Requests);
    }

    [Fact]
    public async Task Typecast_speech_preserves_native_options_and_metadata_without_overriding_routing_or_auth()
    {
        await using var host = await FixtureHost.StartAsync(_ => Audio(), tetrateUpstageTypecast: true);
        using var response = await host.PostAsync("/api/speech", """
            {"model":"typecast/ssfm-v30/voice-1","text":"Read this","language":"ko","outputFormat":"mp3",
             "providerOptions":{"typecast":{"language":"en","voice_id":"override","text":"override","api_key":"override",
              "audioTempo":1.25,"seed":42,"nextText":"And this","prompt":{"emotion_type":"custom","native_prompt":[true,null]},
              "output":{"audio_format":"wav","audio_pitch":-2,"native_output":{"a":1}},"native_root":{"array":[true,null]}},
              "upstage":{"unexpected":true}}}
            """, Typecast, Upstage, ("X-Api-Key", "not-forwarded"));
        var result = await Json(response);
        Assert.Equal("AQID", (string?)result["audio"]!["base64"]);
        Assert.Equal("audio/mpeg", (string?)result["audio"]!["mediaType"]);
        var headers = result["providerMetadata"]!["typecast"]!["headers"]!.AsObject();
        Assert.Equal("tts-1", (string?)Assert.Single(headers, entry => entry.Key.Equals("x-request-id", StringComparison.OrdinalIgnoreCase)).Value);
        var sent = Assert.Single(host.Requests);
        Assert.Equal("/v1/text-to-speech", sent.Path);
        Assert.Equal("typecast-fixture", sent.Headers["X-Api-Key"]);
        Assert.False(sent.Headers.ContainsKey("X-Upstage-Key"));
        Assert.Equal("ssfm-v30", (string?)sent.Body!["model"]);
        Assert.Equal("voice-1", (string?)sent.Body["voice_id"]);
        Assert.Equal("Read this", (string?)sent.Body["text"]);
        Assert.Equal("ko", (string?)sent.Body["language"]);
        Assert.Equal(42, (int?)sent.Body["seed"]);
        Assert.Equal("custom", (string?)sent.Body["prompt"]!["emotion_type"]);
        Assert.Equal("And this", (string?)sent.Body["prompt"]!["next_text"]);
        Assert.True((bool?)sent.Body["prompt"]!["native_prompt"]![0]);
        Assert.Equal("mp3", (string?)sent.Body["output"]!["audio_format"]);
        Assert.Equal(-2, (int?)sent.Body["output"]!["audio_pitch"]);
        Assert.Equal(1.25, (double?)sent.Body["output"]!["audio_tempo"]);
        Assert.Equal(1, (int?)sent.Body["output"]!["native_output"]!["a"]);
        Assert.True((bool?)sent.Body["native_root"]!["array"]![0]);
        Assert.False(sent.Body.AsObject().ContainsKey("api_key"));
        Assert.False(sent.Body.AsObject().ContainsKey("unexpected"));
    }

    [Theory]
    [InlineData("/v1/audio/speech", false, null)]
    [InlineData("/v1/chat/completions", false, "\"audio\"")]
    [InlineData("/v1/chat/completions", true, "[DONE]")]
    [InlineData("/v1/responses", false, "\"input_file\"")]
    [InlineData("/v1/responses", true, "response.completed")]
    [InlineData("/v1/messages", false, "\"document\"")]
    [InlineData("/v1/messages", true, "message_stop")]
    [InlineData("/api/chat", true, "\"file\"")]
    public async Task Typecast_uses_existing_audio_and_file_contracts(string route, bool stream, string? marker)
    {
        await using var host = await FixtureHost.StartAsync(_ => Audio(), tetrateUpstageTypecast: true);
        var payload = JsonNode.Parse(route switch
        {
            "/v1/audio/speech" or "/v1/responses" => """{"input":"Hi"}""",
            "/api/chat" => """{"messages":[{"id":"u","role":"user","parts":[{"type":"text","text":"Hi"}]}]}""",
            _ => """{"max_tokens":10,"messages":[{"role":"user","content":"Hi"}]}"""
        })!.AsObject();
        payload["model"] = "typecast/ssfm-v30/v1";
        if (route is not ("/api/chat" or "/v1/audio/speech")) payload["stream"] = stream;
        using var response = await host.PostAsync(route, payload.ToJsonString(), Typecast);
        if (marker is null)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(new byte[] { 1, 2, 3 }, await response.Content.ReadAsByteArrayAsync());
            Assert.Equal("audio/mpeg", response.Content.Headers.ContentType?.MediaType);
        }
        else
        {
            var wire = await GatewayTests.Success(response);
            Assert.Contains(marker, wire);
            Assert.Contains("AQID", wire);
            Assert.Contains("tts-1", wire);
            if (stream) Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        }
        var sent = Assert.Single(host.Requests);
        Assert.Equal("/v1/text-to-speech", sent.Path);
        Assert.False(sent.Stream);
        Assert.Equal("Hi", (string?)sent.Body!["text"]);
        Assert.Equal("smart", (string?)sent.Body["prompt"]!["emotion_type"]);
    }

    private static HttpResponseMessage Audio()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("audio/mpeg");
        response.Headers.Add("X-Request-Id", "tts-1");
        return response;
    }

    private static async Task<JsonNode> Json(HttpResponseMessage response) => JsonNode.Parse(await GatewayTests.Success(response))!;
}
