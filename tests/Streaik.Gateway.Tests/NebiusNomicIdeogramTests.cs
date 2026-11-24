using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace Streaik.Gateway.Tests;

public sealed class NebiusNomicIdeogramTests
{
    private static readonly (string, string) Nebius = ("x-nebius-key", "nebius-fixture");
    private static readonly (string, string) Nomic = ("x-nomic-key", "nomic-fixture");
    private static readonly (string, string) Ideogram = ("x-ideogram-key", "ideogram-fixture");
    private const string ImageUrl = "data:image/png;base64,AQID";

    [Fact]
    public async Task Models_are_key_scoped_and_keep_native_namespaces()
    {
        await using var host = await FixtureHost.StartAsync(request =>
        {
            CheckNebius(request, "/v1/models");
            return FixtureHost.Json("""{"data":[{"id":"vendor/Model"},{"id":"BAAI/bge-en-icl"}]}""");
        }, nebiusNomicIdeogram: true);
        using var unkeyed = await host.GetAsync("/v1/models");
        var before = await GatewayTests.Success(unkeyed);
        Assert.DoesNotContain("nebius/", before);
        Assert.DoesNotContain("nomic/", before);
        Assert.DoesNotContain("ideogram/", before);
        Assert.Empty(host.Requests);

        using var response = await host.GetAsync("/v1/models", Nebius, Nomic, Ideogram);
        var models = (await Json(response))["data"]!.AsArray().ToDictionary(model => (string)model!["id"]!, model => (string)model!["type"]!);
        Assert.Equal("language", models["nebius/vendor/Model"]);
        Assert.Equal("embedding", models["nebius/BAAI/bge-en-icl"]);
        Assert.Equal(2, models.Count(model => model.Key.StartsWith("nomic/", StringComparison.Ordinal) && model.Value == "embedding"));
        Assert.Equal(6, models.Count(model => model.Key.StartsWith("ideogram/", StringComparison.Ordinal) && model.Value == "image"));
        Assert.Single(host.Requests);
    }

    [Theory]
    [InlineData("/v1/chat/completions", false)]
    [InlineData("/v1/chat/completions", true)]
    [InlineData("/v1/responses", false)]
    [InlineData("/v1/responses", true)]
    [InlineData("/v1/messages", false)]
    [InlineData("/v1/messages", true)]
    [InlineData("/api/chat", true)]
    public async Task Nebius_uses_native_Responses_but_bridges_Messages_and_Vercel_chat(string route, bool stream)
    {
        var path = route == "/v1/responses" ? route : "/v1/chat/completions";
        await using var host = await FixtureHost.StartAsync(request =>
        {
            CheckNebius(request, path);
            Assert.Equal("vendor/Model", (string?)request.Body!["model"]);
            Assert.Equal(stream, request.Stream);
            if (route is "/v1/chat/completions" or "/v1/responses" or "/api/chat")
                Assert.True((bool)request.Body["native_flag"]!);
            return FixtureHost.Reply(request);
        }, nebiusNomicIdeogram: true);
        var payload = JsonNode.Parse(route switch
        {
            "/v1/responses" => """{"input":"Hi","native_flag":true}""",
            "/v1/messages" => """{"max_tokens":20,"messages":[{"role":"user","content":"Hi"}]}""",
            "/api/chat" => """{"messages":[{"id":"u","role":"user","parts":[{"type":"text","text":"Hi"}]}],"providerMetadata":{"nebius":{"native_flag":true}}}""",
            _ => """{"messages":[{"role":"user","content":"Hi"}],"native_flag":true}"""
        })!.AsObject();
        payload["model"] = "nebius/vendor/Model";
        if (route != "/api/chat") payload["stream"] = stream;
        using var response = await host.PostAsync(route, payload.ToJsonString(), Nebius);
        var wire = await GatewayTests.Success(response);
        Assert.Contains("hello", wire);
        Assert.Contains("future_response", wire);
        if (stream) Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Single(host.Requests);
    }

