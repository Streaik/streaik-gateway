using System.Net;
using System.Text.Json.Nodes;
using Xunit;

namespace Streaik.Gateway.Tests;

public sealed class CommonstackLeonardoNeuphonicTests
{
    private static readonly (string, string) Commonstack = ("x-commonstack-key", "common-fixture");
    private static readonly (string, string) Leonardo = ("x-leonardo-key", "leo-fixture");
    private static readonly (string, string) Neuphonic = ("x-neuphonic-key", "neu-fixture");

    [Fact]
    public async Task Discovery_uses_native_endpoints_and_gateway_model_ids()
    {
        await using var host = await FixtureHost.StartAsync(request => request.Host switch
        {
            "api.commonstack.ai" => FixtureHost.Json("""{"data":[{"id":"vendor/Model"},{"id":"uncommon-route/auto"}]}"""),
            "cloud.leonardo.ai" => FixtureHost.Json("""{"custom_models":[{"id":"platform-1","name":"Platform"}]}"""),
            "api.neuphonic.com" => FixtureHost.Json("""{"data":{"voices":[{"voice_id":"voice-1","lang_code":"en","name":"Fixture voice"}]}}"""),
            _ => FixtureHost.Reply(request)
        }, commonstackLeonardoNeuphonic: true);

        using var anonymous = await host.GetAsync("/v1/models");
        await GatewayTests.Success(anonymous);
        Assert.Empty(host.Requests);
        using var response = await host.GetAsync("/v1/models", Commonstack, Leonardo, Neuphonic);
        var models = JsonNode.Parse(await GatewayTests.Success(response))!["data"]!.AsArray().ToDictionary(model => (string)model!["id"]!);
        Assert.Contains("commonstack/vendor/Model", models.Keys);
        Assert.Contains("commonstack/uncommon-route/auto", models.Keys);
        Assert.Contains("commonstack/uncommon-route/best", models.Keys);
        Assert.Equal("image", (string?)models["leonardo/platform-1"]!["type"]);
        Assert.Equal("speech", (string?)models["neuphonic/en/voice-1"]!["type"]);
        Assert.Equal("Bearer common-fixture", Assert.Single(host.Requests, call => call.Path == "/v1/models").Headers["Authorization"]);
        Assert.Equal("Bearer leo-fixture", Assert.Single(host.Requests, call => call.Path == "/api/rest/v1/platformModels").Headers["Authorization"]);
        Assert.Equal("neu-fixture", Assert.Single(host.Requests, call => call.Path == "/voices").Headers["x-api-key"]);
    }

    [Theory]
    [InlineData("chat/completions", false)]
    [InlineData("chat/completions", true)]
    [InlineData("responses", false)]
    [InlineData("responses", true)]
    [InlineData("messages", false)]
    [InlineData("messages", true)]
    public async Task Commonstack_keeps_native_text_protocols_and_nested_extensions(string protocol, bool stream)
    {
        await using var host = await FixtureHost.StartAsync(commonstackLeonardoNeuphonic: true);
        var body = new JsonObject
        {
            ["model"] = "commonstack/vendor/Model", ["stream"] = stream,
            ["commonstack"] = JsonNode.Parse("""{"model":"wrong","stream":false,"headers":{"authorization":"wrong"},"route":{"prefer":["vendor",2]}}""")
        };
        if (protocol == "responses") body["input"] = "hi";
        else body["messages"] = JsonNode.Parse("""[{"role":"user","content":"hi"}]""");
        if (protocol == "messages") body["max_tokens"] = 64;
        using var response = await host.PostAsync($"/v1/{protocol}", body.ToJsonString(), Commonstack, ("anthropic-version", "2023-06-01"));
        var wire = await GatewayTests.Success(response);
        Assert.Contains("hello", wire);
        Assert.Contains(stream && protocol == "messages" ? "future_content" : "future_response", wire);
        var call = Assert.Single(host.Requests);
        Assert.Equal("api.commonstack.ai", call.Host);
        Assert.Equal($"/v1/{protocol}", call.Path);
        Assert.Equal("Bearer common-fixture", call.Headers["Authorization"]);
        Assert.Equal(stream, call.Stream);
        Assert.Equal("vendor/Model", (string?)call.Body!["model"]);
        Assert.Equal(2, (int)call.Body["route"]!["prefer"]![1]!);
        Assert.Null(call.Body["commonstack"]);
        Assert.Null(call.Body["headers"]);
        if (protocol == "messages") Assert.Equal("2023-06-01", call.Headers["anthropic-version"]);
    }

