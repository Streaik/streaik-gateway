using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace Streaik.Gateway.Tests;

public sealed class HuggingFaceMixedbreadSegmindTests
{
    private static readonly (string, string) HuggingFace = ("x-huggingface-key", "hf-fixture");
    private static readonly (string, string) Mixedbread = ("x-mixedbread-key", "mxbai-fixture");
    private static readonly (string, string) Segmind = ("x-segmind-key", "segmind-fixture");

    [Fact]
    public async Task Models_use_credential_scoped_static_or_upstream_catalogs()
    {
        await using var host = await Start(request =>
        {
            Check(request, "router.huggingface.co", "/v1/models", "hf-fixture");
            return FixtureHost.Json("""{"data":[{"id":"vendor/Model","owned_by":"vendor"},{"id":"vendor/old","active":false}]}""");
        });
        using var unkeyed = await host.GetAsync("/v1/models");
        Assert.DoesNotContain("segmind/", await GatewayTests.Success(unkeyed));
        Assert.Empty(host.Requests);

        using var response = await host.GetAsync("/v1/models", HuggingFace, Mixedbread, Segmind);
        var models = (await Json(response))["data"]!.AsArray().ToDictionary(m => (string)m!["id"]!);
        Assert.Equal("language", (string?)models["huggingface/vendor/Model"]!["type"]);
        Assert.DoesNotContain("huggingface/vendor/old", models.Keys);
        Assert.Equal(131_072, (int?)models["mixedbread/toast-1"]!["context_window"]);
        Assert.Equal("image", (string?)models["segmind/fast-flux-schnell"]!["type"]);
        Assert.Contains("segmind/background-removal", models.Keys);
        Assert.Single(host.Requests);
    }

    [Theory]
    [InlineData("huggingface", "/v1/chat/completions", false)]
    [InlineData("huggingface", "/v1/chat/completions", true)]
    [InlineData("huggingface", "/api/chat", true)]
    [InlineData("mixedbread", "/v1/chat/completions", false)]
    [InlineData("mixedbread", "/v1/chat/completions", true)]
    [InlineData("mixedbread", "/v1/responses", false)]
    [InlineData("mixedbread", "/v1/responses", true)]
    [InlineData("mixedbread", "/v1/messages", false)]
    [InlineData("mixedbread", "/v1/messages", true)]
    [InlineData("mixedbread", "/api/chat", true)]
    public async Task Chat_and_Responses_keep_native_options_and_route_translated_turns(string provider, string route, bool stream)
    {
        var hf = provider == "huggingface";
        var model = hf ? "vendor/Model:fastest" : "toast-1";
        await using var host = await Start(request =>
        {
            Check(request, hf ? "router.huggingface.co" : "api.mixedbread.com", route == "/v1/responses" ? route : "/v1/chat/completions",
                hf ? "hf-fixture" : "mxbai-fixture");
            Assert.Equal(model, (string?)request.Body!["model"]);
            Assert.Equal(stream, request.Stream);
            if (route != "/v1/messages")
            {
                Assert.True((bool)request.Body["future_option"]!["nested"]![0]!);
                Assert.Null(request.Body["future_option"]!["nested"]![1]);
            }
            return FixtureHost.Reply(request);
        });
        var payload = JsonNode.Parse(route switch
        {
            "/v1/responses" => """{"input":"Hi"}""",
            "/v1/messages" => """{"max_tokens":20,"messages":[{"role":"user","content":"Hi"}]}""",
            "/api/chat" => """{"messages":[{"id":"u","role":"user","parts":[{"type":"text","text":"Hi"}]}]}""",
            _ => """{"messages":[{"role":"user","content":"Hi"}]}"""
        })!.AsObject();
        var option = JsonNode.Parse("""{"nested":[true,null]}""");
        payload["model"] = $"{provider}/{model}";
        if (route == "/api/chat") payload["providerMetadata"] = new JsonObject { [provider] = new JsonObject { ["future_option"] = option } };
        else
        {
            payload["stream"] = stream;
            if (route != "/v1/messages") payload["future_option"] = option;
        }
        using var response = await host.PostAsync(route, payload.ToJsonString(), hf ? HuggingFace : Mixedbread);
        var wire = await GatewayTests.Success(response);
        Assert.Contains("hello", wire);
        Assert.Contains("future_response", wire);
        if (stream) Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Single(host.Requests);
    }