    [Fact]
    public async Task Nebius_embeddings_forward_options_and_return_raw_Vercel_metadata()
    {
        await using var host = await FixtureHost.StartAsync(request =>
        {
            CheckNebius(request, "/v1/embeddings");
            Assert.Equal("vendor/embed", (string?)request.Body!["model"]);
            Assert.Equal(2, (int?)request.Body["dimensions"]);
            Assert.Equal("query", (string?)request.Body["purpose"]);
            return MetadataResponse("""{"model":"upstream","usage":{"total_tokens":7},"data":[{"index":0,"embedding":[1.5,2]}],"future":{"revision":3}}""");
        }, nebiusNomicIdeogram: true);
        using var native = await host.PostAsync("/v1/embeddings", """{"model":"nebius/vendor/embed","input":"test","dimensions":2,"purpose":"query"}""", Nebius);
        Assert.Equal("nebius/vendor/embed", (string?)(await Json(native))["model"]);
        using var vercel = await host.PostAsync("/api/embeddings", """
            {"model":"nebius/vendor/embed","values":["test"],"dimensions":2,"providerOptions":{"nebius":{"purpose":"query"}}}
            """, Nebius);
        var json = await Json(vercel);
        Assert.Equal(1.5, (double?)json["embeddings"]![0]![0]);
        Assert.Equal(7, (int?)json["usage"]!["tokens"]);
        CheckMetadata(json);
    }

    [Fact]
    public async Task Nomic_maps_text_and_dimensions_preserves_raw_metadata_and_encodes_little_endian()
    {
        await using var host = await FixtureHost.StartAsync(request =>
        {
            Assert.Equal("api-atlas.nomic.ai", request.Host);
            Assert.Equal("/v1/embedding/text", request.Path);
            Assert.Equal("Bearer nomic-fixture", request.Headers["Authorization"]);
            Assert.Equal("nomic-embed-text-v1.5", (string?)request.Body!["model"]);
            Assert.Equal("search_document", (string?)request.Body["task_type"]);
            Assert.Equal(2, (int?)request.Body["dimensionality"]);
            Assert.Null(request.Body["encoding_format"]);
            Assert.Null(request.Body["input"]);
            Assert.Equal("a", (string?)request.Body["texts"]![0]);
            return MetadataResponse(request.Body["texts"]!.AsArray().Count == 1
                ? """{"embeddings":[[1.0,-2.5]],"usage":{"total_tokens":3},"future":{"revision":3}}"""
                : """{"embeddings":[[1.0,-2.5],[3.0,4.0]],"usage":{"total_tokens":6},"future":{"revision":3}}""");
        }, nebiusNomicIdeogram: true);
        using var native = await host.PostAsync("/v1/embeddings", """
            {"model":"nomic/nomic-embed-text-v1.5","input":"a","dimensions":2,"encoding_format":"base64","task_type":"search_document"}
            """, Nomic);
        var result = await Json(native);
        Assert.Equal("nomic/nomic-embed-text-v1.5", (string?)result["model"]);
        Assert.Equal(new byte[] { 0, 0, 128, 63, 0, 0, 32, 192 }, Convert.FromBase64String((string)result["data"]![0]!["embedding"]!));

        using var vercel = await host.PostAsync("/api/embeddings", """
            {"model":"nomic/nomic-embed-text-v1.5","values":["a","b"],"dimensions":2,"providerOptions":{"nomic":{"task_type":"search_document","texts":["ignored"]}}}
            """, Nomic);
        var sdk = await Json(vercel);
        Assert.Equal(3, (double?)sdk["embeddings"]![1]![0]);
        Assert.Equal(6, (int?)sdk["usage"]!["tokens"]);
        CheckMetadata(sdk);

        using var badInput = await host.PostAsync("/v1/embeddings", """{"model":"nomic/nomic-embed-text-v1","input":[123]}""", Nomic);
        Assert.Equal(HttpStatusCode.BadRequest, badInput.StatusCode);
        Assert.Equal(2, host.Requests.Count);
    }

