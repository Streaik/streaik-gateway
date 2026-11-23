using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace Streaik.Gateway.Tests;

public sealed class MoonshotJinaBlackForestLabsTests
{
    private static readonly (string, string) MoonshotKey = ("x-moonshot-key", "test-moonshot");
    private static readonly (string, string) JinaKey = ("x-jina-key", "test-jina");
    private static readonly (string, string) BflKey = ("x-blackforestlabs-key", "test-bfl");

    [Fact]
    public async Task Models_combine_live_Moonshot_and_static_Jina_and_FLUX_catalogs()
    {
        await using var host = await FixtureHost.StartAsync(request =>
        {
            Assert.Equal("api.moonshot.ai", request.Host);
            Assert.Equal("/v1/models", request.Path);
            Assert.Equal("Bearer test-moonshot", request.Headers["Authorization"]);
            return FixtureHost.Json("""{"data":[{"id":"kimi-fixture","owned_by":"moonshot","context_length":256000,"pricing":{"input":"0.000001","output":0.000002}}]}""");
        }, moonshotJinaBlackForestLabs: true);
        using var response = await host.GetAsync("/v1/models", MoonshotKey);
        var models = JsonNode.Parse(await GatewayTests.Success(response))!["data"]!.AsArray();
        var moonshot = Assert.Single(models, model => (string?)model?["id"] == "moonshot/kimi-fixture")!;
        Assert.Equal(256000, (int?)moonshot["context_window"]);
        Assert.Equal("0.000001", moonshot["pricing"]!["input"]!.ToString());
        Assert.Contains(models, model => (string?)model?["id"] == "jina/jina-embeddings-v4" && (string?)model?["type"] == "embedding");
        Assert.Contains(models, model => (string?)model?["id"] == "blackforestlabs/flux-tools/video-edit-v1" && (string?)model?["type"] == "video");
        Assert.Single(host.Requests);
    }

    [Fact]
    public async Task Moonshot_native_chat_forwards_tools_and_preserves_nested_extension_data()
    {
        await using var host = await FixtureHost.StartAsync(request =>
        {
            Assert.Equal("api.moonshot.ai", request.Host);
            Assert.Equal("/v1/chat/completions", request.Path);
            Assert.Equal("Bearer test-moonshot", request.Headers["Authorization"]);
            Assert.Equal("kimi-k2.5", (string?)request.Body!["model"]);
            Assert.Equal("disabled", (string?)request.Body["thinking"]!["type"]);
            Assert.Equal("lookup", (string?)request.Body["tools"]![0]!["function"]!["name"]);
            return FixtureHost.Json("""
                {"id":"result","created":123,"model":"kimi-k2.5","choices":[{"index":0,"message":{"role":"assistant","content":null,"reasoning_content":"inspect","tool_calls":[{"id":"call1","type":"function","function":{"name":"lookup","arguments":"{}"}}]},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":3,"completion_tokens":2,"total_tokens":5,"billing":{"tier":"test"}},"provider_trace":{"nodes":[{"id":7}]}}
                """);
        }, moonshotJinaBlackForestLabs: true);
        using var response = await host.PostAsync("/v1/chat/completions", """
            {"model":"moonshot/kimi-k2.5","messages":[{"role":"user","content":"lookup"}],"thinking":{"type":"disabled"},"tools":[{"type":"function","function":{"name":"lookup","parameters":{"type":"object"}}}]}
            """, MoonshotKey);
        var body = JsonNode.Parse(await GatewayTests.Success(response))!;
        Assert.Equal("inspect", (string?)body["choices"]![0]!["message"]!["reasoning_content"]);
        Assert.Equal("lookup", (string?)body["choices"]![0]!["message"]!["tool_calls"]![0]!["function"]!["name"]);
        Assert.Equal("test", (string?)body["usage"]!["billing"]!["tier"]);
        Assert.Equal(7, (int?)body["provider_trace"]!["nodes"]![0]!["id"]);
    }

