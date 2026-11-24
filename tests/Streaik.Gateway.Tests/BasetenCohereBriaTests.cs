using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Streaik.Gateway.Sample;
using Streaik.OpenAI.Models;
using Streaik.Providers;

namespace Streaik.Gateway.Tests;

public sealed class BasetenCohereBriaTests
{
    private static readonly (string Name, string Value) Baseten = ("X-Baseten-Key", "baseten-key");
    private static readonly (string Name, string Value) Cohere = ("X-Cohere-Key", "cohere-key");
    private static readonly (string Name, string Value) Bria = ("X-Bria-Key", "bria-key");
    private const string SystemOneModel = "decision-models/baseten-elephant-9b-latest";

    [Fact]
    public async Task Provider_catalogs_are_key_scoped_and_publish_native_models_with_gateway_prefixes()
    {
        await using var host = await FixtureHost.StartAsync(req => (req.Host, req.Path) switch
        {
            ("inference.baseten.co", "/v1/models") => FixtureHost.Json("""{"data":[{"id":"vendor/chat","name":"Vendor Chat","context_length":4096,"pricing":{"prompt":"0.001","completion":0.002}}]}"""),
            ("api.cohere.com", "/v1/models?page_size=1000") => FixtureHost.Json("""{"models":[{"name":"embed-v4.0","features":["image"]}],"next_page_token":"next page"}"""),
            ("api.cohere.com", "/v1/models?page_size=1000&page_token=next%20page") => FixtureHost.Json("""{"models":[{"name":"cohere-transcribe"},{"name":"parse-document"}]}"""),
            _ => throw new InvalidOperationException(req.Path)
        }, basetenCohereBria: true);
        using var anonymous = await host.GetAsync("/v1/models");
        Assert.DoesNotContain("baseten/", await GatewayTests.Success(anonymous));
        Assert.Empty(host.Requests);
        using var response = await host.GetAsync("/v1/models", Baseten, Cohere, Bria);
        var models = (await Body(response))["data"]!.AsArray().ToDictionary(x => (string)x!["id"]!);
        Assert.Equal(4096, (int?)models["baseten/vendor/chat"]!["context_window"]);
        Assert.Equal("decision", (string?)models["baseten/" + SystemOneModel]!["type"]);
        Assert.Equal("embedding", (string?)models["cohere/embed-v4.0"]!["type"]);
        Assert.Equal("transcription", (string?)models["cohere/cohere-transcribe"]!["type"]);
        Assert.Equal("video", (string?)models["bria/video/edit/erase"]!["type"]);
        Assert.All(host.Requests, req => Assert.Equal(req.Host == "api.cohere.com" ? "Bearer cohere-key" : "Bearer baseten-key", req.Headers["Authorization"]));
    }

    [Fact]
    public async Task Baseten_serves_native_chat_and_adapts_existing_chat_protocols()
    {
        await using var host = await FixtureHost.StartAsync(req =>
        {
            Assert.Equal("inference.baseten.co", req.Host);
            Assert.Equal("/v1/chat/completions", req.Path);
            Assert.Equal("vendor/chat", (string?)req.Body!["model"]);
            Assert.Equal("Bearer baseten-key", req.Headers["Authorization"]);
            return req.Stream
                ? FixtureHost.Sse("""{"id":"b1","object":"chat.completion.chunk","created":1,"choices":[{"index":0,"delta":{"content":"hello"},"finish_reason":"stop"}],"provider_trace":{"nested":[4]}}""", "[DONE]")
                : FixtureHost.Json("""{"id":"b1","object":"chat.completion","created":1,"choices":[{"index":0,"message":{"role":"assistant","content":"hello"},"finish_reason":"stop"}],"provider_trace":{"nested":[4]}}""");
        }, basetenCohereBria: true);
        using var native = await host.PostAsync("/v1/chat/completions", """
            {"model":"baseten/vendor/chat","messages":[{"role":"user","content":"hi"}],"priority":{"custom":[3]}}
            """, Baseten, Cohere);
        Assert.Equal(4, (int?)(await Body(native))["provider_trace"]!["nested"]![0]);
        using var responses = await host.PostAsync("/v1/responses", """{"model":"baseten/vendor/chat","input":"hello"}""", Baseten);
        Assert.Contains("hello", await GatewayTests.Success(responses));
        using var messages = await host.PostAsync("/v1/messages", """{"model":"baseten/vendor/chat","max_tokens":30,"messages":[{"role":"user","content":"hi"}]}""", Baseten);
        Assert.Contains("hello", await GatewayTests.Success(messages));
        using var vercel = await host.PostAsync("/api/chat", """
            {"model":"baseten/vendor/chat","messages":[{"id":"u","role":"user","parts":[{"type":"text","text":"hi"}]}],
            "providerMetadata":{"baseten":{"future":{"array":["untouched",null]}}}}
            """, Baseten);
        var text = await GatewayTests.Success(vercel);
        Assert.Contains("provider_trace", text);
        Assert.Contains("hello", text);
        Assert.Equal("untouched", (string?)host.Requests.Last().Body!["future"]!["array"]![0]);
        Assert.Null(host.Requests.Last().Body!["future"]!["array"]![1]);
    }

