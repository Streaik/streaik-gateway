using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace Streaik.Gateway.Tests;

public sealed class BaseApiByteSpaceCambaiTests
{
    private static readonly (string, string) Cambai = ("x-cambai-key", "camb-fixture");
    private const string TranslationModel = "cambai/translate/source-language/1/target-language/2";
    private const string NativeOptions = """{"trace":{"values":[1,true]},"headers":{"authorization":"bad"}}""";

    [Fact]
    public async Task Catalog_uses_request_credentials_and_includes_all_Cambai_model_families()
    {
        await using var host = await FixtureHost.StartAsync(Reply, baseApiByteSpaceCambai: true);
        using var anonymous = await host.GetAsync("/v1/models");
        var noKey = await GatewayTests.Success(anonymous);
        Assert.DoesNotContain("baseapi/", noKey);
        Assert.DoesNotContain("bytespace/", noKey);
        Assert.DoesNotContain("cambai/", noKey);
        Assert.Empty(host.Requests);

        using var response = await host.GetAsync("/v1/models", ("x-baseapi-key", "base-fixture"), ("x-bytespace-key", "byte-fixture"), Cambai);
        var models = JsonNode.Parse(await GatewayTests.Success(response))!["data"]!.AsArray()
            .ToDictionary(model => model!["id"]!.GetValue<string>());
        Assert.Contains("baseapi/vendor/Model", models.Keys);
        Assert.Contains("bytespace/vendor/Model", models.Keys);
        Assert.Contains("cambai/text-to-voice", models.Keys);
        Assert.Contains("cambai/text-to-sound/sound", models.Keys);
        Assert.Contains("cambai/text-to-sound/music", models.Keys);
        Assert.Contains("cambai/tts/source-language/1", models.Keys);
        Assert.Contains("cambai/transcribe/source-language/1", models.Keys);
        Assert.Contains("cambai/translated-tts/source-language/1/target-language/2", models.Keys);
        Assert.Contains(TranslationModel, models.Keys);
        Assert.DoesNotContain("cambai/translate/source-language/1/target-language/1", models.Keys);
        Assert.All(host.Requests.Where(r => r.Host == "client.camb.ai"), r => Assert.Equal("camb-fixture", r.Headers["x-api-key"]));
        Assert.Equal("Bearer base-fixture", Assert.Single(host.Requests, r => r.Host == "api.baseapi.dev").Headers["Authorization"]);
        Assert.Equal("Bearer byte-fixture", Assert.Single(host.Requests, r => r.Host == "api.bytespace.sg").Headers["Authorization"]);
    }

    [Theory]
    [InlineData("baseapi", "api.baseapi.dev", "chat/completions", false)]
    [InlineData("bytespace", "api.bytespace.sg", "chat/completions", true)]
    [InlineData("baseapi", "api.baseapi.dev", "messages", false)]
    [InlineData("baseapi", "api.baseapi.dev", "messages", true)]
    [InlineData("bytespace", "api.bytespace.sg", "messages", false)]
    [InlineData("baseapi", "api.baseapi.dev", "responses", true)]
    [InlineData("bytespace", "api.bytespace.sg", "responses", false)]
    [InlineData("baseapi", "api.baseapi.dev", "vercel", true)]
    [InlineData("bytespace", "api.bytespace.sg", "vercel", true)]
    public async Task Text_routes_use_the_native_endpoint_or_existing_bridge_and_keep_raw_fields(string provider, string domain, string protocol, bool stream)
    {
        await using var host = await FixtureHost.StartAsync(baseApiByteSpaceCambai: true);
        var options = JsonNode.Parse(NativeOptions)!;
        options["model"] = "wrong";
        var request = new JsonObject { ["model"] = $"{provider}/vendor/Model", [provider] = options };
        switch (protocol)
        {
            case "vercel":
                request.Remove(provider);
                request["providerMetadata"] = new JsonObject { [provider] = options };
                request["messages"] = JsonNode.Parse("""[{"id":"u1","role":"user","parts":[{"type":"text","text":"hi"}]}]""");
                break;
            case "responses":
                request["input"] = "hi";
                break;
            default:
                request["messages"] = JsonNode.Parse("""[{"role":"user","content":"hi"}]""");
                if (protocol == "messages") request["max_tokens"] = 64;
                break;
        }
        if (protocol != "vercel") request["stream"] = stream;
        using var response = await host.PostAsync(protocol == "vercel" ? "/api/chat" : $"/v1/{protocol}", request.ToJsonString(),
            ($"x-{provider}-key", "key-fixture"), ("anthropic-version", "2023-06-01"));
        var wire = await GatewayTests.Success(response);
        Assert.Contains("hello", wire);
        Assert.Contains(provider == "baseapi" && protocol == "messages" && stream ? "future_content" : "future_response", wire);

        var native = Assert.Single(host.Requests);
        Assert.Equal(domain, native.Host);
        Assert.Equal("Bearer key-fixture", native.Headers["Authorization"]);
        Assert.Equal("vendor/Model", native.Body!["model"]!.GetValue<string>());
        Assert.True(native.Body["trace"]!["values"]![1]!.GetValue<bool>());
        Assert.Null(native.Body["headers"]);
        if (provider == "baseapi" && protocol == "messages")
        {
            Assert.Equal("/v1/messages", native.Path);
            Assert.Equal("2023-06-01", native.Headers["anthropic-version"]);
        }
        else Assert.Equal("/v1/chat/completions", native.Path);
    }