    [Theory]
    [InlineData("/api/chat", "{\"model\":\"jina/jina-deepsearch-v1\",\"messages\":[{\"id\":\"m\",\"role\":\"user\",\"parts\":[{\"type\":\"text\",\"text\":\"find\"}]}],\"providerMetadata\":{\"jina\":{\"search_budget\":{\"limit\":3},\"model\":\"override\"}}}", true)]
    [InlineData("/v1/messages", "{\"model\":\"jina/jina-deepsearch-v1\",\"messages\":[{\"role\":\"user\",\"content\":\"find\"}],\"max_tokens\":32,\"stream\":true}", true)]
    [InlineData("/v1/responses", "{\"model\":\"jina/jina-deepsearch-v1\",\"input\":\"find\",\"stream\":true}", true)]
    [InlineData("/v1/messages", "{\"model\":\"jina/jina-deepsearch-v1\",\"messages\":[{\"role\":\"user\",\"content\":\"find\"}],\"max_tokens\":32}", false)]
    [InlineData("/v1/responses", "{\"model\":\"jina/jina-deepsearch-v1\",\"input\":\"find\"}", false)]
    public async Task Jina_chat_bridges_retain_native_search_diagnostics(string route, string payload, bool stream)
    {
        await using var host = await FixtureHost.StartAsync(request =>
        {
            Assert.Equal("deepsearch.jina.ai", request.Host);
            Assert.Equal("/v1/chat/completions", request.Path);
            Assert.Equal("Bearer test-jina", request.Headers["Authorization"]);
            Assert.Equal("jina-deepsearch-v1", (string?)request.Body!["model"]);
            if (route == "/api/chat") Assert.Equal(3, (int?)request.Body!["search_budget"]!["limit"]);
            Assert.Equal(stream, request.Stream);
            return stream ? FixtureHost.Sse(
                """{"id":"result","model":"jina-deepsearch-v1","choices":[{"index":0,"delta":{"content":"found","search_details":{"trace":"nested"}},"finish_reason":null}]}""",
                """{"choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"search_trace":{"hits":[{"score":9}]}}""",
                """{"choices":[],"usage":{"prompt_tokens":4,"completion_tokens":1,"total_tokens":5}}""", "[DONE]")
                : FixtureHost.Json("""{"id":"result","model":"jina-deepsearch-v1","choices":[{"index":0,"message":{"role":"assistant","content":"found","search_details":{"trace":"nested"}},"finish_reason":"stop"}],"search_trace":{"hits":[{"score":9}]}}""");
        }, moonshotJinaBlackForestLabs: true);
        using var response = await host.PostAsync(route, payload, JinaKey);
        var body = await GatewayTests.Success(response);
        Assert.Contains("found", body);
        Assert.Contains("search_trace", body);
        Assert.Contains("search_details", body);
        Assert.Contains("nested", body);
    }

