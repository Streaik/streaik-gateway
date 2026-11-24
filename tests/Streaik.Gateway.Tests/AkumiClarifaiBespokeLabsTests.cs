using System.Net;
using System.Text.Json.Nodes;

namespace Streaik.Gateway.Tests;

public sealed class AkumiClarifaiBespokeLabsTests
{
    private const string ClarifaiModel = "https://clarifai.com/vendor/app/models/Model";
    private const string ClarifaiInference = "/v2/ext/openai/v1";
    private static readonly (string, string) Akumi = ("X-Akumi-Key", "akumi-key");
    private static readonly (string, string) Clarifai = ("X-Clarifai-Key", "clarifai-key");
    private static readonly (string, string) BespokeLabs = ("X-BespokeLabs-Key", "bespoke-key");

    [Fact]
    public async Task Discovery_uses_provider_auth_and_preserves_native_model_ids()
    {
        await using var host = await FixtureHost.StartAsync(request => request.Path switch
        {
            "/v2/models?page=1" => FixtureHost.Json("""
                {"models":[{"user_id":"vendor","app_id":"app","name":"skip","model_type_id":"text-to-text",
                  "model_version":{"method_signatures":[{"name":"openai_transport"}]}}]}
                """),
            "/v2/models?page=2" => FixtureHost.Json("""
                {"models":[{"user_id":"vendor","app_id":"app","name":"Model","model_type_id":"text-to-image",
                  "model_version":{"method_signatures":[{"name":"openai_transport"},{"name":"openai_stream_transport"}]}}]}
                """),
            "/v2/models?page=3" => FixtureHost.Json("""{"models":[]}"""),
            _ => FixtureHost.Reply(request)
        }, akumiClarifaiBespokeLabs: true);

        using var anonymous = await host.GetAsync("/v1/models");
        Assert.Empty((await Json(anonymous))["data"]!.AsArray());
        using var response = await host.GetAsync("/v1/models", Akumi, Clarifai, BespokeLabs);
        var models = (await Json(response))["data"]!.AsArray().ToDictionary(item => item!["id"]!.GetValue<string>());
        Assert.Contains("akumi/vendor/Model", models);
        Assert.Equal("image", (string?)models[$"clarifai/{ClarifaiModel}"]!["type"]);
        Assert.DoesNotContain("clarifai/https://clarifai.com/vendor/app/models/skip", models);
        Assert.Equal(4, models.Keys.Count(id => id.StartsWith("bespokelabs/", StringComparison.Ordinal)));
        Assert.Equal("decision", (string?)models["bespokelabs/nimble-v4-max"]!["type"]);
        Assert.Equal(4, host.Requests.Count);
        Check(host.Requests.Single(request => request.Host == "api.akumi.cloud"), "api.akumi.cloud", "/v1/models", "Bearer akumi-key");
        Assert.Collection(host.Requests.Where(request => request.Host == "api.clarifai.com"),
            request => Check(request, "api.clarifai.com", "/v2/models?page=1", "Key clarifai-key"),
            request => Check(request, "api.clarifai.com", "/v2/models?page=2", "Key clarifai-key"),
            request => Check(request, "api.clarifai.com", "/v2/models?page=3", "Key clarifai-key"));
    }