    [Theory]
    [InlineData("/v1/chat/completions", false)]
    [InlineData("/v1/chat/completions", true)]
    [InlineData("/v1/responses", false)]
    [InlineData("/v1/responses", true)]
    [InlineData("/api/chat", true)]
    public async Task Cambai_translation_keeps_order_and_raw_jobs_without_forwarding_unrelated_history(string route, bool stream)
    {
        await using var host = await FixtureHost.StartAsync(Reply, baseApiByteSpaceCambai: true);
        var options = JsonNode.Parse(NativeOptions)!;
        options["source_language"] = 99;
        options["texts"] = new JsonArray("wrong");
        var request = new JsonObject { ["model"] = TranslationModel };
        if (route == "/api/chat")
        {
            request["providerMetadata"] = new JsonObject { ["cambai"] = options };
            request["messages"] = JsonNode.Parse("""
                [{"id":"u1","role":"user","parts":[{"type":"text","text":"previous"}]},
                 {"id":"a1","role":"assistant","parts":[{"type":"text","text":"skip"}]},
                 {"id":"u2","role":"user","parts":[{"type":"text","text":"one"},{"type":"text","text":"two"}]}]
                """);
        }
        else
        {
            request["stream"] = stream;
            request["cambai"] = options;
            var history = JsonNode.Parse("""
                [{"role":"user","content":"one"},{"role":"assistant","content":"skip"},{"role":"user","content":"two"}]
                """);
            request[route.EndsWith("responses", StringComparison.Ordinal) ? "input" : "messages"] = history;
        }
        using var response = await host.PostAsync(route, request.ToJsonString(), Cambai);
        var wire = await GatewayTests.Success(response);
        Assert.Contains("uno", wire);
        Assert.Contains("dos", wire);
        Assert.Contains("raw_result", wire);
        Assert.Contains("raw_create", wire);
        Assert.Contains("raw_status", wire);

        Assert.Equal(new[] { "/apis/translate", "/apis/translate/task-1", "/apis/translation-result/7" }, host.Requests.Select(r => r.Path));
        var payload = host.Requests.First().Body!;
        Assert.Equal(new[] { "one", "two" }, payload["texts"]!.AsArray().Select(v => v!.GetValue<string>()));
        Assert.Equal(1, payload["source_language"]!.GetValue<int>());
        Assert.Equal(2, payload["target_language"]!.GetValue<int>());
        Assert.True(payload["trace"]!["values"]![1]!.GetValue<bool>());
        Assert.Null(payload["headers"]);
    }

    [Theory]
    [InlineData("tts/source-language/1", "tts", "tts-result", "language", "1")]
    [InlineData("translated-tts/source-language/1/target-language/2", "translated-tts", "tts-result", "target_language", "2")]
    [InlineData("text-to-sound/music", "text-to-sound", "text-to-sound-result", "audio_type", "\"music\"")]
    [InlineData("text-to-sound/sound", "text-to-sound", "text-to-sound-result", "audio_type", "\"sound\"")]
    [InlineData("text-to-voice", "text-to-voice", "text-to-voice-result", "voice_description", "\"warm\"")]
    public async Task Cambai_speech_routes_all_job_types_and_does_not_send_keys_to_assets(
        string model, string operation, string result, string field, string expected)
    {
        await using var host = await FixtureHost.StartAsync(Reply, baseApiByteSpaceCambai: true);
        using var response = await host.PostAsync("/api/speech", $$$"""
            {"model":"cambai/{{{model}}}","text":"speak","voice":"456","instructions":"warm","providerOptions":{"cambai":{{{NativeOptions}}}}}
            """, Cambai);
        var output = JsonNode.Parse(await GatewayTests.Success(response))!;
        Assert.Equal("AQID", output["audio"]!["base64"]!.GetValue<string>());
        Assert.True(output["providerMetadata"]!["cambai"]!["create"]!["raw_create"]!.GetValue<bool>());
        var api = host.Requests.Where(r => r.Host == "client.camb.ai").ToArray();
        Assert.Equal(new[] { $"/apis/{operation}", $"/apis/{operation}/task-1", $"/apis/{result}/7" }, api.Select(r => r.Path));
        Assert.All(api, r => Assert.Equal("camb-fixture", r.Headers["x-api-key"]));
        Assert.Equal(expected, api[0].Body![field]!.ToJsonString());
        Assert.True(api[0].Body!["trace"]!["values"]![1]!.GetValue<bool>());
        Assert.Null(api[0].Body!["headers"]);
        if (model == "text-to-voice")
        {
            var asset = Assert.Single(host.Requests, r => r.Host == "media.camb.ai");
            Assert.False(asset.Headers.ContainsKey("x-api-key"));
            Assert.False(asset.Headers.ContainsKey("Authorization"));
        }
    }