    [Fact]
    public async Task Jina_embeddings_keep_native_inputs_encodings_and_raw_metadata_on_Vercel()
    {
        await using var host = await FixtureHost.StartAsync(request =>
        {
            Assert.Equal("api.jina.ai", request.Host);
            Assert.Equal("/v1/embeddings", request.Path);
            Assert.Equal("Bearer test-jina", request.Headers["Authorization"]);
            Assert.Equal("jina-embeddings-v4", (string?)request.Body!["model"]);
            Assert.False(request.Body.AsObject().ContainsKey("encoding_format"));
            if ((string?)request.Body["embedding_type"] == "base64")
            {
                Assert.Equal("test", (string?)request.Body["input"]![0]!["text"]);
                Assert.Equal(128, (int?)request.Body["dimensions"]);
                return FixtureHost.Json("""{"data":[{"index":0,"embedding":"AQID"}],"usage":{"total_tokens":4}}""");
            }
            Assert.Equal("float", (string?)request.Body["embedding_type"]);
            Assert.Equal("retrieval.query", (string?)request.Body["task"]);
            var response = FixtureHost.Json("""{"data":[{"index":1,"embedding":[0.9]},{"index":0,"embedding":[0.1]}],"usage":{"total_tokens":8},"billing":{"allocations":[12]}}""");
            response.Headers.Add("x-request-id", "jina-embed");
            response.Headers.Add("Set-Cookie", "secret=not-forwarded");
            return response;
        }, moonshotJinaBlackForestLabs: true);
        using var native = await host.PostAsync("/v1/embeddings", """
            {"model":"jina/jina-embeddings-v4","encoding_format":"base64","dimensions":128,"input":[{"text":"test"}]}
            """, JinaKey);
        var nativeBody = JsonNode.Parse(await GatewayTests.Success(native))!;
        Assert.Equal("AQID", (string?)nativeBody["data"]![0]!["embedding"]);
        Assert.Equal(4, (int?)nativeBody["usage"]!["prompt_tokens"]);
        using var vercel = await host.PostAsync("/api/embeddings", """
            {"model":"jina/jina-embeddings-v4","values":["a","b"],"providerOptions":{"jina":{"embedding_type":"base64","task":"retrieval.query"}}}
            """, JinaKey);
        var result = JsonNode.Parse(await GatewayTests.Success(vercel))!;
        Assert.Equal(0.1, (double?)result["embeddings"]![0]![0]);
        Assert.Equal(8, (int?)result["usage"]!["tokens"]);
        Assert.Equal(12, (int?)result["response"]!["body"]!["billing"]!["allocations"]![0]);
        var requestId = Assert.Single(result["response"]!["headers"]!.AsObject(), header => header.Key.Equals("x-request-id", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("jina-embed", (string?)requestId.Value);
        Assert.DoesNotContain("not-forwarded", result.ToJsonString());
    }

    [Fact]
    public async Task Jina_rerank_keeps_native_document_objects_and_options_without_overriding_contract_fields()
    {
        await using var host = await FixtureHost.StartAsync(request =>
        {
            Assert.Equal(("api.jina.ai", "/v1/rerank"), (request.Host, request.Path));
            Assert.Equal("original", (string?)request.Body!["query"]);
            Assert.Equal(1, (int?)request.Body["top_n"]);
            Assert.Equal("data", (string?)request.Body["documents"]![0]!["text"]);
            Assert.True((bool?)request.Body["return_documents"]);
            Assert.Equal("value", (string?)request.Body["future"]!["key"]);
            return FixtureHost.Json("""{"id":"rank","results":[{"index":0,"relevance_score":0.8,"document":{"text":"data"}}],"usage":{"total_tokens":9,"native":{"hits":1}}}""");
        }, moonshotJinaBlackForestLabs: true);
        using var response = await host.PostAsync("/api/rerank", """
            {"model":"jina/jina-reranker-v3","query":"original","topN":1,"documents":{"type":"object","values":[{"text":"data"}]},
            "providerOptions":{"jina":{"QUERY":"override","TOP_N":99,"return_documents":true,"future":{"key":"value"}}}}
            """, JinaKey);
        var result = JsonNode.Parse(await GatewayTests.Success(response))!;
        Assert.Equal(0.8, (double?)result["ranking"]![0]!["relevanceScore"]);
        Assert.Equal(1, (int?)result["providerMetadata"]!["jina"]!["usage"]!["native"]!["hits"]);
        Assert.Equal("data", (string?)result["response"]!["body"]!["results"]![0]!["document"]!["text"]);
    }

    [Fact]
    public async Task Flux_image_generation_translates_typed_fields_and_keeps_metadata_without_delivery_secrets()
    {
        await using var host = await FixtureHost.StartAsync(request => (request.Host, request.Path) switch
        {
            ("api.bfl.ai", "/v1/flux-3-image-klein") => SubmitImage(request),
            ("api.eu.bfl.ai", "/v1/get_result?id=image") when request.Headers["x-key"] == "test-bfl" =>
                FixtureHost.Json("""{"status":"Ready","result":{"sample":"https://delivery.bfl.ai/example.webp?sig=private","diagnostics":{"steps":[12]}}}"""),
            ("delivery.bfl.ai", "/example.webp?sig=private") => Asset(request, "image/webp"),
            _ => throw new InvalidOperationException($"Unexpected {request.Host}{request.Path}")
        }, moonshotJinaBlackForestLabs: true);
        using var response = await host.PostAsync("/api/images", """
            {"model":"blackforestlabs/flux-3-image-klein","prompt":"draw","aspectRatio":"3:2","n":2,"seed":5,
            "providerOptions":{"blackforestlabs":{"quality":"high","extra":{"tune":[2]},"prompt":"override"}}}
            """, BflKey);
        var json = await GatewayTests.Success(response);
        var result = JsonNode.Parse(json)!;
        Assert.Equal(2, result["images"]!.AsArray().Count);
        Assert.Equal("data:image/webp;base64,AQID", (string?)result["images"]![0]);
        Assert.Equal("quality", (string?)result["warnings"]![0]!["feature"]);
        Assert.Equal(0.24m, (decimal?)result["providerMetadata"]!["gateway"]!["cost"]);
        Assert.Equal(12, (int?)result["providerMetadata"]!["blackforestlabs"]!["results"]![0]!["result"]!["result"]!["diagnostics"]!["steps"]![0]);
        Assert.DoesNotContain("sig=private", json);
        Assert.Equal(6, host.Requests.Count);

        static HttpResponseMessage SubmitImage(FixtureHost.UpstreamRequest request)
        {
            Assert.Equal("test-bfl", request.Headers["x-key"]);
            Assert.Equal("draw", (string?)request.Body!["prompt"]);
            Assert.Equal("3:2", (string?)request.Body["aspect_ratio"]);
            Assert.Equal(5, (int?)request.Body["seed"]);
            Assert.Equal(2, (int?)request.Body["extra"]!["tune"]![0]);
            Assert.False(request.Body.AsObject().ContainsKey("width"));
            Assert.False(request.Body.AsObject().ContainsKey("quality"));
            return FixtureHost.Json("""{"id":"image","cost":12,"polling_url":"https://api.eu.bfl.ai/v1/get_result?id=image"}""");
        }
    }

    [Fact]
    public async Task Flux_erase_edit_uses_inline_image_and_mask_and_existing_completion_event()
    {
        await using var host = await FixtureHost.StartAsync(request =>
        {
            if (request.Method == "GET") return FixtureHost.Json("""{"status":"Ready","result":{"b64_json":"AQID"}}""");
            Assert.Equal("/v1/flux-tools/erase-v1", request.Path);
            Assert.Equal("AQID", (string?)request.Body!["image"]);
            Assert.Equal("BAUG", (string?)request.Body["mask"]);
            Assert.False(request.Body.AsObject().ContainsKey("prompt"));
            return FixtureHost.Json("""{"id":"image"}""");
        }, moonshotJinaBlackForestLabs: true);
        using var response = await host.PostAsync("/v1/images/edits", """
            {"model":"blackforestlabs/flux-tools/erase-v1","prompt":"erase","stream":true,
            "images":[{"image_url":"data:image/png;base64,AQID"}],"mask":{"image_url":"data:image/png;base64,BAUG"}}
            """, BflKey);
        var body = await GatewayTests.Success(response);
        Assert.Contains("image_edit.completed", body);
        Assert.Contains("AQID", body);
        Assert.DoesNotContain("partial_image", body);
    }

    [Fact]
    public async Task Flux_video_operation_tracks_pending_and_completed_and_preserves_native_draft_metadata()
    {
        var polls = 0;
        await using var host = await FixtureHost.StartAsync(request => (request.Host, request.Path) switch
        {
            ("api.bfl.ai", "/v1/flux-3-video") => SubmitVideo(request),
            ("api.eu.bfl.ai", "/v1/get_result?id=video") when request.Headers["x-key"] == "test-bfl" =>
                Interlocked.Increment(ref polls) == 1 ? FixtureHost.Json("""{"status":"Pending","queue":{"position":3}}""")
                    : FixtureHost.Json("""{"status":"Ready","cost":420,"result":{"sample":"https://delivery.bfl.ai/file.mp4","draft_cache":{"id":"cache"}}}"""),
            ("delivery.bfl.ai", "/file.mp4") => Asset(request, "video/mp4"),
            _ => throw new InvalidOperationException($"Unexpected {request.Host}{request.Path}")
        }, configure: services => services.AddStreaikReferences(), moonshotJinaBlackForestLabs: true);
        using var started = await host.PostAsync("/api/videos", """
            {"model":"blackforestlabs/flux-3-video-draft-enhance","resolution":"1080p","fps":30,
            "providerOptions":{"blackforestlabs":{"mode":"t2v","draft_cache":{"id":"original"}}}}
            """, BflKey);
        var start = JsonNode.Parse(await GatewayTests.Success(started))!;
        var operation = (string)start["operation"]!;
        Assert.StartsWith("blackforestlabs/", operation);
        Assert.DoesNotContain("get_result", operation);
        Assert.Equal("fps", (string?)start["warnings"]![0]!["feature"]);
        using var pending = await host.GetAsync($"/api/videos/{operation}", BflKey);
        var pendingBody = JsonNode.Parse(await GatewayTests.Success(pending))!;
        Assert.Equal("pending", (string?)pendingBody["status"]);
        Assert.Equal(3, (int?)pendingBody["providerMetadata"]!["blackforestlabs"]!["queue"]!["position"]);
        using var completed = await host.GetAsync($"/api/videos/{operation}", BflKey);
        var result = JsonNode.Parse(await GatewayTests.Success(completed))!;
        Assert.Equal("completed", (string?)result["status"]);
        Assert.Equal("AQID", (string?)result["videos"]![0]!["data"]);
        Assert.Equal(4.2m, (decimal?)result["providerMetadata"]!["gateway"]!["cost"]);
        Assert.Equal("cache", (string?)result["providerMetadata"]!["blackforestlabs"]!["result"]!["draft_cache"]!["id"]);

        static HttpResponseMessage SubmitVideo(FixtureHost.UpstreamRequest request)
        {
            Assert.Equal("draft_enhance", (string?)request.Body!["mode"]);
            Assert.Equal("original", (string?)request.Body["draft_cache"]!["id"]);
            Assert.Equal("1080p", (string?)request.Body["resolution"]);
            return FixtureHost.Json("""{"id":"video","polling_url":"https://api.eu.bfl.ai/v1/get_result?id=video"}""");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Flux_rejects_foreign_poll_and_asset_destinations(bool asset)
    {
        await using var host = await FixtureHost.StartAsync(request => (request.Host, request.Path) switch
        {
            ("api.bfl.ai", "/v1/flux-2-pro") => FixtureHost.Json(asset ? """{"id":"test"}"""
                : """{"id":"test","polling_url":"https://api.eu.bfl.ai.example.com/v1/get_result?id=test"}"""),
            ("api.bfl.ai", "/v1/get_result?id=test") when asset =>
                FixtureHost.Json("""{"status":"Ready","result":{"sample":"https://delivery.bfl.ai.example.com/image.png"}}"""),
            _ => throw new InvalidOperationException($"Unexpected {request.Host}{request.Path}")
        }, moonshotJinaBlackForestLabs: true);
        using var response = await host.PostAsync("/v1/images/generations", """{"model":"blackforestlabs/flux-2-pro","prompt":"draw"}""", BflKey);
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(asset ? 2 : 1, host.Requests.Count);
    }

    private static HttpResponseMessage Asset(FixtureHost.UpstreamRequest request, string mime)
    {
        Assert.False(request.Headers.ContainsKey("x-key"));
        Assert.False(request.Headers.ContainsKey("Authorization"));
        var result = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
        result.Content.Headers.ContentType = new MediaTypeHeaderValue(mime);
        return result;
    }
}