    [Fact]
    public async Task Baseten_System_One_maps_both_decision_contracts_and_preserves_metadata()
    {
        await using var host = await FixtureHost.StartAsync(req =>
        {
            Assert.Equal("/v1/systemone", req.Path);
            var question = req.Body!["questions"]!.AsObject().Single();
            Assert.Equal("noul", (string?)question.Value!["type"]);
            Assert.Equal("priority", (string?)req.Body["service_tier"]);
            Assert.Equal(7, (int?)req.Body["future"]!["bits"]![0]);
            var response = FixtureHost.Json($$$"""
                {"model":"{{{SystemOneModel}}}","request_id":"sys-1","answers":{"{{{question.Key}}}":{"type":"noul","noul":0.75}},
                "usage":{"input_tokens":12,"output_tokens":1},"future":{"batch":[1,2]}}
                """);
            response.Headers.Add("x-request-id", "header-sys");
            return response;
        }, basetenCohereBria: true);

        using var native = await host.PostAsync("/v1/decisions", $$$"""
            {"model":"baseten/{{{SystemOneModel}}}","input":"Input","questions":[{"type":"predicate","name":"flag","instructions":"Flag?"}],
            "service_tier":"priority","future":{"bits":[7]}}
            """, Baseten);
        var openAI = await Body(native);
        Assert.Equal(0.75, (double?)openAI["answers"]![0]!["probability"]);
        Assert.Equal(13, (int?)openAI["usage"]!["total_tokens"]);
        Assert.Equal(2, (int?)openAI["providerMetadata"]!["baseten"]!["future"]!["batch"]![1]);

        using var sdk = await host.PostAsync("/api/decisions", $$$$$"""
            {"model":"baseten/{{{{{SystemOneModel}}}}}","stateFormat":"sdk","state":[{"type":"text","text":"Input"}],
            "questions":{"flag":{"type":"boolean","instructions":"Flag?"}},"providerOptions":{"baseten":{"service_tier":"priority","future":{"bits":[7]}}}}
            """, Baseten);
        var result = await Body(sdk);
        Assert.Equal("Input", (string?)host.Requests.Last().Body!["state"]!["messages"]![0]!["content"]![0]!["text"]);
        Assert.Equal("sys-1", (string?)result["response"]!["id"]);
        Assert.Contains(result["response"]!["headers"]!.AsObject(), entry => entry.Key.Equals("x-request-id", StringComparison.OrdinalIgnoreCase)
            && (string?)entry.Value == "header-sys");
        Assert.Equal(0.75, (double?)result["answers"]!["flag"]!["probability"]);
    }

