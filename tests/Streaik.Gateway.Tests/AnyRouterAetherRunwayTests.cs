using System.Net;
using System.Text.Json.Nodes;

namespace Streaik.Gateway.Tests;

public sealed class AnyRouterAetherRunwayTests
{
    private static readonly (string, string) AnyRouter = ("X-Anyrouter-Key", "anyrouter-fixture");
    private static readonly (string, string) Aether = ("X-Aether-Key", "aether-fixture");
    private static readonly (string, string) Runway = ("X-Runway-Key", "runway-fixture");

    [Fact]
    public async Task Catalogs_preserve_native_ids_pricing_and_paginated_avatars()
    {
        await using var host = await FixtureHost.StartAsync(request => (request.Host, request.Path) switch
        {
            ("anyrouter.dev", "/api/v1/models") => FixtureHost.Json("""{"data":[{"id":"vendor/model","provider":"Vendor","context_length":4096}]}"""),
            ("api.aetherapi.dev", "/v1/models") => FixtureHost.Json("""{"data":[{"id":"vendor/model","context":"8192","input_cost":2,"output_cost":4}]}"""),
            ("api.dev.runwayml.com", "/v1/avatars") => FixtureHost.Json("""{"data":[],"hasMore":true,"nextCursor":"c+2"}"""),
            ("api.dev.runwayml.com", "/v1/avatars?cursor=c%2B2") => FixtureHost.Json("""
                {"data":[{"id":"1507c0f0-1a0f-4269-a5d4-5244c2ac7141","name":"Demo","voice":{"presetId":"Voice"}}],"hasMore":false}
                """),
            _ => throw new InvalidOperationException(request.Path)
        }, anyRouterAetherRunway: true);
        using var noKeys = await host.GetAsync("/v1/models");
        Assert.Empty((await Body(noKeys))["data"]!.AsArray());
        Assert.Empty(host.Requests);
        using var response = await host.GetAsync("/v1/models", AnyRouter, Aether, Runway);
        var models = (await Body(response))["data"]!.AsArray().ToDictionary(x => (string)x!["id"]!);
        Assert.Equal("Vendor", (string?)models["anyrouter/vendor/model"]!["owned_by"]);
        Assert.Equal("decision", (string?)models["anyrouter/anyrouter/decision"]!["type"]);
        Assert.Equal(8192, (int)models["aether/vendor/model"]!["context_window"]!);
        Assert.Equal(0.000002m, (decimal)models["aether/vendor/model"]!["pricing"]!["input"]!);
        Assert.Equal("image", (string?)models["runway/gen4_image"]!["type"]);
        Assert.Equal("Avatar: Demo", (string?)models["runway/avatar/1507c0f0-1a0f-4269-a5d4-5244c2ac7141"]!["name"]);
        Assert.All(host.Requests.Where(r => r.Host == "api.dev.runwayml.com"), r => Assert.Equal("2024-11-06", r.Headers["X-Runway-Version"]));
    }

