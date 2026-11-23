using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace Streaik.Gateway.Tests;

public sealed class ChutesPineconeRecraftTests
{
    private static readonly (string, string) ChutesKey = ("X-Chutes-Key", "fixture-chutes");
    private static readonly (string, string) PineconeKey = ("X-Pinecone-Key", "fixture-pinecone");
    private static readonly (string, string) RecraftKey = ("X-Recraft-Key", "fixture-recraft");

    [Fact]
    public async Task Listings_use_native_ids_and_discover_only_supported_model_types()
    {
        await using var host = await FixtureHost.StartAsync(request => (request.Host, request.Path) switch
        {
            ("llm.chutes.ai", "/v1/models") => Chutes(request,
                """{"data":[{"id":"publisher/custom","owned_by":"publisher","context_length":1024}]}"""),
            ("api.pinecone.io", "/models") => Pinecone(request,
                """{"models":[{"model":"example-embed","type":"embed","vector_type":"dense","max_sequence_length":2048},{"model":"example-rerank","type":"rerank","short_description":"Ranking model"},{"model":"pinecone-sparse-english-v0","type":"embed","vector_type":"sparse"}]}"""),
            _ => throw new InvalidOperationException("Unexpected upstream model request.")
        }, chutesPineconeRecraft: true);
        using var response = await host.GetAsync("/v1/models", ChutesKey, PineconeKey, RecraftKey);
        var models = (await Body(response))["data"]!.AsArray();
        var chutes = Assert.Single(models, m => Id(m) == "chutes/publisher/custom")!;
        Assert.Equal("language", (string?)chutes["type"]);
        Assert.Equal(1024, (int?)chutes["context_window"]);
        Assert.Contains(models, m => Id(m) == "pinecone/example-embed" && (string?)m?["type"] == "embedding");
        Assert.Contains(models, m => Id(m) == "pinecone/example-rerank" && (string?)m?["type"] == "reranking");
        Assert.DoesNotContain(models, m => Id(m) == "pinecone/pinecone-sparse-english-v0");
        Assert.Equal(13, models.Count(m => Id(m)?.StartsWith("recraft/", StringComparison.Ordinal) == true));
        Assert.Contains(models, m => Id(m) == "recraft/generateBackground" && (string?)m?["type"] == "image");
        Assert.Equal(2, host.Requests.Count);

        static string? Id(JsonNode? model) => (string?)model?["id"];
    }

    [Fact]
    public async Task Chutes_native_chat_round_trips_extension_fields_and_tools()
    {
        await using var host = await FixtureHost.StartAsync(request =>
        {
            Assert.Equal("/v1/chat/completions", request.Path);
            Assert.Equal("publisher/custom", (string?)request.Body!["model"]);
            Assert.Equal(3, (int?)request.Body["native_options"]!["budget"]);
            Assert.Equal("lookup", (string?)request.Body["tools"]![0]!["function"]!["name"]);
            return Chutes(request, """
                {"id":"fixture","model":"publisher/custom","choices":[{"index":0,"message":{"role":"assistant","content":"found","message_details":{"trace":[1]}},"finish_reason":"stop"}],"usage":{"prompt_tokens":3,"completion_tokens":2,"total_tokens":5,"metering":{"tier":"test"}},"provider_trace":{"steps":[{"id":7}]}}
                """);
        }, chutesPineconeRecraft: true);
        using var response = await host.PostAsync("/v1/chat/completions", """
            {"model":"chutes/publisher/custom","messages":[{"role":"user","content":"find"}],"native_options":{"budget":3},"tools":[{"type":"function","function":{"name":"lookup","parameters":{"type":"object"}}}]}
            """, ChutesKey);
        var body = await Body(response);
        Assert.Equal(7, (int?)body["provider_trace"]!["steps"]![0]!["id"]);
        Assert.Equal(1, (int?)body["choices"]![0]!["message"]!["message_details"]!["trace"]![0]);
        Assert.Equal("test", (string?)body["usage"]!["metering"]!["tier"]);
    }