    [Fact]
    public async Task Baseten_choices_preserve_value_types_and_scores_preserve_level_order()
    {
        await using var host = await FixtureHost.StartAsync(req =>
        {
            var questions = req.Body!["questions"]!;
            Assert.Equal("true", (string?)questions["question-0"]!["criteria"]!["option-0"]);
            Assert.Equal("true: string choice", (string?)questions["question-0"]!["criteria"]!["option-1"]);
            Assert.Equal("high", (string?)questions["question-1"]!["criteria"]![1]);
            return FixtureHost.Json("""
                {"model":"decision-models/baseten-elephant-9b-latest","answers":{
                "question-1":{"type":"score","score":0.65,"confidence":0.65,"legend":{"0":"low","1":"high"},"probabilities":{"0":0.35,"1":0.65}},
                "question-0":{"type":"choice","confidence":0.8,"choice":"option-0","probabilities":{"option-0":0.8,"option-1":0.2}}},
                "usage":{"input_tokens":5,"output_tokens":2}}
                """);
        }, basetenCohereBria: true);
        using var reply = await host.PostAsync("/v1/decisions", """
            {"model":"baseten/decision-models/baseten-elephant-9b-latest","input":"evidence","questions":[
            {"type":"choice","name":"choice","instructions":"Choose","choices":[{"value":true},{"value":"true","description":"string choice"}]},
            {"type":"score","name":"score","instructions":"Rate","levels":[{"label":"low"},{"label":"high"}]}]}
            """, Baseten);
        var answers = (await Body(reply))["answers"]!;
        Assert.True((bool?)answers[0]!["choice"]);
        Assert.Equal("true", (string?)answers[0]!["probabilities"]![1]!["value"]);
        Assert.Equal("high", (string?)answers[1]!["probabilities"]![1]!["label"]);
        Assert.Equal(0.65, (double?)answers[1]!["score"]);
    }

    [Fact]
    public async Task Cohere_chat_translates_tools_reasoning_citations_and_raw_usage()
    {
        await using var host = await FixtureHost.StartAsync(req =>
        {
            Assert.Equal("/v2/chat", req.Path);
            Assert.Equal("Bearer cohere-key", req.Headers["Authorization"]);
            var body = req.Body!;
            Assert.Equal(23, (int?)body["max_tokens"]);
            Assert.Equal("strict", (string?)body["citation_options"]!["mode"]);
            return req.Stream ? FixtureHost.Sse(
                """{"type":"message-start","id":"co1","delta":{"message":{"role":"assistant"}}}""",
                """{"type":"content-delta","index":0,"delta":{"message":{"content":{"thinking":"plan"}}}}""",
                """{"type":"content-delta","index":0,"delta":{"message":{"content":{"text":"hello"}}}}""",
                """{"type":"tool-call-start","index":0,"delta":{"message":{"tool_calls":{"id":"call1","type":"function","function":{"name":"lookup","arguments":""}}}}}""",
                """{"type":"tool-call-delta","index":0,"delta":{"message":{"tool_calls":{"function":{"arguments":"{}"}}}}}""",
                """{"type":"citation-start","index":0,"delta":{"message":{"citations":{"start":0,"end":5,"sources":[{"document":{"url":"https://example.org/ref","title":"Reference"},"future":[9]}]}}}}""",
                """{"type":"message-end","delta":{"finish_reason":"TOOL_CALL","usage":{"tokens":{"input_tokens":10,"output_tokens":3},"billed_units":{"input_tokens":2}},"extra":{"trace":[1]}}}""")
                : FixtureHost.Json("""
                    {"id":"co1","finish_reason":"TOOL_CALL","message":{"content":[{"type":"thinking","thinking":"plan"},{"type":"text","text":"hello"}],
                    "tool_calls":[{"id":"call1","type":"function","function":{"name":"lookup","arguments":"{}"}}],
                    "citations":[{"start":0,"end":5,"sources":[{"document":{"url":"https://example.org/ref","title":"Reference"}}]}]},
                    "usage":{"tokens":{"input_tokens":10,"output_tokens":3}},"future":{"trace":[1]}}
                    """);
        }, basetenCohereBria: true);

        using var native = await host.PostAsync("/v1/chat/completions", """
            {"model":"cohere/command-a","messages":[{"role":"user","content":"hi"}],"max_completion_tokens":23,
            "tools":[{"type":"function","function":{"name":"lookup","strict":true,"parameters":{"type":"object"}}}],
            "tool_choice":{"type":"function","function":{"name":"lookup"}},"citation_options":{"mode":"strict"}}
            """, Cohere);
        var json = await Body(native);
        var message = json["choices"]![0]!["message"]!;
        Assert.Equal("plan", (string?)message["reasoning_content"]);
        Assert.Equal("https://example.org/ref", (string?)message["annotations"]![0]!["url_citation"]!["url"]);
        Assert.Equal(1, (int?)json["providerMetadata"]!["cohere"]!["future"]!["trace"]![0]);
        Assert.Equal("REQUIRED", (string?)host.Requests.Single().Body!["tool_choice"]);
        Assert.True((bool?)host.Requests.Single().Body!["strict_tools"]);
        using var ui = await host.PostAsync("/api/chat", """
            {"model":"cohere/command-a","maxOutputTokens":23,"messages":[{"id":"u","role":"user","parts":[{"type":"text","text":"hi"}]}],
            "providerMetadata":{"cohere":{"citation_options":{"mode":"strict"}}}}
            """, Cohere);
        var stream = await GatewayTests.Success(ui);
        Assert.Contains("hello", stream);
        Assert.Contains("lookup", stream);
        Assert.Contains("reasoning-delta", stream);
        Assert.Contains("source-url", stream);
        Assert.Contains("billed_units", stream);
        Assert.Contains("https://example.org/ref", stream);
    }