    [Fact]
    public async Task Cambai_transcription_preserves_segments_and_raw_result_and_can_frame_completed_text_as_sse()
    {
        await using var host = await FixtureHost.StartAsync(Reply, baseApiByteSpaceCambai: true);
        using var vercel = await host.PostAsync("/api/transcriptions", """
            {"model":"cambai/transcribe/source-language/1","audio":"AQID","mediaType":"audio/wav",
             "providerOptions":{"cambai":{"trace":{"level":2},"language":99}}}
            """, Cambai);
        var result = JsonNode.Parse(await GatewayTests.Success(vercel))!;
        Assert.Equal("hello", result["text"]!.GetValue<string>());
        Assert.Equal(1.25, result["segments"]![0]!["endSecond"]!.GetValue<double>());
        Assert.Equal("speaker_1", result["response"]!["body"]!["result"]![0]!["speaker"]!.GetValue<string>());
        var upload = host.Requests.First();
        Assert.Equal("/apis/transcribe", upload.Path);
        Assert.Contains("media_file", upload.RawBody);
        Assert.Contains("\"level\":2", upload.RawBody);
        Assert.DoesNotContain("\r\n99\r\n", upload.RawBody);

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("cambai/transcribe/source-language/1"), "model");
        form.Add(new StringContent("true"), "stream");
        form.Add(new ByteArrayContent([1, 2, 3]) { Headers = { ContentType = new MediaTypeHeaderValue("audio/wav") } }, "file", "fixture.wav");
        using var native = await host.PostFormAsync("/v1/audio/transcriptions", form, Cambai);
        var events = await GatewayTests.Success(native);
        Assert.Contains("transcript.text.delta", events);
        Assert.Contains("transcript.text.done", events);
        Assert.Contains("hello", events);
    }

    [Fact]
    public async Task Cambai_never_follows_an_untrusted_preview_or_completes_a_failed_job()
    {
        await using var host = await FixtureHost.StartAsync(r => r.Path switch
        {
            "/apis/text-to-voice-result/7" => FixtureHost.Json("""{"previews":["https://example.net/voice.mp3"]}"""),
            "/apis/translate/task-1" => FixtureHost.Json("""{"status":"ERROR","details":"not public"}"""),
            _ => Reply(r)
        }, baseApiByteSpaceCambai: true);
        using var preview = await host.PostAsync("/api/speech", """
            {"model":"cambai/text-to-voice","text":"hi","instructions":"calm"}
            """, Cambai);
        Assert.Equal(HttpStatusCode.BadGateway, preview.StatusCode);
        using var translation = await host.PostAsync("/v1/responses", $$"""
            {"model":"{{TranslationModel}}","input":"hi","stream":true}
            """, Cambai);
        Assert.Equal(HttpStatusCode.BadGateway, translation.StatusCode);
        Assert.DoesNotContain("not public", await translation.Content.ReadAsStringAsync());
        Assert.All(host.Requests, req => Assert.Equal("client.camb.ai", req.Host));
        Assert.DoesNotContain(host.Requests, req => req.Path == "/apis/translation-result/7");
    }

    private static HttpResponseMessage Reply(FixtureHost.UpstreamRequest request) => request.Path switch
    {
        "/apis/source-languages" => FixtureHost.Json("""[{"id":1,"language":"English"}]"""),
        "/apis/target-languages" => FixtureHost.Json("""[{"id":1,"language":"English"},{"id":2,"language":"Spanish"}]"""),
        "/apis/translate" or "/apis/tts" or "/apis/translated-tts" or "/apis/text-to-sound" or "/apis/text-to-voice" or "/apis/transcribe"
            => FixtureHost.Json("""{"task_id":"task-1","raw_create":true}"""),
        _ when request.Path.EndsWith("/task-1", StringComparison.Ordinal) => FixtureHost.Json("""{"status":"SUCCESS","run_id":7,"raw_status":{"stage":3}}"""),
        "/apis/translation-result/7" => FixtureHost.Json("""{"texts":["uno","dos"],"raw_result":[false,4]}"""),
        "/apis/transcription-result/7" => FixtureHost.Json("""[{"start":0,"end":1.25,"text":"hello","speaker":"speaker_1"}]"""),
        "/apis/text-to-voice-result/7" => FixtureHost.Json("""{"previews":["https://media.camb.ai/voice.mp3"]}"""),
        "/apis/tts-result/7" or "/apis/text-to-sound-result/7" or "/voice.mp3"
            => new(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) { Headers = { ContentType = new("audio/mpeg") } } },
        _ => FixtureHost.Reply(request)
    };
}