    [Fact]
    public async Task Hugging_Face_preserves_inputs_batch_order_raw_metadata_and_base64_encoding()
    {
        await using var host = await Start(request =>
        {
            Check(request, "router.huggingface.co", "/hf-inference/models/vendor/model/pipeline/feature-extraction", "hf-fixture");
            Assert.Equal("a", (string?)request.Body!["parameters"]!["prompt_name"]);
            Assert.True(request.Body["parameters"]!.AsObject().ContainsKey("future"));
            Assert.Null(request.Body["model"]);
            Assert.Null(request.Body["encoding_format"]);
            var input = request.Body["inputs"]!;
            Assert.Equal("a", input is JsonArray batch ? (string?)batch[0] : (string?)input);
            return Metadata(FixtureHost.Json(input is JsonArray ? "[[1,-2.5],[3,4]]" : "[1,-2.5]"));
        });
        using var native = await host.PostAsync("/v1/embeddings", """
            {"model":"huggingface/vendor/model","input":"a","inputs":"ignored","encoding_format":"base64","parameters":{"prompt_name":"a","future":null}}
            """, HuggingFace);
        var embedding = await Json(native);
        Assert.Equal("huggingface/vendor/model", (string?)embedding["model"]);
        Assert.Equal(new byte[] { 0, 0, 128, 63, 0, 0, 32, 192 }, Convert.FromBase64String((string)embedding["data"]![0]!["embedding"]!));

        using var vercel = await host.PostAsync("/api/embeddings", """
            {"model":"huggingface/vendor/model","values":["a","b"],"providerOptions":{"huggingface":{"inputs":["ignored"],"parameters":{"prompt_name":"a","future":null}},"other":{"unused":true}}}
            """, HuggingFace);
        var result = await Json(vercel);
        Assert.Equal("[[1,-2.5],[3,4]]", result["embeddings"]!.ToJsonString());
        Assert.Equal("[[1,-2.5],[3,4]]", result["response"]!["body"]!.ToJsonString());
        Assert.Contains("fixture-id", result["response"]!["headers"]!.ToJsonString());
        Assert.DoesNotContain("never-forward-this", result.ToJsonString());
    }

    [Theory]
    [InlineData("[]", HttpStatusCode.UnprocessableEntity)]
    [InlineData("[42]", HttpStatusCode.UnprocessableEntity)]
    [InlineData("\"test\"", HttpStatusCode.BadGateway)]
    public async Task Hugging_Face_rejects_invalid_inputs_or_malformed_vectors(string input, HttpStatusCode expected)
    {
        await using var host = await Start(_ => FixtureHost.Json("[[1,\"broken\"]]"));
        using var response = await host.PostAsync("/v1/embeddings", $$"""{"model":"huggingface/vendor/model","input":{{input}} }""", HuggingFace);
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(expected == HttpStatusCode.BadGateway ? 1 : 0, host.Requests.Count);
    }

    [Fact]
    public async Task Segmind_preserves_raw_json_and_returns_binary_image_and_safe_metadata()
    {
        await using var host = await Start(request =>
        {
            Check(request, "api.segmind.com", "/v1/background-removal");
            Assert.Equal("segmind-fixture", request.Headers["x-api-key"]);
            var body = request.Body!.AsObject();
            Assert.Equal(8475609304L, (long?)body["seed"]);
            Assert.Equal("16:9", (string?)body["aspect_ratio"]);
            Assert.Equal(3, body["future"]!["nested"]!.AsArray().Count);
            Assert.Contains("12345678901234567890.123456789", request.RawBody);
            Assert.Equal("inline-native-image", (string?)body["image"]);
            foreach (var key in new[] { "model", "stream", "prompt", "n", "size", "ignored" }) Assert.False(body.ContainsKey(key));
            return BinaryImage();
        });
        using var response = await host.PostAsync("/api/images", """
            {"model":"segmind/background-removal","n":2,"size":"1024x1024","seed":42,"aspectRatio":"1:1","providerOptions":{
                "segmind":{"seed":8475609304,"aspect_ratio":"16:9","image":"inline-native-image","model":"ignored","stream":true,"prompt":"ignored",
                    "future":{"nested":[true,null,12345678901234567890.123456789]}},"other":{"ignored":true}}}
            """, Segmind);
        var result = await Json(response);
        Assert.Equal("data:image/webp;base64,AQID", (string?)result["images"]![0]);
        Assert.Equal("segmind/background-removal", (string?)result["response"]!["modelId"]);
        Assert.Equal(2, result["warnings"]!.AsArray().Count);
        Assert.Equal("42873396", (string?)result["providerMetadata"]!["segmind"]!["headers"]!["x-seed-value"]);
        Assert.DoesNotContain("never-forward-this", result.ToJsonString());
    }