    [Fact]
    public async Task Cohere_Parse_returns_markdown_and_preserves_the_document_response()
    {
        await using var host = await FixtureHost.StartAsync(req =>
        {
            Assert.Equal("/v2/parse", req.Path);
            Assert.Equal("data:application/pdf;base64,AQID", (string?)req.Body!["document"]!["image_url"]);
            Assert.Equal("markdown", (string?)req.Body["output_format"]);
            return FixtureHost.Json("""{"pages":[{"markdown":{"content":"# Report"},"future":{"bounding_box":[1,2]}}],"meta":{"tokens":{"input_tokens":4,"output_tokens":2}}}""");
        }, basetenCohereBria: true);
        using var response = await host.PostAsync("/v1/chat/completions", """
            {"model":"cohere/parse-document","messages":[{"role":"user","content":[{"type":"file","file":{"filename":"report.pdf","file_data":"data:application/pdf;base64,AQID"}}]}]}
            """, Cohere);
        var result = await Body(response);
        Assert.Equal("# Report", (string?)result["choices"]![0]!["message"]!["content"]);
        Assert.Equal(2, (int?)result["providerMetadata"]!["cohere"]!["documents"]![0]!["pages"]![0]!["future"]!["bounding_box"]![1]);
    }

    [Fact]
    public async Task Cohere_media_supports_multimodal_embedding_reranking_and_transcription_metadata()
    {
        await using var host = await FixtureHost.StartAsync(req => req.Path switch
        {
            "/v2/embed" when (string?)req.Body!["embedding_types"]![0] == "base64" => FixtureHost.Json("""{"embeddings":{"base64":["AQID"]},"meta":{"billed_units":{"input_tokens":3}}}"""),
            "/v2/embed" => FixtureHost.Json("""{"embeddings":{"float":[[1.25,2.5]]},"meta":{"billed_units":{"input_tokens":5},"future":{"units":[9]}}}"""),
            "/v2/rerank" => FixtureHost.Json("""{"id":"rank","results":[{"index":1,"relevance_score":0.8}],"meta":{"future":[7]}}"""),
            "/v2/audio/transcriptions" => FixtureHost.Json("""{"text":"hello","future":{"marks":[1]}}"""),
            _ => throw new InvalidOperationException(req.Path)
        }, basetenCohereBria: true);
        using var native = await host.PostAsync("/v1/embeddings", """{"model":"cohere/embed-v4","input":"text","encoding_format":"base64"}""", Cohere);
        Assert.Equal("AQID", (string?)(await Body(native))["data"]![0]!["embedding"]);
        using var multi = await host.PostAsync("/api/embeddings", """
            {"model":"cohere/embed-v4","values":[""],"dimensions":256,"providerOptions":{"cohere":{"input_type":"image","images":["data:image/png;base64,AQID"],"future":{"model_hint":3}}}}
            """, Cohere);
        var vectors = await Body(multi);
        Assert.Equal(1.25, (double?)vectors["embeddings"]![0]![0]);
        Assert.Equal(9, (int?)vectors["response"]!["body"]!["meta"]!["future"]!["units"]![0]);
        var embed = host.Requests.Last().Body!;
        Assert.Equal(256, (int?)embed["output_dimension"]);
        Assert.Null(embed["texts"]);
        Assert.Equal("data:image/png;base64,AQID", (string?)embed["images"]![0]);
        Assert.Equal(3, (int?)embed["future"]!["model_hint"]);

        using var rerank = await host.PostAsync("/api/rerank", """
            {"model":"cohere/rerank-v3.5","query":"find","documents":{"type":"text","values":["one","two"]},"topN":1,
            "providerOptions":{"cohere":{"max_tokens_per_doc":50,"future":{"hint":"retain"}}}}
            """, Cohere);
        var ranking = await Body(rerank);
        Assert.Equal(1, (int?)ranking["ranking"]![0]!["index"]);
        Assert.Equal(7, (int?)ranking["providerMetadata"]!["cohere"]!["meta"]!["future"]![0]);
        Assert.Equal("retain", (string?)host.Requests.Last().Body!["future"]!["hint"]);

        using var transcription = await host.PostAsync("/api/transcriptions", """
            {"model":"cohere/cohere-transcribe","audio":"AQID","mediaType":"audio/wav","providerOptions":{"cohere":{"language":"en","future":{"keep":[1]}}}}
            """, Cohere);
        var transcript = await Body(transcription);
        Assert.Equal("hello", (string?)transcript["text"]);
        Assert.Equal(1, (int?)transcript["providerMetadata"]!["cohere"]!["future"]!["marks"]![0]);
        Assert.Contains("{\"keep\":[1]}", host.Requests.Last().RawBody);
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("cohere/cohere-transcribe"), "model");
        form.Add(new StringContent("en"), "language");
        form.Add(new StringContent("true"), "stream");
        form.Add(new ByteArrayContent([1, 2, 3]) { Headers = { ContentType = new MediaTypeHeaderValue("audio/wav") } }, "file", "fixture.wav");
        using var sse = await host.PostFormAsync("/v1/audio/transcriptions", form, Cohere);
        var frames = await GatewayTests.Success(sse);
        Assert.Contains("transcript.text.delta", frames);
        Assert.Contains("transcript.text.done", frames);
    }

    [Fact]
    public async Task Bria_supports_async_image_edit_and_preserves_provider_metadata_without_asset_credentials()
    {
        await using var host = await FixtureHost.StartAsync(req => (req.Host, req.Path) switch
        {
            ("engine.prod.bria-api.com", "/v2/image/edit/gen_fill") => FixtureHost.Json("""{"status":"IN_PROGRESS","request_id":"image_123","status_url":"https://untrusted.example/status","submit_meta":[4]}""", HttpStatusCode.Accepted),
            ("engine.prod.bria-api.com", "/v2/status/image_123") => FixtureHost.Json("""{"status":"COMPLETED","result":{"image_url":"https://delivery.bria-api.com/image.png","extra":[{"seed":9}]}}"""),
            ("delivery.bria-api.com", "/image.png") => Asset("image/png"),
            _ => throw new InvalidOperationException(req.Host + req.Path)
        }, basetenCohereBria: true);
        using var reply = await host.PostAsync("/api/images", """
            {"model":"bria/edit/gen_fill","prompt":"add a tree","seed":7,"files":[{"type":"file","mediaType":"image/png","data":"AQID"}],
            "mask":{"type":"file","mediaType":"image/png","data":"BAUG"},"providerOptions":{"bria":{"generativeFill":{"refine_prompt":true},"future":{"options":[6]}}}}
            """, Bria, Cohere);
        var result = await Body(reply);
        Assert.Equal("data:image/png;base64,AQID", (string?)result["images"]![0]);
        Assert.Equal(9, (int?)result["providerMetadata"]!["bria"]!["result"]!["result"]!["extra"]![0]!["seed"]);
        Assert.Equal(4, (int?)result["providerMetadata"]!["bria"]!["submission"]!["submit_meta"]![0]);
        var submission = host.Requests.First();
        Assert.Equal("AQID", (string?)submission.Body!["image"]);
        Assert.Equal("BAUG", (string?)submission.Body["mask"]);
        Assert.Equal(7, (int?)submission.Body["seed"]);
        Assert.True((bool?)submission.Body["refine_prompt"]);
        Assert.Equal(6, (int?)submission.Body["future"]!["options"]![0]);
        foreach (var req in host.Requests.Take(2)) Assert.Equal("bria-key", req.Headers["api_token"]);
        Assert.Empty(host.Requests.Last().Headers);
    }

    [Fact]
    public async Task Bria_image_routes_reuse_chat_and_existing_image_completion_events()
    {
        await using var host = await FixtureHost.StartAsync(req => req.Path switch
        {
            "/v2/image/generate/lite" => FixtureHost.Json("""{"result":{"image_url":"https://engine.prod.bria-api.com/result.png"},"future":{"seed":9}}"""),
            "/result.png" => Asset("image/png"),
            _ => throw new InvalidOperationException(req.Path)
        }, basetenCohereBria: true);
        using var native = await host.PostAsync("/v1/images/generations", """{"model":"bria/generate/lite","prompt":"tree","stream":true}""", Bria);
        Assert.Contains("image_generation.completed", await GatewayTests.Success(native));
        using var ui = await host.PostAsync("/api/chat", """
            {"model":"bria/generate/lite","messages":[{"id":"u","role":"user","parts":[{"type":"text","text":"tree"}]}]}
            """, Bria);
        var stream = await GatewayTests.Success(ui);
        Assert.Contains("image/png", stream);
        Assert.Contains("AQID", stream);
        Assert.Contains("\"seed\":9", stream);
    }

    [Fact]
    public async Task Bria_video_jobs_use_protected_references_and_map_pending_completed_and_error()
    {
        var calls = 0;
        await using var host = await FixtureHost.StartAsync(req => req.Path switch
        {
            "/v2/video/edit/increase_resolution" => FixtureHost.Json("""{"request_id":"vid","status_url":"https://engine.prod.bria-api.com/v2/status/vid?version=2","extra":[6]}"""),
            "/v2/status/vid?version=2" => Interlocked.Increment(ref calls) switch
            {
                1 => FixtureHost.Json("""{"status":"IN_PROGRESS","request_id":"vid","progress":20}"""),
                2 => FixtureHost.Json("""{"status":"COMPLETED","result":{"video_url":"https://delivery.bria-api.com/result.mp4"},"details":{"steps":[2]}}"""),
                _ => FixtureHost.Json("""{"status":"ERROR","error":{"message":"video failed","code":"TOOL_ERROR"}}""")
            },
            "/result.mp4" => Asset("video/mp4"),
            _ => throw new InvalidOperationException(req.Path)
        }, configure: services => services.AddStreaikReferences(), basetenCohereBria: true);
        using var start = await host.PostAsync("/api/videos", """
            {"model":"bria/video/edit/increase_resolution","inputReferences":[{"type":"url","url":"https://example.com/clip.mp4"}],
            "generateAudio":true,"providerOptions":{"bria":{"desired_increase":2,"future":{"value":[3]}}}}
            """, Bria);
        var started = await Body(start);
        Assert.Equal(6, (int?)started["providerMetadata"]!["bria"]!["extra"]![0]);
        Assert.Equal("https://example.com/clip.mp4", (string?)host.Requests.First().Body!["video"]);
        Assert.True((bool?)host.Requests.First().Body!["preserve_audio"]);
        var operation = (string)started["operation"]!;
        Assert.StartsWith("bria/", operation);
        Assert.DoesNotContain("vid?version", operation);
        using var pending = await host.GetAsync("/api/videos/" + operation, Bria);
        Assert.Equal("pending", (string?)(await Body(pending))["status"]);
        using var complete = await host.GetAsync("/api/videos/" + operation, Bria);
        var completed = await Body(complete);
        Assert.Equal("completed", (string?)completed["status"]);
        Assert.Equal("AQID", (string?)completed["videos"]![0]!["data"]);
        Assert.Equal(2, (int?)completed["providerMetadata"]!["bria"]!["details"]!["steps"]![0]);
        Assert.Empty(host.Requests.Last().Headers);
        using var error = await host.GetAsync("/api/videos/" + operation, Bria);
        Assert.Equal("video failed", (string?)(await Body(error))["error"]);
    }

    [Fact]
    public async Task Bria_does_not_fetch_untrusted_assets()
    {
        await using var host = await FixtureHost.StartAsync(_ => FixtureHost.Json("""
            {"result":{"image_url":"https://somewhere.example/result.png"}}
            """), basetenCohereBria: true);
        using var response = await host.PostAsync("/v1/images/generations", """{"model":"bria/generate","prompt":"tree"}""", Bria);
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Single(host.Requests);
    }

    [Fact]
    public async Task Bria_image_cancellation_stops_the_inflight_status_poll()
    {
        var pollStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(
            [new("Providers:bria:ApiKey", "fixture")]).Build());
        services.AddBria();
        services.AddHttpClient("bria").ConfigurePrimaryHttpMessageHandler(() => new ProbeHandler(async (request, ct) =>
        {
            Interlocked.Increment(ref requests);
            if (request.Method == HttpMethod.Post) return FixtureHost.Json("""{"status":"IN_PROGRESS","request_id":"cancel_me"}""");
            pollStarted.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("The poll should have been canceled.");
        }));
        using var root = services.BuildServiceProvider();
        using var scope = root.CreateScope();
        var provider = scope.ServiceProvider.GetRequiredKeyedService<IImageProvider>("bria");
        using var cancel = new CancellationTokenSource();
        var generate = provider.GenerateImageAsync(new OpenAIImageGenerationRequest { Model = "generate", Prompt = "tree" },
            new Dictionary<string, string>(), cancel.Token);
        await pollStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => generate.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(2, requests);
    }

    [Fact]
    public async Task Provider_registration_does_not_change_the_openapi_contract()
    {
        await using var before = await FixtureHost.StartAsync(configure: s => s.AddOpenApi(GatewayOpenApi.Configure), configureApp: app => app.MapOpenApi());
        await using var after = await FixtureHost.StartAsync(configure: s => s.AddOpenApi(GatewayOpenApi.Configure), configureApp: app => app.MapOpenApi(), basetenCohereBria: true);
        var baseline = JsonNode.Parse(await before.Client.GetStringAsync("/openapi/v1.json"))!;
        var current = JsonNode.Parse(await after.Client.GetStringAsync("/openapi/v1.json"))!;
        Assert.True(JsonNode.DeepEquals(baseline["paths"], current["paths"]));
        Assert.True(JsonNode.DeepEquals(baseline["components"]?["schemas"], current["components"]?["schemas"]));
    }

    private static HttpResponseMessage Asset(string mime) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent([1, 2, 3]) { Headers = { ContentType = new MediaTypeHeaderValue(mime) } }
    };

    private static async Task<JsonNode> Body(HttpResponseMessage response) => JsonNode.Parse(await GatewayTests.Success(response))!;

    private sealed class ProbeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }
}