    [Theory]
    [InlineData("/v1/chat/completions", "{\"model\":\"chutes/publisher/custom\",\"messages\":[{\"role\":\"user\",\"content\":\"find\"}],\"stream\":true}")]
    [InlineData("/v1/responses", "{\"model\":\"chutes/publisher/custom\",\"input\":\"find\",\"stream\":true}")]
    [InlineData("/v1/messages", "{\"model\":\"chutes/publisher/custom\",\"messages\":[{\"role\":\"user\",\"content\":\"find\"}],\"max_tokens\":32,\"stream\":true}")]
    [InlineData("/api/chat", "{\"model\":\"chutes/publisher/custom\",\"messages\":[{\"id\":\"m\",\"role\":\"user\",\"parts\":[{\"type\":\"text\",\"text\":\"find\"}]}],\"providerMetadata\":{\"chutes\":{\"native_options\":{\"budget\":3},\"model\":\"override\"}}}")]
    public async Task Chutes_streams_through_existing_routes_and_preserves_diagnostics(string route, string payload)
    {
        await using var host = await FixtureHost.StartAsync(request =>
        {
            Assert.Equal(("llm.chutes.ai", "/v1/chat/completions"), (request.Host, request.Path));
            Assert.Equal("Bearer fixture-chutes", request.Headers["Authorization"]);
            Assert.Equal("publisher/custom", (string?)request.Body!["model"]);
            Assert.True(request.Stream);
            if (route == "/api/chat") Assert.Equal(3, (int?)request.Body["native_options"]!["budget"]);
            return FixtureHost.Sse(
                """{"id":"c","model":"publisher/custom","choices":[{"index":0,"delta":{"content":"found","message_details":{"trace":"nested"}},"finish_reason":null}]}""",
                """{"choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"provider_trace":{"steps":[{"id":7}]}}""",
                """{"choices":[],"usage":{"prompt_tokens":3,"completion_tokens":1,"total_tokens":4}}""", "[DONE]");
        }, chutesPineconeRecraft: true);
        using var response = await host.PostAsync(route, payload, ChutesKey);
        var result = await GatewayTests.Success(response);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("found", result);
        Assert.Contains("provider_trace", result);
        Assert.Contains("message_details", result);
        Assert.Contains("nested", result);
    }

    [Fact]
    public async Task Pinecone_dense_embeddings_keep_order_parameters_and_upstream_metadata()
    {
        await using var host = await FixtureHost.StartAsync(request =>
        {
            Assert.Equal("/embed", request.Path);
            Assert.Equal("embed-model", (string?)request.Body!["model"]);
            var values = request.Body["inputs"]!.AsArray();
            if (values.Count == 1)
            {
                Assert.Equal("one", (string?)values[0]!["text"]);
                return Pinecone(request, """{"model":"embed-model","vector_type":"dense","data":[{"values":[0.1]}],"usage":{"total_tokens":3}}""");
            }
            Assert.Equal("one", (string?)values[0]!["text"]);
            Assert.Equal("two", (string?)values[1]!["text"]);
            Assert.Equal("query", (string?)request.Body["parameters"]!["input_type"]);
            Assert.Equal("END", (string?)request.Body["parameters"]!["truncate"]);
            Assert.Equal(7, (int?)request.Body["parameters"]!["future"]!["hint"]![0]);
            return Pinecone(request, """
                {"model":"embed-model","vector_type":"dense","data":[{"values":[0.1]},{"values":[0.2]}],"usage":{"total_tokens":6,"billing":{"units":[3]}}}
                """);
        }, chutesPineconeRecraft: true);
        using var native = await host.PostAsync("/v1/embeddings", """{"model":"pinecone/embed-model","input":"one"}""", PineconeKey);
        var nativeBody = await Body(native);
        Assert.Equal("pinecone/embed-model", (string?)nativeBody["model"]);
        Assert.Equal(0, (int?)nativeBody["data"]![0]!["index"]);
        Assert.Equal(3, (int?)nativeBody["usage"]!["prompt_tokens"]);
        using var vercel = await host.PostAsync("/api/embeddings", """
            {"model":"pinecone/embed-model","values":["one","two"],"providerOptions":{"pinecone":{"inputType":"query","truncate":"END","parameters":{"input_type":"passage","future":{"hint":[7]}}}}}
            """, PineconeKey);
        var result = await Body(vercel);
        Assert.Equal(0.1, (double?)result["embeddings"]![0]![0]);
        Assert.Equal(0.2, (double?)result["embeddings"]![1]![0]);
        Assert.Equal(6, (int?)result["usage"]!["tokens"]);
        Assert.Equal(3, (int?)result["response"]!["body"]!["usage"]!["billing"]!["units"]![0]);
        AssertSafeHeaders(result);
    }