    [Theory]
    [InlineData("ideogram-v3", "/v1/ideogram-v3/generate", null, "prompt")]
    [InlineData("ideogram-v4", "/v1/ideogram-v4/generate", null, "json_prompt")]
    [InlineData("ideogram-v3/generate-transparent", "/v1/ideogram-v3/generate-transparent", null, "aspect_ratio")]
    [InlineData("ideogram-v3", "/v1/ideogram-v3/remix", "image", "image_weight")]
    [InlineData("ideogram-v4", "/v1/ideogram-v4/remix", "image", "text_prompt")]
    [InlineData("ideogram-v3", "/v1/edit", "images", "transparent_background")]
    [InlineData("ideogram-v3/reframe", "/v1/ideogram-v3/reframe", "image", "resolution")]
    [InlineData("ideogram-v3/replace-background", "/v1/ideogram-v3/replace-background", "image", "prompt")]
    [InlineData("upscale", "/upscale", "image_file", "image_request")]
    public async Task Ideogram_routes_workflows_with_multipart_options_and_safe_raw_metadata(string model, string path, string? fileField, string option)
    {
        await using var host = await FixtureHost.StartAsync(request =>
        {
            if (request.Method == "GET") return IdeogramAsset(request);
            Assert.Equal("api.ideogram.ai", request.Host);
            Assert.Equal(path, request.Path);
            Assert.Equal("ideogram-fixture", request.Headers["Api-Key"]);
            var raw = request.RawBody!;
            Assert.Contains("name=" + option, raw);
            if (fileField is not null) Assert.Contains("name=" + fileField + "; filename=", raw);
            Assert.DoesNotContain("name=mask;", raw);
            Assert.DoesNotContain("name=api_key", raw);
            Assert.Contains("name=future_setting", raw);
            return IdeogramResult();
        }, nebiusNomicIdeogram: true);
        var options = JsonNode.Parse("""{"future_setting":{"mode":true},"api_key":"not-the-key"}""")!.AsObject();
        if (option == "json_prompt") options[option] = new JsonObject { ["subject"] = "tree" };
        if (option == "image_weight") options[option] = 50;
        if (option == "transparent_background") options[option] = true;
        if (option == "image_request") options["upscale"] = new JsonObject { ["detail"] = 25 };
        var data = new JsonObject { ["model"] = "ideogram/" + model, ["providerOptions"] = new JsonObject { ["ideogram"] = options } };
        if (option != "json_prompt" && model is not ("upscale" or "ideogram-v3/reframe")) data["prompt"] = "tree";
        if (option == "resolution") data["size"] = "1024x1024";
        if (option == "aspect_ratio") data["aspectRatio"] = "3:2";
        if (fileField is not null) data["files"] = new JsonArray(new JsonObject { ["type"] = "url", ["url"] = ImageUrl, ["mediaType"] = "image/png" });
        if (path == "/v1/edit") data["mask"] = new JsonObject { ["type"] = "url", ["url"] = ImageUrl, ["mediaType"] = "image/png" };
        using var response = await host.PostAsync("/api/images", data.ToJsonString(), Ideogram);
        var json = await Json(response);
        Assert.Equal("data:image/webp;base64,AQID", (string?)json["images"]![0]);
        Assert.Equal(42, (int?)json["providerMetadata"]!["ideogram"]!["data"]![0]!["seed"]);
        Assert.True((bool)json["providerMetadata"]!["ideogram"]!["future"]!["enabled"]!);
        Assert.DoesNotContain("download-token", json.ToJsonString());
        Assert.DoesNotContain("hidden-token", json.ToJsonString());
        if (path == "/v1/edit") Assert.Equal("mask", (string?)json["warnings"]![0]!["feature"]);
        Assert.Equal(2, host.Requests.Count);
    }