    [Fact]
    public async Task Commonstack_Vercel_chat_uses_Responses_without_unifying_requests()
    {
        await using var host = await FixtureHost.StartAsync(commonstackLeonardoNeuphonic: true);
        using var response = await host.PostAsync("/api/chat", """
            {"model":"commonstack/vendor/Model","messages":[{"id":"u1","role":"user","parts":[{"type":"text","text":"Hi"}]}],
             "providerMetadata":{"commonstack":{"route":{"prefer":["vendor"]}}}}
            """, Commonstack);
        Assert.Contains("hello", await GatewayTests.Success(response));
        var call = Assert.Single(host.Requests);
        Assert.Equal("/v1/responses", call.Path);
        Assert.True(call.Stream);
        Assert.Equal("vendor", (string?)call.Body!["route"]!["prefer"]![0]);
    }

    [Fact]
    public async Task Commonstack_forwards_images_and_multipart_edits_with_raw_diagnostics()
    {
        await using var host = await FixtureHost.StartAsync(_ => FixtureHost.Json("""
            {"created":1,"data":[{"b64_json":"AQID","quality_report":{"confidence":[0.7]}}],"trace":{"id":"image-fixture"}}
            """), commonstackLeonardoNeuphonic: true);
        using var image = await host.PostAsync("/api/images", """
            {"model":"commonstack/vendor/image","prompt":"Clouds","n":1,
             "providerOptions":{"commonstack":{"quality":"high","tune":{"steps":[1,2]}}}}
            """, Commonstack);
        var result = JsonNode.Parse(await GatewayTests.Success(image))!;
        Assert.Equal("data:image/png;base64,AQID", (string?)result["images"]![0]);
        var metadata = result["providerMetadata"]!["commonstack"]!;
        Assert.Equal("image-fixture", (string?)metadata["trace"]!["id"]);
        Assert.Equal(0.7, (double)metadata["data"]![0]!["quality_report"]!["confidence"]![0]!);
        Assert.Null(metadata["data"]![0]!["b64_json"]);

        using var edit = await host.PostAsync("/v1/images/edits", """
            {"model":"commonstack/vendor/image","prompt":"Clouds","stream":true,
             "images":[{"image_url":"data:image/png;base64,BAU="}],"mask":{"image_url":"data:image/png;base64,Aw=="},
             "commonstack":{"headers":{"authorization":"wrong"},"editing":{"modes":[1,true]}}}
            """, Commonstack);
        var wire = await GatewayTests.Success(edit);
        Assert.Contains("image_edit.completed", wire);
        Assert.Contains("image-fixture", wire);
        var calls = host.Requests.ToArray();
        Assert.Equal("/v1/openai/images/generations", calls[0].Path);
        Assert.Equal("high", (string?)calls[0].Body!["quality"]);
        Assert.Equal(2, (int)calls[0].Body!["tune"]!["steps"]![1]!);
        Assert.Equal("/v1/openai/images/edits", calls[1].Path);
        Assert.Contains("image[]", calls[1].RawBody);
        Assert.Contains("mask", calls[1].RawBody);
        Assert.Contains("{\"modes\":[1,true]}", calls[1].RawBody);
        Assert.DoesNotContain("wrong", calls[1].RawBody);
        Assert.DoesNotContain("name=stream", calls[1].RawBody);
    }