    [Fact]
    public async Task Pinecone_rerank_preserves_rank_order_document_objects_and_diagnostics()
    {
        await using var host = await FixtureHost.StartAsync(request =>
        {
            Assert.Equal("/rerank", request.Path);
            Assert.Equal("rank-model", (string?)request.Body!["model"]);
            Assert.Equal("find", (string?)request.Body["query"]);
            Assert.Equal(2, (int?)request.Body["top_n"]);
            Assert.True((bool?)request.Body["return_documents"]);
            Assert.Equal("title", (string?)request.Body["rank_fields"]![0]);
            Assert.Equal("Second", (string?)request.Body["documents"]![1]!["title"]);
            Assert.Equal("END", (string?)request.Body["parameters"]!["truncate"]);
            Assert.Equal(7, (int?)request.Body["future"]!["hint"]![0]);
            return Pinecone(request, """
                {"id":"r","data":[{"index":1,"score":0.95,"document":{"title":"Second"}},{"index":0,"score":0.16}],"usage":{"rerank_units":1,"billing":{"tier":"standard"}}}
                """);
        }, chutesPineconeRecraft: true);
        using var response = await host.PostAsync("/api/rerank", """
            {"model":"pinecone/rank-model","query":"find","documents":{"type":"object","values":[{"title":"First"},{"title":"Second"}]},"topN":2,
             "providerOptions":{"pinecone":{"MODEL":"override","TOP_N":99,"rank_fields":["title"],"return_documents":true,"parameters":{"truncate":"END"},"future":{"hint":[7]}}}}
            """, PineconeKey);
        var result = await Body(response);
        Assert.Equal(1, (int?)result["ranking"]![0]!["index"]);
        Assert.Equal(0.95, (double?)result["ranking"]![0]!["relevanceScore"]);
        Assert.Equal("Second", (string?)result["response"]!["body"]!["data"]![0]!["document"]!["title"]);
        Assert.Equal("standard", (string?)result["providerMetadata"]!["pinecone"]!["usage"]!["billing"]!["tier"]);
        AssertSafeHeaders(result);
    }

    [Theory]
    [InlineData("{\"model\":\"pinecone/m\",\"input\":[1,2]}")]
    [InlineData("{\"model\":\"pinecone/m\",\"input\":\"a\",\"encoding_format\":\"base64\"}")]
    [InlineData("{\"model\":\"pinecone/m\",\"input\":\"a\",\"dimensions\":3}")]
    [InlineData("{\"model\":\"pinecone/pinecone-sparse-english-v0\",\"input\":\"a\"}")]
    [InlineData("{\"model\":\"pinecone/m\",\"input\":\"a\",\"parameters\":[]}")]
    public async Task Pinecone_rejects_embedding_inputs_incompatible_with_dense_contract(string payload)
    {
        await using var host = await FixtureHost.StartAsync(chutesPineconeRecraft: true);
        using var response = await host.PostAsync("/v1/embeddings", payload, PineconeKey);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(host.Requests);
    }