    [Fact]
    public async Task Ideogram_native_stream_and_Vercel_chat_reuse_the_image_adapters()
    {
        await using var host = await FixtureHost.StartAsync(request => request.Method == "GET" ? IdeogramAsset(request) : IdeogramResult(),
            nebiusNomicIdeogram: true);
        using var native = await host.PostAsync("/v1/images/generations", """
            {"model":"ideogram/ideogram-v4","stream":true,"json_prompt":{"subject":"tree"}}
            """, Ideogram);
        var sse = await GatewayTests.Success(native);
        Assert.Contains("image_generation.completed", sse);
        Assert.Contains("AQID", sse);
        using var chat = await host.PostAsync("/api/chat", """
            {"model":"ideogram/ideogram-v3","messages":[{"id":"u","role":"user","parts":[{"type":"text","text":"tree"}]}]}
            """, Ideogram);
        var vercel = await GatewayTests.Success(chat);
        Assert.Contains("data:image/webp;base64,AQID", vercel);
        Assert.Contains("\"seed\":42", vercel);
    }

    [Fact]
    public async Task Ideogram_rejects_untrusted_delivery_and_invalid_workflows_without_extra_requests()
    {
        await using var host = await FixtureHost.StartAsync(request =>
        {
            Assert.Equal("POST", request.Method);
            return FixtureHost.Json("""{"data":[{"url":"https://ideogram.ai.invalid/asset"}]}""");
        }, nebiusNomicIdeogram: true);
        using var missingResolution = await host.PostAsync("/api/images", """
            {"model":"ideogram/ideogram-v3/reframe","files":[{"type":"url","url":"data:image/png;base64,AQID","mediaType":"image/png"}]}
            """, Ideogram);
        Assert.Equal(HttpStatusCode.BadRequest, missingResolution.StatusCode);
        Assert.Empty(host.Requests);
        using var untrusted = await host.PostAsync("/v1/images/generations", """{"model":"ideogram/ideogram-v3","prompt":"tree"}""", Ideogram);
        Assert.Equal(HttpStatusCode.BadGateway, untrusted.StatusCode);
        Assert.Single(host.Requests);
    }

    private static HttpResponseMessage IdeogramResult() => FixtureHost.Json("""
        {"data":[{"url":"https://ideogram.ai/assets/image.webp?sig=download-token","seed":42,"prompt":"tree"}],"token":"hidden-token","future":{"enabled":true}}
        """);

    private static HttpResponseMessage IdeogramAsset(FixtureHost.UpstreamRequest request)
    {
        Assert.Equal("ideogram.ai", request.Host);
        Assert.Equal("/assets/image.webp?sig=download-token", request.Path);
        Assert.False(request.Headers.ContainsKey("Api-Key"));
        Assert.False(request.Headers.ContainsKey("Authorization"));
        return new(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) { Headers = { ContentType = new MediaTypeHeaderValue("image/webp") } } };
    }

    private static void CheckNebius(FixtureHost.UpstreamRequest request, string path)
    {
        Assert.Equal("api.tokenfactory.nebius.com", request.Host);
        Assert.Equal(path, request.Path);
        Assert.Equal("Bearer nebius-fixture", request.Headers["Authorization"]);
    }

    private static HttpResponseMessage MetadataResponse(string body)
    {
        var result = FixtureHost.Json(body);
        result.Headers.Add("x-request-id", "fixture-id");
        result.Headers.TryAddWithoutValidation("Set-Cookie", "server-only-cookie");
        result.Headers.TryAddWithoutValidation("X-API-Key", "server-only-key");
        return result;
    }

    private static void CheckMetadata(JsonNode json)
    {
        Assert.Equal(3, (int?)json["response"]!["body"]!["future"]!["revision"]);
        var requestId = Assert.Single(json["response"]!["headers"]!.AsObject(), pair => pair.Key.Equals("x-request-id", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("fixture-id", (string?)requestId.Value);
        Assert.DoesNotContain("server-only", json.ToJsonString());
    }

    private static async Task<JsonNode> Json(HttpResponseMessage response) => JsonNode.Parse(await GatewayTests.Success(response))!;
}