    [Theory]
    [InlineData("/api/images", "data:image/png;base64,AQID")]
    [InlineData("/api/chat", "data:image/png;base64,AQID")]
    [InlineData("/v1/images/generations", "image_generation.completed")]
    public async Task Leonardo_polls_and_returns_inline_images_and_metadata(string endpoint, string expected)
    {
        var polls = 0;
        await using var host = await FixtureHost.StartAsync(request => request.Path switch
        {
            "/api/rest/v1/generations" => FixtureHost.Json("""{"sdGenerationJob":{"generationId":"job-1"},"pricing":{"credits":2}}"""),
            "/api/rest/v1/generations/job-1" when endpoint == "/api/images" && Interlocked.Increment(ref polls) == 1
                => FixtureHost.Json("""{"generations_by_pk":{"status":"PENDING"}}"""),
            "/api/rest/v1/generations/job-1" => FixtureHost.Json("""
                {"generations_by_pk":{"status":"COMPLETE","generated_images":[{"url":"https://cdn.leonardo.ai/fixture.png?signature=fixture","seed_detail":{"selected":6}}]}}
                """),
            "/fixture.png?signature=fixture" => new(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) { Headers = { ContentType = new("image/png") } } },
            _ => FixtureHost.Reply(request)
        }, commonstackLeonardoNeuphonic: true);
        var body = endpoint switch
        {
            "/api/chat" => """
                {"model":"leonardo/platform-1","messages":[{"id":"u","role":"user","parts":[{"type":"text","text":"Clouds"}]}]}
                """,
            "/api/images" => """
                {"model":"leonardo/platform-1","prompt":"Clouds","size":"768x512","seed":6,"n":1,
                 "providerOptions":{"leonardo":{"modelId":"wrong","width":24,"guidance":{"weights":[2]}}}}
                """,
            _ => """{"model":"leonardo/platform-1","prompt":"Clouds","stream":true,"n":1}"""
        };
        using var response = await host.PostAsync(endpoint, body, Leonardo);
        var wire = await GatewayTests.Success(response);
        Assert.Contains(expected, wire);
        Assert.Contains("seed_detail", wire);
        Assert.DoesNotContain("signature=fixture", wire);
        var submit = Assert.Single(host.Requests, call => call.Method == "POST");
        Assert.Equal("Bearer leo-fixture", submit.Headers["Authorization"]);
        Assert.Equal("platform-1", (string?)submit.Body!["modelId"]);
        Assert.Equal("Clouds", (string?)submit.Body["prompt"]);
        if (endpoint == "/api/images")
        {
            Assert.Equal(2, polls);
            Assert.Equal(768, (int)submit.Body["width"]!);
            Assert.Equal(512, (int)submit.Body["height"]!);
            Assert.Equal(6, (int)submit.Body["seed"]!);
            Assert.Equal(2, (int)submit.Body["guidance"]!["weights"]![0]!);
        }
        var asset = Assert.Single(host.Requests, call => call.Host == "cdn.leonardo.ai");
        Assert.False(asset.Headers.ContainsKey("Authorization"));
        Assert.False(asset.Headers.ContainsKey("x-leonardo-key"));
    }

    [Fact]
    public async Task Leonardo_rejects_unknown_delivery_hosts_without_fetching()
    {
        await using var host = await FixtureHost.StartAsync(request => request.Method == "POST"
            ? FixtureHost.Json("""{"sd_generation_job":{"generation_id":"job-1"}}""")
            : FixtureHost.Json("""{"generations_by_pk":{"status":"COMPLETE","generated_images":[{"url":"https://unknown.example/fixture.png"}]}}"""),
            commonstackLeonardoNeuphonic: true);
        using var response = await host.PostAsync("/v1/images/generations", """{"model":"leonardo/platform-1","prompt":"Clouds"}""", Leonardo);
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(2, host.Requests.Count);
        Assert.All(host.Requests, call => Assert.Equal("cloud.leonardo.ai", call.Host));
    }

    [Fact]
    public async Task Neuphonic_supports_binary_speech_and_Vercel_streaming_with_raw_metadata()
    {
        await using var host = await FixtureHost.StartAsync(_ => Speech(), commonstackLeonardoNeuphonic: true);
        using var native = await host.PostAsync("/v1/audio/speech", """
            {"model":"neuphonic/en/voice-1","input":"hello","voice":"override","response_format":"pcm",
             "providerOptions":{"neuphonic":{"voice_id":"wrong","text":"wrong","hints":{"sounds":["a"]}}}}
            """, Neuphonic);
        Assert.Equal(HttpStatusCode.OK, native.StatusCode);
        Assert.Equal("audio/pcm", native.Content.Headers.ContentType?.MediaType);
        Assert.Equal(new byte[] { 1, 2, 3 }, await native.Content.ReadAsByteArrayAsync());

        using var streaming = await host.PostAsync("/api/speech/stream", """
            {"model":"neuphonic/en/voice-1","text":[{"type":"text","text":"Hel"},{"type":"text","text":"lo"}],
             "outputFormat":{"type":"pcm","rate":16000},"includeRawChunks":true,
             "providerOptions":{"neuphonic":{"hints":{"sounds":["b"]}}}}
            """, Neuphonic);
        var events = await GatewayTests.Success(streaming);
        Assert.Contains("\"type\":\"raw\"", events);
        Assert.Contains("AQI=", events);
        Assert.Contains("\"rate\":24000", events);
        Assert.Contains("\"type\":\"finish\"", events);
        Assert.Contains("timestamps", events);
        Assert.Contains("speech-fixture", events);
        Assert.DoesNotContain("private=fixture", events);
        var calls = host.Requests.ToArray();
        Assert.All(calls, call =>
        {
            Assert.Equal("api.neuphonic.com", call.Host);
            Assert.Equal("/sse/speak/en", call.Path);
            Assert.Equal("neu-fixture", call.Headers["x-api-key"]);
            Assert.Null(call.Body!["model"]);
        });
        Assert.Equal("hello", (string?)calls[0].Body!["text"]);
        Assert.Equal("override", (string?)calls[0].Body!["voice_id"]);
        Assert.Equal("a", (string?)calls[0].Body!["hints"]!["sounds"]![0]);
        Assert.Equal("Hello", (string?)calls[1].Body!["text"]);
        Assert.Equal(16000, (int)calls[1].Body!["sampling_rate"]!);
    }

    [Theory]
    [InlineData("/api/speech", """{"text":"hi"}""", "speech-fixture")]
    [InlineData("/api/chat", """{"messages":[{"id":"u","role":"user","parts":[{"type":"text","text":"hi"}]}]}""", "AQID")]
    [InlineData("/v1/chat/completions", """{"messages":[{"role":"user","content":"hi"}],"stream":true}""", "AQI=")]
    [InlineData("/v1/responses", """{"input":"hi","stream":true}""", "AQID")]
    [InlineData("/v1/messages", """{"messages":[{"role":"user","content":"hi"}],"max_tokens":32}""", "AQID")]
    [InlineData("/v1/audio/speech", """{"input":"hi","stream_format":"sse"}""", "speech.audio.done")]
    public async Task Neuphonic_reuses_existing_speech_and_chat_surfaces(string endpoint, string request, string expected)
    {
        await using var host = await FixtureHost.StartAsync(_ => Speech(), commonstackLeonardoNeuphonic: true);
        var body = JsonNode.Parse(request)!.AsObject();
        body["model"] = "neuphonic/en/voice-1";
        using var response = await host.PostAsync(endpoint, body.ToJsonString(), Neuphonic);
        Assert.Contains(expected, await GatewayTests.Success(response));
        Assert.Equal("/sse/speak/en", Assert.Single(host.Requests).Path);
    }

    [Fact]
    public async Task Neuphonic_does_not_emit_completion_for_an_upstream_error()
    {
        await using var host = await FixtureHost.StartAsync(_ => FixtureHost.Sse("""{"status_code":500,"errors":[{"message":"provider failure"}]}"""),
            commonstackLeonardoNeuphonic: true);
        using var response = await host.PostAsync("/v1/audio/speech", """{"model":"neuphonic/en/voice-1","input":"hi","stream_format":"sse"}""", Neuphonic);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain("speech.audio.done", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Neuphonic_rejects_voice_changes_across_streaming_text_parts()
    {
        await using var host = await FixtureHost.StartAsync(_ => Speech(), commonstackLeonardoNeuphonic: true);
        using var response = await host.PostAsync("/api/speech/stream", """
            {"model":"neuphonic/en/voice-1","text":[{"text":"hi"},{"text":"there","voice":"other"}]}
            """, Neuphonic);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Empty(host.Requests);
    }

    private static HttpResponseMessage Speech()
    {
        var response = FixtureHost.Sse(
            """{"status_code":200,"data":{"audio":"AQI=","sampling_rate":24000,"timestamps":[{"word":"hi","time":0.1}]}}""",
            """{"status_code":200,"data":{"audio":"Aw==","sampling_rate":24000}}""",
            "[DONE]");
        response.Headers.Add("x-request-id", "speech-fixture");
        response.Headers.Add("Set-Cookie", "private=fixture");
        return response;
    }
}