    [Theory]
    [InlineData("akumi", "/v1/chat/completions", false, "future_response")]
    [InlineData("akumi", "/v1/chat/completions", true, "future_response")]
    [InlineData("akumi", "/v1/responses", true, "hello")]
    [InlineData("akumi", "/v1/messages", false, "hello")]
    [InlineData("clarifai", "/v1/chat/completions", false, "future_response")]
    [InlineData("clarifai", "/v1/chat/completions", true, "future_response")]
    [InlineData("clarifai", "/v1/responses", false, "future_response")]
    [InlineData("clarifai", "/v1/responses", true, "response.completed")]
    [InlineData("clarifai", "/v1/messages", true, "message_stop")]
    public async Task Text_protocols_use_the_correct_upstream(string provider, string path, bool stream, string marker)
    {
        await using var host = await FixtureHost.StartAsync(request => FixtureHost.Reply(request with
        { Path = request.Path.StartsWith(ClarifaiInference, StringComparison.Ordinal) ? "/v1" + request.Path[ClarifaiInference.Length..] : request.Path }),
            akumiClarifaiBespokeLabs: true);
        var isClarifai = provider == "clarifai";
        var model = isClarifai ? ClarifaiModel : "vendor/Model";
        var nativeResponses = isClarifai && path == "/v1/responses";
        var input = path == "/v1/responses" ? "\"input\":\"test\",\"max_output_tokens\":32" :
            "\"messages\":[{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"test\"}]}],\"max_tokens\":32";
        using var response = await host.PostAsync(path, $$"""
            {"model":"{{provider}}/{{model}}",{{input}},"stream":{{(stream ? "true" : "false")}}}
            """, isClarifai ? Clarifai : Akumi);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, text);
        Assert.Contains(marker, text);
        var request = Assert.Single(host.Requests);
        Check(request, isClarifai ? "api.clarifai.com" : "api.akumi.cloud",
            (isClarifai ? ClarifaiInference : "/v1") + (nativeResponses ? "/responses" : "/chat/completions"),
            isClarifai ? "Bearer clarifai-key" : "Bearer akumi-key");
        Assert.Equal(model, (string?)request.Body!["model"]);
        Assert.Equal(stream, request.Stream);
        if (!isClarifai) Assert.Equal("test", (string?)request.Body["messages"]!.AsArray().Last()!["content"]);
    }

    [Theory]
    [InlineData("akumi")]
    [InlineData("clarifai")]
    public async Task Vercel_chat_keeps_nested_native_options_and_metadata(string provider)
    {
        await using var host = await FixtureHost.StartAsync(_ => FixtureHost.Sse("""
            {"id":"chat_1","object":"chat.completion.chunk","created":1,"model":"native","choices":[{"index":0,"delta":{"content":"hello"},"finish_reason":"stop"}],"diagnostics":{"flags":[true,null]}}
            """, "[DONE]"), akumiClarifaiBespokeLabs: true);
        var key = provider == "akumi" ? Akumi : Clarifai;
        using var response = await host.PostAsync("/api/chat", $$"""
            {"model":"{{provider}}/native","messages":[{"id":"u1","role":"user","parts":[{"type":"text","text":"hi"}]}],
             "providerMetadata":{"{{provider}}":{"stream":false,"model":"other","headers":{"authorization":"wrong"},"native_options":{"trace":["yes"]} } } }
            """, key, ("Cookie", "do-not-forward"));
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, text);
        Assert.Contains("\"diagnostics\":{\"flags\":[true,null]}", text);
        var request = Assert.Single(host.Requests);
        Assert.EndsWith("/chat/completions", request.Path);
        Assert.Equal("native", (string?)request.Body!["model"]);
        Assert.True((bool?)request.Body["stream"]);
        Assert.Equal("yes", (string?)request.Body["native_options"]!["trace"]![0]);
        Assert.Null(request.Body["headers"]);
        Assert.False(request.Headers.ContainsKey("Cookie"));
        Assert.Equal($"Bearer {key.Item2}", request.Headers["Authorization"]);
    }

    [Theory]
    [InlineData("akumi")]
    [InlineData("clarifai")]
    public async Task Embeddings_keep_openai_contract_and_sdk_raw_body(string provider)
    {
        await using var host = await FixtureHost.StartAsync(_ => WithHeaders(FixtureHost.Json("""
            {"object":"list","data":[{"index":0,"object":"embedding","embedding":[0.25,0.5],"details":{"tier":"fast"}}],"usage":{"prompt_tokens":1,"total_tokens":1},"diagnostics":{"flags":[true,null]}}
            """)), akumiClarifaiBespokeLabs: true);
        var key = provider == "akumi" ? Akumi : Clarifai;
        using var native = await host.PostAsync("/v1/embeddings", $$"""
            {"model":"{{provider}}/vendor/embed","input":[[4,5]],"native_options":{"trace":true} }
            """, key);
        var nativeBody = await Json(native);
        Assert.Equal($"{provider}/vendor/embed", (string?)nativeBody["model"]);
        Assert.Equal(0.25, (double?)nativeBody["data"]![0]!["embedding"]![0]);
        using var sdk = await host.PostAsync("/api/embeddings", $$"""
            {"model":"{{provider}}/vendor/embed","values":["hello"],"providerOptions":{"{{provider}}":{"input":["bad"],"native_options":{"trace":true} } } }
            """, key);
        var reply = await Json(sdk);
        Assert.True((bool?)reply["response"]!["body"]!["diagnostics"]!["flags"]![0]);
        Assert.Equal("fast", (string?)reply["response"]!["body"]!["data"]![0]!["details"]!["tier"]);
        var headers = reply["response"]!["headers"]!.AsObject();
        Assert.Equal("request-fixture", (string?)Assert.Single(headers, header => header.Key.Equals("x-request-id", StringComparison.OrdinalIgnoreCase)).Value);
        Assert.DoesNotContain(headers, header => header.Key.Equals("x-private", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("hello", (string?)host.Requests.Last().Body!["input"]![0]);
        Assert.All(host.Requests, request =>
        {
            Assert.Equal((provider == "akumi" ? "/v1" : ClarifaiInference) + "/embeddings", request.Path);
            Assert.True((bool?)request.Body!["native_options"]!["trace"]);
        });
    }

    [Fact]
    public async Task Clarifai_image_supports_buffered_stream_and_sdk_raw_metadata()
    {
        await using var host = await FixtureHost.StartAsync(_ => FixtureHost.Json("""
            {"created":1,"data":[{"b64_json":"aGVsbG8=","native_image":{"trace":[3]}}],"diagnostics":{"flags":[true]}}
            """), akumiClarifaiBespokeLabs: true);
        using var sdk = await host.PostAsync("/api/images", $$"""
            {"model":"clarifai/{{ClarifaiModel}}","prompt":"hello","aspectRatio":"1:1","seed":12,
              "providerOptions":{"clarifai":{"prompt":"ignored","native_options":{"trace":true} } } }
            """, Clarifai);
        var image = await Json(sdk);
        Assert.Equal("data:image/png;base64,aGVsbG8=", (string?)image["images"]![0]);
        Assert.Equal(3, (int?)image["providerMetadata"]!["clarifai"]!["data"]![0]!["native_image"]!["trace"]![0]);
        Assert.Equal(2, image["warnings"]!.AsArray().Count);
        using var stream = await host.PostAsync("/v1/images/generations", $$"""
            {"model":"clarifai/{{ClarifaiModel}}","prompt":"hello","stream":true}
            """, Clarifai);
        var text = await stream.Content.ReadAsStringAsync();
        Assert.True(stream.IsSuccessStatusCode, text);
        Assert.Contains("image_generation.completed", text);
        Assert.All(host.Requests, request =>
        {
            Check(request, "api.clarifai.com", ClarifaiInference + "/images/generations", "Bearer clarifai-key");
            Assert.False(request.Stream);
            Assert.Equal("b64_json", (string?)request.Body!["response_format"]);
            Assert.Equal("hello", (string?)request.Body["prompt"]);
            Assert.Equal(ClarifaiModel, (string?)request.Body["model"]);
            Assert.Null(request.Body["seed"]);
        });
    }

    [Fact]
    public async Task BespokeLabs_sdk_keeps_raw_json_and_falls_back_only_on_not_found()
    {
        await using var host = await FixtureHost.StartAsync(request => request.Path == "/v1/systemone"
            ? FixtureHost.Json("{}", HttpStatusCode.NotFound)
            : WithHeaders(FixtureHost.Json("""
                {"model":"nimble-v4-max","answers":{"eligible":{"type":"noul","noul":0.85,"native_options":{"raw":true}},
                  "rating":{"type":"score","score":0.25,"confidence":0.75,"probabilities":{"0":0.75,"1":0.25},"legend":{"0":"low","1":"high"}}},
                  "usage":{"input_tokens":2,"output_tokens":4},"diagnostics":{"trace":["x"]}}
                """)), akumiClarifaiBespokeLabs: true);
        using var response = await host.PostAsync("/api/decisions", """
            {"model":"bespokelabs/nimble-v4-max","state":{"key":"value"},"questions":{
                "eligible":{"type":"boolean","criteria":{"true":"yes","false":"no"}},
                "rating":{"type":"score","instructions":"evaluate","criteria":["low","high"]}}}
            """, BespokeLabs);
        var result = await Json(response);
        Assert.Equal(.85, (double?)result["answers"]!["eligible"]!["probability"]);
        Assert.Equal(.25, (double?)result["answers"]!["rating"]!["score"]);
        Assert.True((bool?)result["providerMetadata"]!["bespokelabs"]!["answers"]!["eligible"]!["native_options"]!["raw"]);
        Assert.Equal("request-fixture", (string?)result["response"]!["id"]);
        Assert.Null(result["response"]!["headers"]!["x-private"]);
        Assert.Collection(host.Requests, first => Check(first, "api.bespokelabs.ai", "/v1/systemone", "Bearer bespoke-key"),
            second => Check(second, "api.bespokelabs.ai", "/v1/nimble/systemone", "Bearer bespoke-key"));
        Assert.Equal(host.Requests.First().RawBody, host.Requests.Last().RawBody);
        var body = host.Requests.First().Body!;
        Assert.Equal("value", (string?)body["state"]!["key"]);
        Assert.Equal("yes", (string?)body["questions"]!["eligible"]!["criteria"]!["true"]);
        Assert.Null(body["questions"]!["eligible"]!["instructions"]);
    }

    [Fact]
    public async Task BespokeLabs_openai_keeps_ordered_answers_and_preserves_choice_value_types()
    {
        await using var host = await FixtureHost.StartAsync(_ => FixtureHost.Json("""
            {"model":"nimble-latest","answers":{
              "question-1":{"type":"choice","choice":"option-1","confidence":0.8,"probabilities":{"option-0":0.2,"option-1":0.8}},
              "question-0":{"type":"noul","noul":0.25}},"usage":{"input_tokens":2,"output_tokens":1},"diagnostics":{"raw":[true]}}
            """), akumiClarifaiBespokeLabs: true);
        using var response = await host.PostAsync("/v1/decisions", """
            {"model":"bespokelabs/nimble-latest","input":"an example","questions":[
              {"type":"predicate","name":"first","instructions":"check it"},
              {"type":"choice","name":"second","instructions":"choose","choices":[{"value":"true","description":"text"},{"value":true,"description":"boolean"}]}]}
            """, BespokeLabs);
        var result = await Json(response);
        Assert.Equal("first", (string?)result["answers"]![0]!["name"]);
        Assert.True((bool?)result["answers"]![1]!["choice"]);
        Assert.Equal("true", (string?)result["answers"]![1]!["probabilities"]![0]!["value"]);
        Assert.True((bool?)result["providerMetadata"]!["bespokelabs"]!["diagnostics"]!["raw"]![0]);
        Assert.Equal(3, (int?)result["usage"]!["total_tokens"]);
        var request = Assert.Single(host.Requests);
        Check(request, "api.bespokelabs.ai", "/v1/systemone", "Bearer bespoke-key");
        Assert.Equal("boolean", (string?)request.Body!["questions"]!["question-1"]!["criteria"]!["option-1"]);
    }

    [Fact]
    public async Task BespokeLabs_does_not_retry_non_404_or_send_unsupported_options()
    {
        await using var host = await FixtureHost.StartAsync(_ => FixtureHost.Json("{}", HttpStatusCode.InternalServerError),
            akumiClarifaiBespokeLabs: true);
        const string body = """
            {"model":"bespokelabs/nimble-latest","input":"text","questions":[{"type":"predicate","instructions":"check it"}]}
            """;
        using var invalid = await host.PostAsync("/v1/decisions", body.Insert(1, "\"prompt_cache_key\":\"not-supported\","), BespokeLabs);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Empty(host.Requests);
        using var upstreamFailure = await host.PostAsync("/v1/decisions", body, BespokeLabs);
        Assert.False(upstreamFailure.IsSuccessStatusCode);
        Assert.Equal("/v1/systemone", Assert.Single(host.Requests).Path);
    }

    private static HttpResponseMessage WithHeaders(HttpResponseMessage response)
    {
        response.Headers.Add("x-request-id", "request-fixture");
        response.Headers.Add("x-private", "not-safe-metadata");
        return response;
    }

    private static async Task<JsonNode> Json(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        return JsonNode.Parse(body)!;
    }

    private static void Check(FixtureHost.UpstreamRequest request, string host, string path, string authorization)
    {
        Assert.Equal(host, request.Host);
        Assert.Equal(path, request.Path);
        Assert.Equal(authorization, request.Headers.GetValueOrDefault("Authorization"));
    }
}
