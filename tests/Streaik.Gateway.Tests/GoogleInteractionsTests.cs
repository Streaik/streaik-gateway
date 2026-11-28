using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Streaik.Vercel.Security;

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

    [Fact]
    public async Task Api_chat_replays_Gemini_tool_rounds_in_order_with_stateless_native_steps()
    {
        static JsonNode[] Steps(string json) => JsonNode.Parse(json)!.AsArray().Select(node => node!).ToArray();

        var expected = new List<JsonNode>(Steps("""[{"type":"user_input","content":[{"type":"text","text":"hello"}]}]"""));
        var firstRound = Steps("""
            [{"type":"thought","signature":"thought-one","summary":[{"type":"text","text":"Checking history."}]},
             {"type":"model_output","content":[{"type":"text","text":"Let me look."}]},
             {"type":"function_call","id":"fn_a","name":"lookup","arguments":{"city":"Delft"},"signature":"signature-a"},
             {"type":"function_call","id":"fn_b","name":"lookup","arguments":{"city":"Leiden"},"signature":"signature-b"}]
            """);
        var secondRound = Steps("""
            [{"type":"thought","signature":"thought-two"},
             {"type":"function_call","id":"fn_c","name":"lookup","arguments":{},"signature":"signature-c"}]
            """);
        var thirdRound = Steps("""
            [{"type":"thought","signature":"thought-three"},
             {"type":"model_output","content":[{"type":"text","text":"Found it."}]}]
            """);
        var round = 0;
        await using var host = await FixtureHost.StartAsync(request =>
        {
            AssertInteraction(request, stream: true);
            Assert.False((bool?)request.Body!["store"]);
            Assert.Null(request.Body["previous_interaction_id"]);
            var input = request.Body["input"]!.AsArray();
            Assert.Equal(expected.Count, input.Count);
            for (var index = 0; index < expected.Count; index++)
                Assert.True(JsonNode.DeepEquals(expected[index], input[index]), $"Step {index}: expected {expected[index]}, got {input[index]}");
            return ++round switch
            {
                1 => FixtureHost.Sse(
                    """{"event_type":"step.start","index":0,"step":{"type":"thought"}}""",
                    """{"event_type":"step.delta","index":0,"delta":{"type":"thought_summary","content":{"type":"text","text":"Checking history."}}}""",
                    """{"event_type":"step.delta","index":0,"delta":{"type":"thought_signature","signature":"thought-one"}}""",
                    """{"event_type":"step.stop","index":0}""",
                    """{"event_type":"step.delta","index":1,"delta":{"type":"text","text":"Let me look."}}""",
                    """{"event_type":"step.start","index":2,"step":{"type":"function_call","id":"fn_a","name":"lookup","arguments":{"city":"Delft"},"signature":"signature-a"}}""",
                    """{"event_type":"step.delta","index":2,"delta":{"type":"arguments_delta","arguments":"{\"city\":"}}""",
                    """{"event_type":"step.start","index":3,"step":{"type":"function_call","id":"fn_b","name":"lookup","arguments":{},"signature":"signature-b"}}""",
                    """{"event_type":"step.delta","index":3,"delta":{"type":"arguments_delta","arguments":"{\"city\":\"Leiden\"}"}}""",
                    """{"event_type":"step.delta","index":2,"delta":{"type":"arguments_delta","arguments":"\"Delft\"}"}}""",
                    """{"event_type":"step.stop","index":2}""",
                    """{"event_type":"step.stop","index":3}""",
                    Completed("""{"id":"private-google-run","status":"requires_action","steps":[{"type":"function_call","id":"fn_a","name":"lookup","signature":null,"arguments":{}}]}""")),
                2 => FixtureHost.Sse(Completed($$"""{"id":"private-google-run","status":"requires_action","steps":[{{string.Join(",", secondRound.Select(step => step.ToJsonString()))}}]}""")),
                3 => FixtureHost.Sse(Completed($$"""{"id":"private-google-run","status":"completed","steps":[{{string.Join(",", thirdRound.Select(step => step.ToJsonString()))}}],"usage":{{Usage}}}""")),
                4 => FixtureHost.Sse(Completed(Answer)),
                _ => throw new InvalidOperationException("Unexpected continuation request.")
            };
        }, services => services.AddSingleton(new ToolApprovalSigner("google-interactions-fixture-signing-secret")), liveProviders: true);

        var body = GatewayTests.Request("/api/chat", PricedModel, true);
        body["tools"] = JsonNode.Parse("""[{"name":"lookup","inputSchema":{"type":"object","properties":{"city":{"type":"string"}}}}]""");
        var history = body["messages"]!.AsArray();
        var assistantParts = new JsonArray();
        // The UI SDK appends all automatic continuations to the same assistant message.
        var assistant = new JsonObject { ["role"] = "assistant", ["id"] = "answer", ["parts"] = assistantParts };

        await ReadRound("Delft", "Leiden");
        history.Add(assistant);
        var withoutApproval = body.DeepClone();
        var unapprovedCall = withoutApproval["messages"]!.AsArray().Last()!["parts"]!.AsArray()
            .First(part => (string?)part?["type"] == "dynamic-tool")!.AsObject();
        unapprovedCall.Remove("approval");
        using (var rejected = await host.PostAsync("/api/chat", withoutApproval.ToJsonString(), ("x-google-key", "gemini-test")))
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Single(host.Requests);
        expected.AddRange(firstRound);
        expected.AddRange(Steps("""
            [{"type":"function_result","call_id":"fn_a","name":"lookup","result":[{"type":"text","text":"Result for Delft"}]},
             {"type":"function_result","call_id":"fn_b","name":"lookup","result":[{"type":"text","text":"Result for Leiden"}]}]
            """));

        await ReadRound("");
        expected.AddRange(secondRound);
        expected.AddRange(Steps("""
            [{"type":"function_result","call_id":"fn_c","name":"lookup","result":[{"type":"text","text":"No city"}]}]
            """));

        var finish = await ReadRound();
        AssertVercelUsage(finish["messageMetadata"]!);
        expected.AddRange(thirdRound);
        expected.AddRange(Steps("""[{"type":"user_input","content":[{"type":"text","text":"What else?"}]}]"""));
        history.Add(JsonNode.Parse("""{"role":"user","id":"follow-up","parts":[{"type":"text","text":"What else?"}]}"""));
        await ReadRound();
        Assert.Equal(4, host.Requests.Count);

        async Task<JsonNode> ReadRound(params string[] cities)
        {
            using var response = await host.PostAsync("/api/chat", body.ToJsonString(), ("x-google-key", "gemini-test"));
            var wire = await GatewayTests.Success(response);
            Assert.DoesNotContain("private-google-run", wire);
            var frames = Frames(wire);
            var finish = Assert.Single(frames, frame => (string?)frame["type"] == "finish");
            Assert.Equal(cities.Length > 0 ? "tool-calls" : "stop", (string?)finish["finishReason"]);
            var calls = frames.Where(frame => (string?)frame["type"] == "tool-input-available").ToArray();
            Assert.Equal(cities.Length, calls.Length);
            foreach (var frame in frames)
            {
                switch ((string?)frame["type"])
                {
                    case "start-step": assistantParts.Add(new JsonObject { ["type"] = "step-start" }); break;
                    case "text-delta": assistantParts.Add(new JsonObject { ["type"] = "text", ["text"] = frame["delta"]!.DeepClone() }); break;
                    case "reasoning-end" when frame["providerMetadata"] is { } metadata:
                        assistantParts.Add(new JsonObject { ["type"] = "reasoning", ["text"] = "", ["state"] = "done", ["providerMetadata"] = metadata.DeepClone() });
                        break;
                    case "tool-input-available":
                        var city = cities[Array.IndexOf(calls, frame)];
                        Assert.Equal(city, (string?)frame["input"]?["city"] ?? "");
                        var approval = Assert.Single(frames, candidate => (string?)candidate["type"] == "tool-approval-request"
                            && JsonNode.DeepEquals(candidate["toolCallId"], frame["toolCallId"]));
                        Assert.StartsWith("v1.", (string?)approval["signature"]);
                        assistantParts.Add(new JsonObject { ["type"] = "dynamic-tool", ["toolCallId"] = frame["toolCallId"]!.DeepClone(),
                            ["toolName"] = frame["toolName"]!.DeepClone(), ["input"] = frame["input"]!.DeepClone(),
                            ["state"] = "output-available", ["output"] = city.Length == 0 ? "No city" : $"Result for {city}",
                            ["approval"] = new JsonObject { ["id"] = approval["approvalId"]!.DeepClone(), ["approved"] = true,
                                ["signature"] = approval["signature"]!.DeepClone() } });
                        break;
                }
            }
            return finish;
        }
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
