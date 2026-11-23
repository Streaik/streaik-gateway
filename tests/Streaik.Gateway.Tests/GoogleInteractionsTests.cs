using System.Net;
using System.Text.Json.Nodes;

namespace Streaik.Gateway.Tests;

public sealed class GoogleInteractionsTests
{
    private const string Answer = """
        {"id":"private-google-run","status":"completed","steps":[{"type":"model_output","content":[{"type":"text","text":"From Gemini"}]}],"usage":{"total_input_tokens":7,"total_output_tokens":3,"total_tokens":10}}
        """;

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
