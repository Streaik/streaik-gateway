using System.Net;
using System.Text.Json.Nodes;

namespace Streaik.Gateway.Tests;

public sealed class GoogleInteractionsTests
{
    private const string PricedModel = "google/gemini-3.8-flash";
    private const string Usage = """
        {"total_input_tokens":100,"total_cached_tokens":20,"total_output_tokens":30,"total_thought_tokens":10,"total_tokens":140}
        """;
    // 80 regular input, 20 cached input, 40 output (including thoughts), at the catalog's per-token rates.
    private const decimal Cost = 0.0002115m;
    private const string Answer = """
        {"id":"private-google-run","status":"completed","steps":[{"type":"model_output","content":[{"type":"text","text":"From Gemini"}]}],"usage":{"total_input_tokens":7,"total_output_tokens":3,"total_tokens":10}}
        """;

    [Theory]
    [InlineData("/api/chat", true)]
    [InlineData("/v1/responses", true)]
    [InlineData("/v1/responses", false)]
    [InlineData("/v1/messages", true)]
    [InlineData("/v1/messages", false)]
    public async Task Gemini_usage_and_estimated_cost_survive_gateway_translation(string route, bool stream)
    {
        var answer = AnswerWithUsage(Usage);
        await using var host = await FixtureHost.StartAsync(request =>
        {
            AssertInteraction(request, stream);
            return stream ? FixtureHost.Sse("""
                {"event_type":"step.delta","index":0,"delta":{"type":"text","text":"From Gemini"},"metadata":{"total_usage":{"total_input_tokens":100,"total_output_tokens":1}}}
                """, Completed(answer)) : FixtureHost.Json(answer);
        }, liveProviders: true);

        var body = await Post(host, route, PricedModel, stream);
        if (route == "/api/chat")
        {
            var metadata = FinishMetadata(body);
            AssertVercelUsage(metadata);
            AssertEstimate(metadata["providerMetadata"]?["gateway"]);
        }
        else if (route == "/v1/responses")
        {
            var result = stream ? Assert.Single(Frames(body), node => (string?)node["type"] == "response.completed")["response"]! : JsonNode.Parse(body)!;
            var usage = result["usage"]!;
            Assert.Equal(100, (int?)usage["input_tokens"]);
            Assert.Equal(40, (int?)usage["output_tokens"]);
            Assert.Equal(140, (int?)usage["total_tokens"]);
            Assert.Equal(20, (int?)usage["input_tokens_details"]?["cached_tokens"]);
            Assert.Equal(10, (int?)usage["output_tokens_details"]?["reasoning_tokens"]);
            AssertEstimate(result["metadata"]?["gateway"]);
        }
        else
        {
            var frames = stream ? Frames(body) : [JsonNode.Parse(body)!];
            var usage = (stream ? Assert.Single(frames, node => (string?)node["type"] == "message_delta") : frames[0])["usage"]!;
            Assert.Equal(80, (int?)usage["input_tokens"]);
            Assert.Equal(40, (int?)usage["output_tokens"]);
            Assert.Equal(20, (int?)usage["cache_read_input_tokens"]);
            Assert.Equal(10, (int?)usage["output_tokens_details"]?["thinking_tokens"]);
            AssertEstimate((stream ? Assert.Single(frames, node => (string?)node["type"] == "message_stop") : frames[0])["metadata"]?["gateway"]);
        }
        Assert.Single(host.Requests);
    }

