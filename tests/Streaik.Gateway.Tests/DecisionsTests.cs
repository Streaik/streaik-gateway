using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Streaik.Gateway.Tests;

public sealed class DecisionsTests
{
    private const string Native = "/v1/decisions";
    private const string Sdk = "/api/decisions";

    [Theory]
    [InlineData("openai", "gpt-6-luna", "/v1/decisions")]
    [InlineData("fireworks", "accounts/fireworks/models/rune", "/inference/v1/decisions")]
    [InlineData("openrouter", "vendor/nested/model", "/api/alpha/decisions")]
    public async Task Both_shapes_route_to_the_native_decisions_endpoint(string provider, string model, string upstreamPath)
    {
        var router = provider == "openrouter";
        await using var host = await FixtureHost.StartAsync(request =>
        {
            Assert.Equal(upstreamPath, request.Path);
            Assert.Equal("Bearer selected", request.Headers["Authorization"]);
            Assert.DoesNotContain("x-anthropic-key", request.Headers.Keys, StringComparer.OrdinalIgnoreCase);
            var body = request.Body!;
            var evidence = body[router ? "state" : "input"]!.GetValue<string>();
            var questionId = router && evidence == "native" ? "question-0" : "flag";
            Assert.Equal(model, body["model"]!.GetValue<string>());
            if (evidence == "legacy") Assert.Equal("trace", request.Headers[$"x-{provider}-trace"]);
            if (router)
            {
                Assert.Null(body["input"]);
                Assert.Equal("noul", body["questions"]![questionId]!["type"]!.GetValue<string>());
            }
            else
            {
                Assert.Null(body["state"]);
                Assert.Equal("predicate", body["questions"]![0]!["type"]!.GetValue<string>());
                Assert.Equal("flag", body["questions"]![0]!["name"]!.GetValue<string>());
            }
            var response = FixtureHost.Json(router
                ? $$$"""{"model":"{{{model}}}","answers":{"{{{questionId}}}":{"type":"noul","noul":0.9}},"usage":{"input_tokens":4,"output_tokens":0,"cost":0.01}}"""
                : $$"""{"model":"{{model}}","answers":[{"type":"predicate","name":"flag","probability":0.9}],"usage":{"input_tokens":4,"output_tokens":3,"total_tokens":7},"future_field":true}""");
            response.Headers.Add("X-Request-Id", "upstream-1");
            return response;
        }, decisions: true);

        using var openAI = await host.PostAsync(Native, $$"""
            {"model":"{{provider}}/{{model}}","input":"native","questions":[{"type":"predicate","name":"flag","instructions":"Flag?"}]}
            """, ("Authorization", "Bearer selected"), ("X-Anthropic-Key", "unrelated"));
        var native = JsonNode.Parse(await GatewayTests.Success(openAI))!;
        Assert.Equal(model, native["model"]!.GetValue<string>());
        Assert.Equal("predicate", native["answers"]![0]!["type"]!.GetValue<string>());
        Assert.Equal(0.9, native["answers"]![0]!["probability"]!.GetValue<double>());
        Assert.Equal(router ? 4 : 7, native["usage"]!["total_tokens"]!.GetValue<int>());
        Assert.Null(native["providerMetadata"]?["gateway"]);
        Assert.Null(native["state"]);
        if (router) Assert.NotNull(native["providerMetadata"]!["openrouter"]);
        else Assert.True(native["future_field"]!.GetValue<bool>());

        using var sdk = await host.PostAsync(Sdk, $$$"""
            {"model":"{{{provider}}}/{{{model}}}","state":"legacy","questions":{"flag":{"type":"boolean","instructions":"Flag?"}},
             "headers":{"x-{{{provider}}}-trace":"trace","Authorization":"Bearer not-a-key"}}
            """, ($"X-{provider}-Key", "selected"));
        var result = JsonNode.Parse(await GatewayTests.Success(sdk))!;
        Assert.Equal("boolean", result["answers"]!["flag"]!["type"]!.GetValue<string>());
        Assert.Equal(0.9, result["answers"]!["flag"]!["probability"]!.GetValue<double>());
        Assert.Equal(4, result["usage"]!["inputTokens"]!.GetValue<int>());
        Assert.Equal($"{provider}/{model}", result["response"]!["modelId"]!.GetValue<string>());
        Assert.Equal("upstream-1", result["response"]!["id"]!.GetValue<string>());
        Assert.NotNull(result["providerMetadata"]![provider]);
        var cost = (decimal?)result["providerMetadata"]?["gateway"]?["cost"];
        Assert.Equal(provider switch { "openai" => 0.0000004m, "openrouter" => 0.01m, _ => (decimal?)null }, cost);
        Assert.Null(result["gateway"]);
    }