    [Fact]
    public async Task AnyRouter_uses_native_chat_responses_and_messages()
    {
        await using var host = await FixtureHost.StartAsync(request => request.Path switch
        {
            "/api/v1/chat/completions" => FixtureHost.Sse("""
                {"id":"a","object":"chat.completion.chunk","created":1,"choices":[{"index":0,"delta":{"role":"assistant","content":"native chat"},"finish_reason":"stop"}],"native":{"trace":[true,null]}}
                """, "[DONE]"),
            "/api/v1/responses" => FixtureHost.Json("""{"id":"resp_1","status":"completed","created_at":1,"output":[],"native":{"trace":[true,null]}}"""),
            "/api/v1/messages" => FixtureHost.Json("""
                {"id":"msg_1","type":"message","role":"assistant","model":"vendor/model","content":[{"type":"text","text":"native messages"}],"stop_reason":"end_turn","usage":{"input_tokens":1,"output_tokens":2},"native":{"trace":[true,null]}}
                """),
            _ => throw new InvalidOperationException(request.Path)
        }, anyRouterAetherRunway: true);
        using var chat = await host.PostAsync("/v1/chat/completions", """
            {"model":"anyrouter/vendor/model","stream":true,"messages":[{"role":"user","content":"hello"}],
             "anyrouter":{"native_options":{"v":[2,null]},"model":"not-this","stream":false,"authorization":"not-this"}}
            """, AnyRouter);
        var chunks = await GatewayTests.Success(chat);
        Assert.Contains("native chat", chunks);
        Assert.Contains("\"trace\":[true,null]", chunks);
        using var responses = await host.PostAsync("/v1/responses", """
            {"model":"anyrouter/vendor/model","input":"hello","anyrouter":{"native_options":{"v":[2,null]}}}
            """, AnyRouter);
        Assert.True((bool)(await Body(responses))["native"]!["trace"]![0]!);
        using var messages = await host.PostAsync("/v1/messages", """
            {"model":"anyrouter/vendor/model","max_tokens":100,"messages":[{"role":"user","content":"hello"}],
             "anyrouter":{"native_options":{"v":[2,null]}}}
            """, AnyRouter, ("anthropic-version", "2023-06-01"));
        Assert.True((bool)(await Body(messages))["native"]!["trace"]![0]!);
        Assert.All(host.Requests, sent =>
        {
            Assert.Equal("anyrouter.dev", sent.Host);
            Assert.Equal("Bearer anyrouter-fixture", sent.Headers["Authorization"]);
            Assert.Equal("vendor/model", (string?)sent.Body!["model"]);
            Assert.Null(sent.Body["authorization"]);
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"v":[2,null]}"""), sent.Body["native_options"]));
        });
        Assert.True(host.Requests.First().Stream);
        Assert.Equal("2023-06-01", host.Requests.Last().Headers["anthropic-version"]);
    }

    [Fact]
    public async Task AnyRouter_rerank_and_decisions_return_raw_results()
    {
        await using var host = await FixtureHost.StartAsync(request => request.Path switch
        {
            "/api/v1/rerank" => FixtureHost.Json("""{"id":"rank","results":[{"index":0,"relevance_score":0.1},{"index":1,"relevance_score":0.9,"document":{"text":"second"}}],"native":{"x":1}}"""),
            "/api/v1/decisions" => FixtureHost.Json("""
                {"model":"typesafe/jev","answers":{"flag":{"type":"noul","noul":0.7},
                   "grade":{"type":"score","confidence":0.8,"score":0.75,"probabilities":{"0":0.25,"1":0.75},"legend":{"0":"low","1":"high"}}},
                 "usage":{"input_tokens":5},"native":{"trace":[false,null]}}
                """),
            _ => throw new InvalidOperationException(request.Path)
        }, anyRouterAetherRunway: true);
        using var reranked = await host.PostAsync("/api/rerank", """
            {"model":"anyrouter/cohere/rerank-v3.5","query":"q","topN":1,"documents":{"type":"text","values":["first",{"text":"second"}]},
             "providerOptions":{"anyrouter":{"return_documents":true,"future_option":{"size":2}}}}
            """, AnyRouter);
        var ranking = await Body(reranked);
        Assert.Equal(1, (int)ranking["ranking"]![0]!["index"]!);
        Assert.Equal(1, (int)ranking["providerMetadata"]!["anyrouter"]!["native"]!["x"]!);
        using var decide = await host.PostAsync("/api/decisions", """
            {"model":"anyrouter/typesafe/jev","state":{"stateful":[true,null]},"questions":{
             "flag":{"type":"boolean","instructions":"Check"},"grade":{"type":"score","instructions":"Grade","criteria":["low","high"]}}}
            """, AnyRouter);
        var decision = await Body(decide);
        Assert.Equal(0.7, (double)decision["answers"]!["flag"]!["probability"]!);
        Assert.Equal(0.75, (double)decision["answers"]!["grade"]!["score"]!);
        Assert.False((bool)decision["providerMetadata"]!["anyrouter"]!["native"]!["trace"]![0]!);
        Assert.Equal(2, (int)host.Requests.First().Body!["future_option"]!["size"]!);
        Assert.Equal("noul", (string?)host.Requests.Last().Body!["questions"]!["flag"]!["type"]);
        Assert.True((bool)host.Requests.Last().Body!["state"]!["stateful"]![0]!);
    }

    [Theory]
    [InlineData("option-1", true)]
    [InlineData("option-0", false)]
    public async Task AnyRouter_OpenAI_decisions_map_choice_values_and_reject_inconsistent_answers(string choice, bool valid)
    {
        await using var host = await FixtureHost.StartAsync(_ => FixtureHost.Json($$$$"""
            {"model":"typesafe/jev","answers":{"question-0":{"type":"choice","choice":"{{{{choice}}}}","confidence":0.8,
             "probabilities":{"option-0":0.2,"option-1":0.8}}},"usage":{"input_tokens":3,"output_tokens":2},"native":{"x":1}}
            """), anyRouterAetherRunway: true);
        using var response = await host.PostAsync("/v1/decisions", """
            {"model":"anyrouter/typesafe/jev","input":"Evidence","questions":[
             {"type":"choice","name":"pick","instructions":"Choose","choices":[{"value":"no"},{"value":true,"description":"Yes"}]}]}
            """, AnyRouter);
        if (!valid)
        {
            Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
            return;
        }
        var result = await Body(response);
        Assert.True((bool)result["answers"]![0]!["choice"]!);
        Assert.Equal(5, (int)result["usage"]!["total_tokens"]!);
        Assert.Equal(1, (int)result["providerMetadata"]!["anyrouter"]!["native"]!["x"]!);
        Assert.Equal("Yes", (string?)Assert.Single(host.Requests).Body!["questions"]!["question-0"]!["criteria"]!["option-1"]);
    }

    [Theory]
    [InlineData("anyrouter", "/api/v1/embeddings")]
    [InlineData("aether", "/v1/embeddings")]
    public async Task Sdk_embeddings_keep_native_extension_data(string provider, string upstreamPath)
    {
        await using var host = await FixtureHost.StartAsync(_ => FixtureHost.Json("""
            {"model":"embed","data":[{"index":0,"embedding":[0.25,0.75],"object":"embedding"}],"usage":{"prompt_tokens":1,"total_tokens":1},"unmodeled":{"version":2}}
            """), anyRouterAetherRunway: true);
        using var response = await host.PostAsync("/api/embeddings", $"{{\"model\":\"{provider}/embed\",\"values\":[\"hello\"]}}", provider == "aether" ? Aether : AnyRouter);
        var result = await Body(response);
        Assert.Equal(2, (int)result["response"]!["body"]!["unmodeled"]!["version"]!);
        Assert.Equal(upstreamPath, Assert.Single(host.Requests).Path);
    }

    [Fact]
    public async Task Aether_bridges_messages_via_chat_and_keeps_image_delivery_separate()
    {
        await using var host = await FixtureHost.StartAsync(request => request.Path switch
        {
            "/v1/models" => FixtureHost.Json("""{"data":[{"id":"vendor/text"}]}"""),
            "/v1/chat/completions" => FixtureHost.Json("""
                {"id":"chat_1","created":1,"object":"chat.completion","choices":[{"index":0,"message":{"role":"assistant","content":"bridged"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":2,"total_tokens":3}}
                """),
            "/v1/images/generations" => FixtureHost.Json("""
                {"created":1,"data":[{"url":"https://cdn.example/image.png","native_item":{"v":3}}],"upstream_meta":{"x":[true,null]}}
                """),
            "/image.png" => Binary("image/png"),
            _ => throw new InvalidOperationException(request.Path)
        }, anyRouterAetherRunway: true);
        using var messages = await host.PostAsync("/v1/messages", """
            {"model":"aether/vendor/text","messages":[{"role":"user","content":"hello"}],"max_tokens":100}
            """, Aether);
        Assert.Contains("bridged", await GatewayTests.Success(messages));
        using var image = await host.PostAsync("/v1/images/generations", """
            {"model":"aether/dall-e-3","prompt":"A river","stream":true,"aether":{"extra_style":{"v":1}}}
            """, Aether);
        var events = await GatewayTests.Success(image);
        Assert.Contains("image_generation.completed", events);
        Assert.Contains("\"b64_json\":\"AQID\"", events);
        Assert.Contains("\"native_item\":{\"v\":3}", events);
        var delivery = Assert.Single(host.Requests, r => r.Host == "cdn.example");
        Assert.False(delivery.Headers.ContainsKey("Authorization"));
        var generation = Assert.Single(host.Requests, r => r.Path == "/v1/images/generations");
        Assert.Equal(1, (int)generation.Body!["extra_style"]!["v"]!);
        Assert.Equal("b64_json", (string?)generation.Body["response_format"]);
    }

    [Fact]
    public async Task Aether_transcription_and_speech_support_the_existing_stream_contracts()
    {
        await using var host = await FixtureHost.StartAsync(r => r.Path switch
        {
            "/v1/audio/transcriptions" => FixtureHost.Json("""
                {"text":"hello","usage":{"seconds":1.25},"segments":[{"text":"hello","start_second":0,"end_second":1.25,"marker":[true,null]}]}
                """),
            "/v1/audio/speech" => Binary("audio/mpeg"),
            _ => throw new InvalidOperationException(r.Path)
        }, anyRouterAetherRunway: true);
        using var sdk = await host.PostAsync("/api/transcriptions", """
            {"model":"aether/whisper-1","audio":"AQID","mediaType":"audio/wav","providerOptions":{"aether":{"language":"en","temperature":0.2}}}
            """, Aether);
        var transcript = await Body(sdk);
        Assert.Equal(1.25, (double)transcript["durationInSeconds"]!);
        Assert.True((bool)transcript["providerMetadata"]!["aether"]!["segments"]![0]!["marker"]![0]!);
        Assert.Contains("name=temperature", Assert.Single(host.Requests).RawBody);
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("aether/whisper-1"), "model");
        form.Add(new StringContent("true"), "stream");
        form.Add(new ByteArrayContent([1, 2, 3]) { Headers = { ContentType = new("audio/wav") } }, "file", "audio.wav");
        using var streamed = await host.PostFormAsync("/v1/audio/transcriptions", form, Aether);
        Assert.Contains("transcript.text.done", await GatewayTests.Success(streamed));
        using var speech = await host.PostAsync("/v1/audio/speech", """
            {"model":"aether/tts-1","input":"hello","voice":"alloy","stream_format":"sse","aether":{"native_knob":{"x":2}}}
            """, Aether);
        var audio = await GatewayTests.Success(speech);
        Assert.Contains("\"audio\":\"AQID\"", audio);
        Assert.Equal(2, (int)host.Requests.Last().Body!["native_knob"]!["x"]!);
    }

    [Fact]
    public async Task Runway_uses_task_metadata_for_image_edits_and_sound_effects()
    {
        await using var host = await FixtureHost.StartAsync(r => r.Path switch
        {
            "/v1/text_to_image" => FixtureHost.Json("""{"id":"image"}"""),
            "/v1/sound_effect" => FixtureHost.Json("""{"id":"effect"}"""),
            "/v1/tasks/image" => FixtureHost.Json("""{"id":"image","status":"SUCCEEDED","output":["https://cdn.example/image.png"],"review":{"value":[null,1]}}"""),
            "/v1/tasks/effect" => FixtureHost.Json("""{"id":"effect","status":"SUCCEEDED","output":[{"uri":"https://cdn.example/audio.mp3"}],"review":{"value":[null,2]}}"""),
            "/image.png" => Binary("image/png"),
            "/audio.mp3" => Binary("audio/mpeg"),
            _ => throw new InvalidOperationException(r.Path)
        }, anyRouterAetherRunway: true);
        using var image = await host.PostAsync("/v1/images/edits", """
            {"model":"runway/gen4_image","prompt":"Paint it","images":[{"image_url":"data:image/png;base64,AQID"}],
             "providerOptions":{"runway":{"contentModeration":{"publicFigureThreshold":"auto","future":true},"seed":22,"model":"wrong"}}}
            """, Runway);
        var generated = await Body(image);
        Assert.Equal("AQID", (string?)generated["data"]![0]!["b64_json"]);
        Assert.Equal(1, (int)generated["providerMetadata"]!["runway"]!["review"]!["value"]![1]!);
        using var sound = await host.PostAsync("/api/speech", """
            {"model":"runway/eleven_text_to_sound_v2","text":"a splash","providerOptions":{"runway":{"soundEffects":{"duration":2.5,"loop":true,"native":null}}}}
            """, Runway);
        var effect = await Body(sound);
        Assert.Equal("AQID", (string?)effect["audio"]!["base64"]);
        Assert.Equal(2, (int)effect["providerMetadata"]!["runway"]!["review"]!["value"]![1]!);
        var submitted = Assert.Single(host.Requests, r => r.Path == "/v1/text_to_image");
        Assert.Equal("gen4_image", (string?)submitted.Body!["model"]);
        Assert.Equal("data:image/png;base64,AQID", (string?)submitted.Body["referenceImages"]![0]!["uri"]);
        Assert.True((bool)submitted.Body["contentModeration"]!["future"]!);
        var soundRequest = Assert.Single(host.Requests, r => r.Path == "/v1/sound_effect");
        Assert.Equal(2.5, (double)soundRequest.Body!["duration"]!);
        Assert.True((bool)soundRequest.Body["loop"]!);
        Assert.All(host.Requests.Where(r => r.Host == "cdn.example"), r => Assert.False(r.Headers.ContainsKey("Authorization")));
    }

    [Theory]
    [InlineData("gen4.5", "\"prompt\":\"Move\"", "text_to_video")]
    [InlineData("gen4.5", "\"image\":{\"type\":\"file\",\"mediaType\":\"image/png\",\"data\":\"AQID\"}", "image_to_video")]
    [InlineData("aleph2", "\"image\":{\"type\":\"file\",\"mediaType\":\"video/mp4\",\"data\":\"AQID\"}", "video_to_video")]
    [InlineData("avatar/tennis-coach", "\"prompt\":\"Game on\"", "avatar_videos")]
    [InlineData("magnific_video_upscaler_creative", "\"inputReferences\":[{\"type\":\"file\",\"mediaType\":\"video/mp4\",\"data\":\"AQID\"}]", "video_upscale")]
    public async Task Runway_routes_video_operations_and_preserves_each_task_state(string model, string fields, string path)
    {
        var polls = 0;
        await using var host = await FixtureHost.StartAsync(r => r.Path switch
        {
            var p when p == "/v1/" + path => FixtureHost.Json("""{"id":"fixture-task","native":{"start":true}}"""),
            "/v1/tasks/fixture-task" when Interlocked.Increment(ref polls) == 1 => FixtureHost.Json("""{"status":"RUNNING","progress":0.5}"""),
            "/v1/tasks/fixture-task" => FixtureHost.Json("""{"status":"SUCCEEDED","output":["https://cdn.example/out.mp4"],"native":{"finish":[1,null]}}"""),
            "/out.mp4" => Binary("video/mp4"),
            _ => throw new InvalidOperationException(r.Path)
        }, configure: services => services.AddStreaikReferences(), anyRouterAetherRunway: true);
        using var started = await host.PostAsync("/api/videos", $"{{\"model\":\"runway/{model}\",{fields},\"providerOptions\":{{\"runway\":{{\"future\":{{\"level\":3}},\"model\":\"wrong\"}}}}}}", Runway);
        var start = await Body(started);
        Assert.True((bool)start["providerMetadata"]!["runway"]!["native"]!["start"]!);
        var operation = (string)start["operation"]!;
        Assert.StartsWith("runway/", operation);
        Assert.DoesNotContain("fixture-task", operation);
        using var pending = await host.GetAsync("/api/videos/" + operation, Runway);
        Assert.Equal(0.5, (double)(await Body(pending))["providerMetadata"]!["runway"]!["progress"]!);
        using var complete = await host.GetAsync("/api/videos/" + operation, Runway);
        var result = await Body(complete);
        Assert.Equal("AQID", (string?)result["videos"]![0]!["data"]);
        Assert.Equal(1, (int)result["providerMetadata"]!["runway"]!["native"]!["finish"]![0]!);
        Assert.Equal("runway/" + model, (string?)result["response"]!["modelId"]);
        Assert.Equal(3, (int)host.Requests.First().Body!["future"]!["level"]!);
    }

    [Fact]
    public async Task Runway_rejects_bad_sound_options_and_untrusted_output_urls()
    {
        await using var host = await FixtureHost.StartAsync(r => r.Path switch
        {
            "/v1/text_to_speech" => FixtureHost.Json("""{"id":"unsafe"}"""),
            "/v1/tasks/unsafe" => FixtureHost.Json("""{"status":"SUCCEEDED","output":["https://127.0.0.1/audio.mp3"]}"""),
            _ => throw new InvalidOperationException(r.Path)
        }, anyRouterAetherRunway: true);
        using var invalid = await host.PostAsync("/api/speech", """
            {"model":"runway/eleven_text_to_sound_v2","text":"splash","providerOptions":{"runway":{"soundEffects":{"duration":31}}}}
            """, Runway);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Empty(host.Requests);
        using var unsafeAsset = await host.PostAsync("/api/speech", """
            {"model":"runway/eleven_v3","text":"hello","providerOptions":{"runway":{"voice":{"presetId":"Voice","custom":{"x":1}}}}}
            """, Runway);
        Assert.Equal(HttpStatusCode.BadGateway, unsafeAsset.StatusCode);
        Assert.Equal(2, host.Requests.Count);
        Assert.Equal("runway-preset", (string?)host.Requests.First().Body!["voice"]!["type"]);
        Assert.Equal(1, (int)host.Requests.First().Body!["voice"]!["custom"]!["x"]!);
    }

    [Fact]
    public async Task Runway_Vercel_video_chat_reuses_the_media_adapter()
    {
        await using var host = await FixtureHost.StartAsync(r => r.Path switch
        {
            "/v1/text_to_video" => FixtureHost.Json("""{"id":"video"}"""),
            "/v1/tasks/video" => FixtureHost.Json("""{"status":"SUCCEEDED","output":["https://cdn.example/out.mp4"],"extra":{"native":true}}"""),
            "/out.mp4" => Binary("video/mp4"),
            _ => throw new InvalidOperationException(r.Path)
        }, configure: services => services.AddStreaikReferences(), anyRouterAetherRunway: true);
        using var response = await host.PostAsync("/api/chat", """
            {"model":"runway/gen4.5","messages":[{"id":"user","role":"user","parts":[{"type":"text","text":"Move"}]}],
             "providerMetadata":{"runway":{"ratio":"1920:1080"}}}
            """, Runway);
        var frames = await GatewayTests.Success(response);
        Assert.Contains("tool-output-available", frames);
        Assert.Contains("\"extra\":{\"native\":true}", frames);
        Assert.Contains("AQID", frames);
        Assert.Equal("1920:1080", (string?)host.Requests.First().Body!["ratio"]);
    }

    private static HttpResponseMessage Binary(string type) => new(HttpStatusCode.OK)
        { Content = new ByteArrayContent([1, 2, 3]) { Headers = { ContentType = new(type) } } };

    private static async Task<JsonNode> Body(HttpResponseMessage response) => JsonNode.Parse(await GatewayTests.Success(response))!;
}