    [Theory]
    [InlineData("/v1/images/generations", false, "output_format")]
    [InlineData("/v1/images/generations", true, "image_generation.completed")]
    [InlineData("/v1/chat/completions", false, "image_url")]
    [InlineData("/v1/chat/completions", true, "image_url")]
    [InlineData("/v1/responses", false, "image_generation_call")]
    [InlineData("/v1/responses", true, "response.completed")]
    [InlineData("/v1/messages", false, "image/webp")]
    [InlineData("/v1/messages", true, "message_stop")]
    [InlineData("/api/chat", true, "image/webp")]
    public async Task Segmind_image_capability_is_reused_by_existing_protocol_adapters(string route, bool stream, string expected)
    {
        await using var host = await Start(request =>
        {
            Check(request, "api.segmind.com", "/v1/fast-flux-schnell");
            Assert.False(request.Stream);
            Assert.Equal("a tree", (string?)request.Body!["prompt"]);
            Assert.True((bool)request.Body["future"]!["native"]!);
            return BinaryImage();
        });
        var payload = JsonNode.Parse(route switch
        {
            "/v1/images/generations" => """{"prompt":"a tree","future":{"native":true}}""",
            "/v1/responses" => """{"input":"a tree","provider_options":{"segmind":{"future":{"native":true}}}}""",
            "/v1/messages" => """{"max_tokens":20,"messages":[{"role":"user","content":"a tree"}],"provider_options":{"segmind":{"future":{"native":true}}}}""",
            "/api/chat" => """{"messages":[{"id":"u","role":"user","parts":[{"type":"text","text":"a tree"}]}],"providerMetadata":{"segmind":{"future":{"native":true}}}}""",
            _ => """{"messages":[{"role":"user","content":"a tree"}],"provider_options":{"segmind":{"future":{"native":true}}}}"""
        })!.AsObject();
        payload["model"] = "segmind/fast-flux-schnell";
        if (route != "/api/chat") payload["stream"] = stream;
        using var response = await host.PostAsync(route, payload.ToJsonString(), Segmind);
        var wire = await GatewayTests.Success(response);
        Assert.Contains(expected, wire);
        Assert.Contains("AQID", wire);
        if (route != "/v1/images/generations" || !stream) Assert.Contains("42873396", wire);
        if (stream) Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Single(host.Requests);
    }

    [Fact]
    public async Task Segmind_rejects_dedicated_edits_and_upstream_errors_remain_sanitized()
    {
        await using var host = await Start(_ => FixtureHost.Json("""{"error":"never-forward-this"}""", HttpStatusCode.BadGateway));
        using var edit = await host.PostAsync("/v1/images/edits", """
            {"model":"segmind/fast-flux-schnell","prompt":"a tree","images":[{"image_url":"data:image/png;base64,AQID"}]}
            """, Segmind);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, edit.StatusCode);
        Assert.Empty(host.Requests);
        using var generation = await host.PostAsync("/v1/images/generations", """{"model":"segmind/fast-flux-schnell","prompt":"a tree"}""", Segmind);
        Assert.Equal(HttpStatusCode.BadGateway, generation.StatusCode);
        Assert.DoesNotContain("never-forward-this", await generation.Content.ReadAsStringAsync());
    }

    private static Task<FixtureHost> Start(Func<FixtureHost.UpstreamRequest, HttpResponseMessage> reply)
        => FixtureHost.StartAsync(reply, huggingFaceMixedbreadSegmind: true);

    private static void Check(FixtureHost.UpstreamRequest request, string host, string path, string? bearer = null)
    {
        Assert.Equal(host, request.Host);
        Assert.Equal(path, request.Path);
        Assert.Equal(bearer is null ? null : $"Bearer {bearer}", request.Headers.GetValueOrDefault("Authorization"));
        Assert.DoesNotContain(request.Headers.Keys, name => name is "x-segmind-key" or "x-huggingface-key" or "x-mixedbread-key");
    }

    private static HttpResponseMessage Metadata(HttpResponseMessage response)
    {
        response.Headers.Add("x-request-id", "fixture-id");
        response.Headers.Add("set-cookie", "never-forward-this");
        response.Headers.Add("x-private", "never-forward-this");
        return response;
    }

    private static HttpResponseMessage BinaryImage()
    {
        var response = Metadata(new(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/webp");
        response.Headers.Add("x-seed-value", "42873396");
        return response;
    }

    private static async Task<JsonNode> Json(HttpResponseMessage response) => JsonNode.Parse(await GatewayTests.Success(response))!;
}