    [Fact]
    public async Task Fireworks_discovers_available_models_without_conflating_chat_with_decisions()
    {
        await using var host = await FixtureHost.StartAsync(request =>
        {
            Assert.Equal("api.fireworks.ai", request.Host);
            Assert.Equal("/inference/v1/models", request.Path);
            Assert.Equal("Bearer fixture", request.Headers["Authorization"]);
            return FixtureHost.Json("""
                {"data":[{"id":"accounts/fireworks/models/rune","context_length":2048},
                    {"id":"accounts/team/models/classifier","type":"decision"},
                    {"id":"accounts/team/models/retired","type":"decision","active":false},
                    {"id":"accounts/fireworks/models/chat","created":1700000000}]}
                """);
        }, decisions: true);
        using var anonymous = await host.GetAsync("/v1/models");
        Assert.Empty(JsonNode.Parse(await GatewayTests.Success(anonymous))!["data"]!.AsArray());
        Assert.Empty(host.Requests);

        using var response = await host.GetAsync("/v1/models", ("X-Fireworks-Key", "fixture"));
        var models = JsonNode.Parse(await GatewayTests.Success(response))!["data"]!.AsArray();
        Assert.Equal(new[] { "fireworks/accounts/fireworks/models/rune", "fireworks/accounts/team/models/classifier", "fireworks/accounts/fireworks/models/chat" },
            models.Select(x => x!["id"]!.GetValue<string>()));
        Assert.Equal(new[] { "decision", "decision", "language" }, models.Select(x => (string)x!["type"]!));
        Assert.Equal(1700000000L, (long)models[2]!["created"]!);
        Assert.Single(host.Requests);
    }

    [Fact]
    public async Task OpenRouter_restores_typed_choices_and_normalized_score_in_request_order()
    {
        await using var host = await FixtureHost.StartAsync(request =>
        {
            Assert.Null(request.Body!["user"]);
            Assert.Null(request.Body["safety_identifier"]);
            var criteria = request.Body!["questions"]!["question-0"]!["criteria"]!.AsObject();
            Assert.Equal(new[] { "option-0", "option-1" }, criteria.Select(x => x.Key));
            return FixtureHost.Json("""
                {"model":"org/decision","answers":{
                    "question-1":{"type":"score","score":1.5,"probabilities":{"0":0,"1":0.5,"2":0.5}},
                    "question-0":{"type":"choice","choice":"option-0","confidence":0.8,"probabilities":{"option-0":0.8,"option-1":0.2}}},
                 "usage":{"input_tokens":5,"output_tokens":2}}
                """);
        }, decisions: true);
        using var response = await host.PostAsync(Native, """
            {"model":"openrouter/org/decision","input":"evidence","safety_identifier":"opaque","questions":[
                {"type":"choice","name":"same","instructions":"Choose","choices":[{"value":true},{"value":"true"}]},
                {"type":"score","name":"same","instructions":"Rate","levels":[{"label":"low"},{"label":"mid"},{"label":"high"}]}]}
            """, ("X-OpenRouter-Key", "fixture"));
        var payload = JsonNode.Parse(await GatewayTests.Success(response))!;
        var answers = payload["answers"]!;
        Assert.True(answers[0]!["choice"]!.GetValue<bool>());
        Assert.Equal("true", answers[0]!["probabilities"]![1]!["value"]!.GetValue<string>());
        Assert.Equal(0.75, answers[1]!["score"]!.GetValue<double>());
        Assert.Equal("mid", answers[1]!["probabilities"]![1]!["label"]!.GetValue<string>());
        Assert.Contains(payload["warnings"]!.AsArray(), warning => warning?["feature"]?.GetValue<string>() == "safety_identifier");
    }