    [Fact]
    public async Task Api_chat_uses_latest_cumulative_usage_even_without_a_step_index_or_reported_total()
    {
        await using var host = await FixtureHost.StartAsync(request =>
        {
            AssertInteraction(request, stream: true);
            return FixtureHost.Sse("""
                {"event_type":"step.delta","index":0,"delta":{"type":"text","text":"From Gemini"},"metadata":{"total_usage":{"total_input_tokens":100,"total_output_tokens":1}}}
                """, """
                {"event_type":"interaction.completed","interaction":{"id":"private-google-run","status":"completed","usage":{}},"metadata":{"total_usage":{"total_input_tokens":100,"total_cached_tokens":20,"total_output_tokens":30,"total_thought_tokens":10}}}
                """);
        }, liveProviders: true);
        var metadata = FinishMetadata(await Post(host, "/api/chat", PricedModel, stream: true));
        AssertVercelUsage(metadata);
        AssertEstimate(metadata["providerMetadata"]?["gateway"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{}")]
    [InlineData("""{"total_input_tokens":100}""")]
    public async Task Api_chat_leaves_missing_usage_and_cost_unknown(string? usage)
    {
        await using var host = await FixtureHost.StartAsync(request =>
        {
            AssertInteraction(request, stream: true);
            return FixtureHost.Sse(Completed(AnswerWithUsage(usage)));
        }, liveProviders: true);
        var metadata = FinishMetadata(await Post(host, "/api/chat", PricedModel, stream: true));
        Assert.Null(metadata["usage"]?["promptTokens"]);
        Assert.Null(metadata["usage"]?["completionTokens"]);
        Assert.Null(metadata["usage"]?["totalTokens"]);
        Assert.Null(metadata["providerMetadata"]?["gateway"]?["cost"]);
    }

    [Fact]
    public async Task Api_chat_reports_usage_without_cost_for_an_unpriced_model()
    {
        await using var host = await FixtureHost.StartAsync(request =>
        {
            AssertInteraction(request, stream: true);
            return FixtureHost.Json(AnswerWithUsage(Usage));
        }, liveProviders: true);
        var metadata = FinishMetadata(await Post(host, "/api/chat", "google/gemini-fixture-unpriced", stream: true));
        AssertVercelUsage(metadata);
        Assert.Null(metadata["providerMetadata"]?["gateway"]?["cost"]);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task Google_chat_completions_preserves_native_routing_and_stream_usage_opt_in(bool stream, bool includeUsage)
    {
        const string usage = """
            {"prompt_tokens":100,"completion_tokens":40,"total_tokens":140,"prompt_tokens_details":{"cached_tokens":20},"completion_tokens_details":{"reasoning_tokens":10}}
            """;
        await using var host = await FixtureHost.StartAsync(request =>
        {
            Assert.Equal("generativelanguage.googleapis.com", request.Host);
            Assert.Equal("/v1beta/openai/chat/completions", request.Path);
            Assert.Equal("gemini-3.8-flash", (string?)request.Body?["model"]);
            Assert.Equal(stream, request.Stream);
            Assert.Equal(includeUsage, (bool?)request.Body?["stream_options"]?["include_usage"] ?? false);
            if (!stream) return FixtureHost.Json("""
                {"id":"chat_1","model":"gemini-3.8-flash","created":1,"choices":[{"index":0,"message":{"role":"assistant","content":"From Gemini"},"finish_reason":"stop"}],"usage":
                """ + usage + "}");
            const string finish = """
                {"id":"chat_1","model":"gemini-3.8-flash","created":1,"choices":[{"index":0,"delta":{"content":"From Gemini"},"finish_reason":"stop"}]}
                """;
            return includeUsage ? FixtureHost.Sse(finish, """
                {"id":"chat_1","model":"gemini-3.8-flash","created":1,"choices":[],"usage":
                """ + usage + "}", "[DONE]") : FixtureHost.Sse(finish, "[DONE]");
        }, liveProviders: true);

        var body = await Post(host, "/v1/chat/completions", PricedModel, stream, includeUsage);
        var frames = stream ? Frames(body) : [JsonNode.Parse(body)!];
        if (!stream || includeUsage)
        {
            var result = Assert.Single(frames, node => node["usage"] is not null);
            Assert.Equal(140, (int?)result["usage"]?["total_tokens"]);
            AssertEstimate(result["metadata"]?["gateway"]);
        }
        else
        {
            Assert.All(frames, node =>
            {
                Assert.Null(node["usage"]);
                Assert.Null(node["metadata"]?["gateway"]?["cost"]);
            });
        }
        Assert.Single(host.Requests);
    }

    [Theory]
    [InlineData("/api/chat", """{"model":"google/gemini-3-flash","messages":[{"id":"u1","role":"user","parts":[{"type":"text","text":"Hello"}]}]}""", "text-delta")]
    [InlineData("/v1/responses", """{"model":"google/gemini-3-flash","input":"Hello","stream":true}""", "response.output_text.delta")]
    [InlineData("/v1/messages", """{"model":"google/gemini-3-flash","max_tokens":32,"messages":[{"role":"user","content":"Hello"}],"stream":true}""", "content_block_delta")]
    public async Task Existing_streaming_contracts_use_Google_Interactions_internally(string route, string body, string fragment)
    {
        await using var host = await FixtureHost.StartAsync(request =>
        {
            AssertInteraction(request, stream: true);
            Assert.Equal("gemini-3-flash", (string?)request.Body!["model"]);
            Assert.False((bool)request.Body["store"]!);
            Assert.Equal("user_input", (string?)request.Body["input"]![0]!["type"]);
            Assert.Equal("Hello", (string?)request.Body["input"]![0]!["content"]![0]!["text"]);
            if (route == "/v1/messages") Assert.Equal(32, (int?)request.Body["generation_config"]!["max_output_tokens"]);
            return FixtureHost.Sse("""{"event_type":"step.delta","index":0,"delta":{"type":"text","text":"From Gemini"}}""",
                "{\"event_type\":\"interaction.completed\",\"interaction\":" + Answer + "}");
        }, liveProviders: true);
        using var response = await host.PostAsync(route, body, ("x-google-key", "gemini-test"), ("x-openai-key", "unrelated"));
        var output = await GatewayTests.Success(response);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains(fragment, output);
        Assert.Contains("From Gemini", output);
        Assert.DoesNotContain("The upstream stream ended unexpectedly", output);
        Assert.DoesNotContain("private-google-run", output);
        Assert.Single(host.Requests);
    }

    [Theory]
    [InlineData("/v1/responses", """{"model":"google/gemini-3-flash","input":"Hello"}""")]
    [InlineData("/v1/messages", """{"model":"google/gemini-3-flash","max_tokens":32,"messages":[{"role":"user","content":"Hello"}]}""")]
    public async Task Existing_json_contracts_use_Google_Interactions_internally(string route, string body)
    {
        await using var host = await FixtureHost.StartAsync(request =>
        {
            AssertInteraction(request, stream: false);
            return FixtureHost.Json(Answer);
        }, liveProviders: true);
        using var response = await host.PostAsync(route, body, ("x-google-key", "gemini-test"));
        var output = await GatewayTests.Success(response);
        Assert.Contains("From Gemini", output);
        Assert.Contains("google/gemini-3-flash", output);
        Assert.DoesNotContain("private-google-run", output);
        Assert.Single(host.Requests);
    }

    [Fact]
    public async Task Agent_catalog_and_Vercel_execution_do_not_expose_native_resource_routes()
    {
        await using var host = await FixtureHost.StartAsync(request =>
        {
            Assert.Equal("gemini-test", request.Headers["x-goog-api-key"]);
            return (request.Method, request.Path) switch
            {
                ("GET", "/v1beta/models?pageSize=1000") => FixtureHost.Json("""{"models":[{"name":"models/gemini-3-flash","supportedGenerationMethods":["generateContent"]},{"name":"models/veo-3.1-generate-preview"},{"name":"models/gemini-embedding-2"}]}"""),
                ("GET", "/v1beta/agents?page_size=1000") => FixtureHost.Json("""{"data":[{"id":"trip-planner","display_name":"Trip Planner"}],"next_page_token":"more"}"""),
                ("GET", "/v1beta/agents?page_size=1000&page_token=more") => FixtureHost.Json("""{"agents":[{"id":"trip-planner"},{"id":"second"}]}"""),
                ("POST", "/v1beta/interactions") => StartAgent(request),
                ("DELETE", "/v1beta/interactions/private-google-run") => new(HttpStatusCode.NoContent),
                _ => throw new InvalidOperationException("Unexpected Google request.")
            };
        }, liveProviders: true);

        using var catalog = await host.GetAsync("/v1/models", ("x-google-key", "gemini-test"));
        var data = JsonNode.Parse(await GatewayTests.Success(catalog))!["data"]!.AsArray();
        var agent = Assert.Single(data, x => (string?)x?["id"] == "google/agents/trip-planner")!;
        Assert.Equal("language", (string?)agent["type"]);
        Assert.Equal("agent", (string?)Assert.Single(agent["tags"]!.AsArray()));
        Assert.Contains(data, x => (string?)x?["type"] == "video");
        Assert.Contains(data, x => (string?)x?["type"] == "embedding");
        Assert.Contains(data, x => (string?)x?["type"] == "transcription");

        using var chat = await host.PostAsync("/api/chat",
            """{"model":"google/agents/trip-planner","messages":[{"id":"u1","role":"user","parts":[{"type":"text","text":"Plan a visit"}]}]}""",
            ("x-google-key", "gemini-test"));
        var output = await GatewayTests.Success(chat);
        Assert.Contains("From Gemini", output);
        Assert.DoesNotContain("private-google-run", output);
        Assert.Single(host.Requests, x => x.Method == "DELETE");
        var upstreamCount = host.Requests.Count;
        using var list = await host.GetAsync("/v1beta/agents", ("x-google-key", "gemini-test"));
        using var lookup = await host.GetAsync("/v1beta/agents/trip-planner", ("x-google-key", "gemini-test"));
        using var create = await host.PostAsync("/v1beta/interactions", "{}", ("x-google-key", "gemini-test"));
        Assert.All(new[] { list, lookup, create }, result => Assert.Equal(HttpStatusCode.NotFound, result.StatusCode));
        Assert.Equal(upstreamCount, host.Requests.Count);

        static HttpResponseMessage StartAgent(FixtureHost.UpstreamRequest request)
        {
            AssertInteraction(request, stream: true);
            Assert.Equal("trip-planner", (string?)request.Body!["agent"]);
            Assert.Null(request.Body["model"]);
            Assert.True((bool)request.Body["store"]!);
            return FixtureHost.Sse("{\"event_type\":\"interaction.completed\",\"interaction\":" + Answer + "}");
        }
    }

    [Fact]
    public async Task Function_calls_with_signatures_round_trip_through_existing_chat_fields()
    {
        const string nativeTool = """
            {"id":"private-google-run","status":"requires_action","steps":[{"type":"function_call","id":"fn_1","name":"lookup","arguments":{"city":"Delft"},"signature":"google-signature"}]}
            """;
        await using var host = await FixtureHost.StartAsync(request =>
        {
            AssertInteraction(request, stream: false);
            var steps = request.Body!["input"]!.AsArray();
            if (steps.Count == 1)
            {
                Assert.Equal("lookup", (string?)request.Body["tools"]![0]!["name"]);
                return FixtureHost.Json(nativeTool);
            }
            Assert.Equal("google-signature", (string?)steps[1]!["signature"]);
            Assert.Equal("fn_1", (string?)steps[1]!["id"]);
            Assert.Equal("fn_1", (string?)steps[2]!["call_id"]);
            Assert.Equal("lookup", (string?)steps[2]!["name"]);
            return FixtureHost.Json(Answer);
        }, liveProviders: true);
        var request = JsonNode.Parse("""
            {"model":"google/gemini-3-flash","max_tokens":128,"messages":[{"role":"user","content":"Find Delft"}],
             "tools":[{"name":"lookup","input_schema":{"type":"object","properties":{"city":{"type":"string"}}}}]}
            """)!;
        using var first = await host.PostAsync("/v1/messages", request.ToJsonString(), ("x-google-key", "gemini-test"));
        var message = JsonNode.Parse(await GatewayTests.Success(first))!;
        Assert.Equal("tool_use", (string?)message["stop_reason"]);
        var next = request["messages"]!.AsArray();
        next.Add(new JsonObject { ["role"] = "assistant", ["content"] = message["content"]!.DeepClone() });
        next.Add(new JsonObject { ["role"] = "user", ["content"] = new JsonArray(new JsonObject
            { ["type"] = "tool_result", ["tool_use_id"] = (string?)message["content"]![0]!["id"], ["content"] = "Found" }) });
        using var second = await host.PostAsync("/v1/messages", request.ToJsonString(), ("x-google-key", "gemini-test"));
        Assert.Contains("From Gemini", await GatewayTests.Success(second));
    }

    private static async Task<string> Post(FixtureHost host, string route, string model, bool stream, bool includeUsage = false)
    {
        var request = GatewayTests.Request(route, model, stream);
        if (includeUsage) request["stream_options"] = new JsonObject { ["include_usage"] = true };
        using var response = await host.PostAsync(route, request.ToJsonString(), ("x-google-key", "gemini-test"));
        return await GatewayTests.Success(response);
    }

    private static string AnswerWithUsage(string? usage)
    {
        var answer = JsonNode.Parse(Answer)!;
        answer["usage"] = usage is null ? null : JsonNode.Parse(usage);
        return answer.ToJsonString();
    }

    private static string Completed(string answer) => "{\"event_type\":\"interaction.completed\",\"interaction\":" + answer + "}";

    private static JsonNode[] Frames(string sse) => sse.Split('\n').Where(line => line.StartsWith("data: {", StringComparison.Ordinal))
        .Select(line => JsonNode.Parse(line[6..])!).ToArray();

    private static JsonNode FinishMetadata(string sse) => Assert.Single(Frames(sse), node => (string?)node["type"] == "finish")["messageMetadata"]!;

    private static void AssertVercelUsage(JsonNode metadata)
    {
        Assert.Equal(100, (int?)metadata["usage"]?["promptTokens"]);
        Assert.Equal(40, (int?)metadata["usage"]?["completionTokens"]);
        Assert.Equal(140, (int?)metadata["usage"]?["totalTokens"]);
    }

    private static void AssertEstimate(JsonNode? metrics)
    {
        Assert.Equal(Cost, (decimal?)metrics?["cost"]);
        Assert.False(metrics!.AsObject().ContainsKey("currency"));
        Assert.False(metrics.AsObject().ContainsKey("estimated"));
    }

    private static void AssertInteraction(FixtureHost.UpstreamRequest request, bool stream)
    {
        Assert.Equal("generativelanguage.googleapis.com", request.Host);
        Assert.Equal("/v1beta/interactions", request.Path);
        Assert.Equal("2026-05-20", request.Headers["Api-Revision"]);
        Assert.Equal("gemini-test", request.Headers["x-goog-api-key"]);
        Assert.False(request.Headers.ContainsKey("Authorization"));
        Assert.False(request.Headers.ContainsKey("x-openai-key"));
        Assert.Equal(stream, request.Stream);
        Assert.Equal(stream, (bool?)request.Body?["stream"]);
    }
}