    [Fact]
    public async Task Recraft_generates_batches_and_keeps_nested_metadata_without_asset_urls_or_credentials()
    {
        await using var host = await FixtureHost.StartAsync(request =>
        {
            if (request.Host == "img.recraft.ai")
            {
                Assert.Equal("/render.png?sig=fixture-private", request.Path);
                Assert.False(request.Headers.ContainsKey("Authorization"));
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) { Headers = { ContentType = new("image/png") } } };
            }
            CheckRecraft(request, "generations");
            Assert.Equal("recraftv4_1", (string?)request.Body!["model"]);
            Assert.Equal("draw", (string?)request.Body["prompt"]);
            Assert.Equal("1024x1024", (string?)request.Body["size"]);
            Assert.Equal(2, (int?)request.Body["n"]);
            Assert.Equal(1, (int?)request.Body["controls"]!["colors"]![0]!["r"]);
            return FixtureHost.Json("""
                {"created":123,"data":[{"url":"https://img.recraft.ai/render.png?sig=fixture-private","diagnostics":{"steps":[9]}},{"b64_json":"BAUG"}],"billing":{"costs":[1.5]},"debug":{"api_key":"fixture-exposed","delivery":"https://img.recraft.ai/render.png?sig=fixture-private"}}
                """);
        }, chutesPineconeRecraft: true);
        using var response = await host.PostAsync("/api/images", """
            {"model":"recraft/recraftv4_1","prompt":"draw","size":"1024x1024","n":2,
             "providerOptions":{"recraft":{"model":"override","prompt":"override","controls":{"colors":[{"r":1}]}}}}
            """, RecraftKey);
        var result = await Body(response);
        Assert.Equal("data:image/png;base64,AQID", (string?)result["images"]![0]);
        Assert.Equal("data:image/png;base64,BAUG", (string?)result["images"]![1]);
        var metadata = result["providerMetadata"]!["recraft"]!;
        Assert.Equal(9, (int?)metadata["data"]![0]!["diagnostics"]!["steps"]![0]);
        Assert.Equal(1.5, (double?)metadata["billing"]!["costs"]![0]);
        Assert.DoesNotContain("fixture-private", result.ToJsonString());
        Assert.DoesNotContain("fixture-exposed", result.ToJsonString());
    }

    [Theory]
    [InlineData("vectorize", "file", false, false, false, "svg")]
    [InlineData("generateBackground", "image", true, true, false, "png")]
    [InlineData("removeBackground", "file", false, false, false, "png")]
    [InlineData("crispUpscale", "file", false, false, false, "png")]
    [InlineData("creativeUpscale", "file", false, false, false, "png")]
    [InlineData("eraseRegion", "image", true, false, false, "png")]
    [InlineData("variateImage", "image", false, false, true, "png")]
    public async Task Recraft_routes_each_image_operation_with_native_multipart_fields(string operation, string fileField, bool masked, bool prompt, bool size, string format)
    {
        await using var host = await FixtureHost.StartAsync(request =>
        {
            CheckRecraft(request, operation);
            var form = request.RawBody!;
            Assert.Contains($"name={fileField};", form);
            Assert.Equal(masked, form.Contains("name=mask;", StringComparison.Ordinal));
            Assert.Equal(prompt, form.Contains("name=prompt", StringComparison.Ordinal));
            Assert.Equal(size, form.Contains("name=size", StringComparison.Ordinal));
            Assert.DoesNotContain("name=model", form);
            return FixtureHost.Json("""{"image":{"b64_json":"AQID"},"id":"operation"}""");
        }, chutesPineconeRecraft: true);
        var body = new JsonObject { ["model"] = $"recraft/{operation}", ["prompt"] = "adjust", ["size"] = "1024x1024",
            ["images"] = new JsonArray(InlineImage()) };
        if (masked) body["mask"] = InlineImage();
        using var response = await host.PostAsync("/v1/images/edits", body.ToJsonString(), RecraftKey);
        var result = await Body(response);
        Assert.Equal("AQID", (string?)result["data"]![0]!["b64_json"]);
        Assert.Equal(format, (string?)result["output_format"]);
        Assert.Equal("operation", (string?)result["providerMetadata"]!["recraft"]!["id"]);
    }

    [Theory]
    [InlineData(false, "imageToImage")]
    [InlineData(true, "inpaint")]
    public async Task Recraft_standard_edits_can_emit_a_single_OpenAI_image_completion(bool masked, string endpoint)
    {
        await using var host = await FixtureHost.StartAsync(request =>
        {
            CheckRecraft(request, endpoint);
            var form = request.RawBody!;
            Assert.Contains("name=image;", form);
            Assert.Contains("recraftv3", form);
            Assert.Equal(masked, form.Contains("name=mask;", StringComparison.Ordinal));
            Assert.Equal(!masked, form.Contains("name=strength", StringComparison.Ordinal));
            Assert.DoesNotContain("name=stream", form);
            return FixtureHost.Json("""{"created":123,"data":[{"b64_json":"AQID"}]}""");
        }, chutesPineconeRecraft: true);
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent("recraft/recraftv3"), "model");
        content.Add(new StringContent("edit"), "prompt");
        content.Add(new StringContent("true"), "stream");
        content.Add(new ByteArrayContent([1, 2, 3]) { Headers = { ContentType = new MediaTypeHeaderValue("image/png") } }, "image", "input.png");
        if (masked) content.Add(new ByteArrayContent([4, 5, 6]), "mask", "mask.png");
        else content.Add(new StringContent("0.4"), "strength");
        using var response = await host.PostFormAsync("/v1/images/edits", content, RecraftKey);
        var result = await GatewayTests.Success(response);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("event: image_edit.completed", result);
        Assert.Contains("AQID", result);
    }

    [Fact]
    public async Task Recraft_can_generate_via_Vercel_chat_without_copying_the_asset_into_metadata()
    {
        await using var host = await FixtureHost.StartAsync(request =>
        {
            CheckRecraft(request, "generations");
            Assert.Equal("draw", (string?)request.Body!["prompt"]);
            Assert.Equal("digital_illustration", (string?)request.Body["style"]);
            return FixtureHost.Json("""{"data":["AQID"],"trace":{"render":[5],"apiKey":"not-forwarded-key"}}""");
        }, chutesPineconeRecraft: true);
        using var response = await host.PostAsync("/api/chat", """
            {"model":"recraft/recraftv3","messages":[{"id":"m","role":"user","parts":[{"type":"text","text":"draw"}]}],
             "providerMetadata":{"recraft":{"style":"digital_illustration"}}}
            """, RecraftKey);
        var result = await GatewayTests.Success(response);
        Assert.Contains("data:image/png;base64,AQID", result);
        Assert.Contains("\"trace\":{\"render\":[5]}", result);
        Assert.DoesNotContain("not-forwarded-key", result);
        Assert.DoesNotContain("\"data\":[\"AQID\"]", result);
    }

    [Fact]
    public async Task Unsupported_capabilities_and_invalid_Recraft_requests_do_not_call_providers()
    {
        await using var host = await FixtureHost.StartAsync(chutesPineconeRecraft: true);
        using var embedding = await host.PostAsync("/v1/embeddings", """{"model":"chutes/m","input":"a"}""", ChutesKey);
        using var chat = await host.PostAsync("/v1/chat/completions", """{"model":"pinecone/m","messages":[{"role":"user","content":"hi"}]}""", PineconeKey);
        using var batch = await host.PostAsync("/v1/images/generations", """{"model":"recraft/recraftv4","prompt":"draw","stream":true,"n":2}""", RecraftKey);
        using var missingMask = await host.PostAsync("/v1/images/edits", """{"model":"recraft/eraseRegion","prompt":"erase","images":[{"image_url":"data:image/png;base64,AQID"}]}""", RecraftKey);
        using var invalidStrength = await host.PostAsync("/v1/images/edits", """{"model":"recraft/recraftv3","prompt":"change","strength":1.1,"images":[{"image_url":"data:image/png;base64,AQID"}]}""", RecraftKey);
        using var untrustedUrl = await host.PostAsync("/v1/images/edits", """{"model":"recraft/vectorize","prompt":"vectorize","images":[{"image_url":"https://untrusted.invalid/a.png"}]}""", RecraftKey);
        Assert.Equal(HttpStatusCode.BadRequest, embedding.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, chat.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, batch.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, missingMask.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalidStrength.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, untrustedUrl.StatusCode);
        Assert.Empty(host.Requests);
    }

    private static JsonObject InlineImage() => new() { ["image_url"] = "data:image/png;base64,AQID" };
    private static async Task<JsonNode> Body(HttpResponseMessage response) => JsonNode.Parse(await GatewayTests.Success(response))!;

    private static HttpResponseMessage Chutes(FixtureHost.UpstreamRequest request, string json)
    {
        Assert.Equal("llm.chutes.ai", request.Host);
        Assert.Equal("Bearer fixture-chutes", request.Headers["Authorization"]);
        return FixtureHost.Json(json);
    }

    private static HttpResponseMessage Pinecone(FixtureHost.UpstreamRequest request, string json)
    {
        Assert.Equal("api.pinecone.io", request.Host);
        Assert.Equal("fixture-pinecone", request.Headers["Api-Key"]);
        Assert.Equal("2026-04", request.Headers["X-Pinecone-Api-Version"]);
        Assert.False(request.Headers.ContainsKey("Authorization"));
        var response = FixtureHost.Json(json);
        response.Headers.Add("X-Pinecone-Request-Id", "fixture-request");
        response.Headers.Add("Set-Cookie", "not-forwarded-cookie");
        response.Headers.Add("Api-Key", "not-forwarded-key");
        return response;
    }

    private static void AssertSafeHeaders(JsonNode body)
    {
        var headers = body["response"]!["headers"]!.AsObject();
        Assert.Equal("fixture-request", (string?)Assert.Single(headers, h => h.Key.Equals("X-Pinecone-Request-Id", StringComparison.OrdinalIgnoreCase)).Value);
        Assert.DoesNotContain("not-forwarded", body.ToJsonString());
    }

    private static void CheckRecraft(FixtureHost.UpstreamRequest request, string endpoint)
    {
        Assert.Equal(("external.api.recraft.ai", $"/v1/images/{endpoint}"), (request.Host, request.Path));
        Assert.Equal("Bearer fixture-recraft", request.Headers["Authorization"]);
    }
}