    [Fact]
    public async Task Sdk_state_parts_and_boolean_criteria_translate_without_losing_evidence()
    {
        await using var host = await FixtureHost.StartAsync(request =>
        {
            var body = request.Body!;
            Assert.Equal("text", body["input"]![0]!["content"]![0]!["text"]!.GetValue<string>());
            Assert.Equal("{\"x\":1}", body["input"]![0]!["content"]![1]!["text"]!.GetValue<string>());
            Assert.Equal("input_image", body["input"]![0]!["content"]![2]!["type"]!.GetValue<string>());
            Assert.True(body["questions"]![0]!["choices"]![0]!["value"]!.GetValue<bool>());
            return FixtureHost.Json("""
                {"model":"m","answers":[{"type":"choice","name":"flag","choice":false,"confidence":0.8,
                    "probabilities":[{"value":true,"probability":0.2},{"value":false,"probability":0.8}]}],
                 "usage":{"input_tokens":4,"output_tokens":0,"total_tokens":4}}
                """);
        });
        using var response = await host.PostAsync(Sdk, """
            {"model":"openai/m","stateFormat":"sdk","state":[{"type":"text","text":"text"},{"type":"json","value":{"x":1}},
                {"type":"file","mediaType":"image/png","data":{"type":"data","data":"AQID"}}],
             "questions":{"flag":{"type":"boolean","instructions":"Check","criteria":{"true":"yes","false":"no"}}}}
            """, ("X-OpenAI-Key", "fixture"));
        Assert.Equal(0.2, JsonNode.Parse(await GatewayTests.Success(response))!["answers"]!["flag"]!["probability"]!.GetValue<double>());
    }

    [Fact]
    public async Task Routes_return_gateway_errors_before_sending_invalid_requests()
    {
        await using var host = await FixtureHost.StartAsync(decisions: true);
        const string body = """{"model":"fireworks/rune","input":"text","questions":[{"type":"predicate","instructions":"Check"}]}""";
        using var noKey = await host.PostAsync(Native, body);
        Assert.Equal(HttpStatusCode.Unauthorized, noKey.StatusCode);
        using var noCapability = await host.PostAsync(Native, body.Replace("fireworks/", "anthropic/"), ("X-Anthropic-Key", "fixture"));
        Assert.Equal(HttpStatusCode.BadRequest, noCapability.StatusCode);
        using var badBody = await host.PostAsync(Sdk,
            """{"model":"openrouter/vendor/model","state":"evidence","questions":{"flag":{"type":"box","instructions":"Find"}}}""", ("X-OpenRouter-Key", "fixture"));
        Assert.Equal(HttpStatusCode.BadRequest, badBody.StatusCode);
        Assert.Empty(host.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.OK, HttpStatusCode.BadGateway)]
    public async Task Upstream_status_and_invalid_success_are_handled_without_leaking_payloads(HttpStatusCode upstream, HttpStatusCode expected)
    {
        await using var host = await FixtureHost.StartAsync(_ => FixtureHost.Json("""{"error":"private upstream detail"}""", upstream));
        using var response = await host.PostAsync(Native,
            """{"model":"openai/model","input":"text","questions":[{"type":"predicate","instructions":"Check"}]}""", ("X-OpenAI-Key", "fixture"));
        Assert.Equal(expected, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.DoesNotContain("private upstream detail", error!.ToJsonString());
    }
}
