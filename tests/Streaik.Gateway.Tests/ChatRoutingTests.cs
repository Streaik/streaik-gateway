using System.Net;
using System.Text.Json.Nodes;
using Xunit;

namespace Streaik.Gateway.Tests;

public sealed class ChatRoutingTests
{
    private const string Chat = "/v1/chat/completions";
    private const string Responses = "/v1/responses";
    private const string Messages = "/v1/messages";
    private const string Web = "/api/chat";

    [Theory]
    [InlineData(Chat, false, Chat)]
    [InlineData(Chat, true, Chat)]
    [InlineData(Responses, false, Responses)]
    [InlineData(Responses, true, Responses)]
    [InlineData(Messages, false, Messages)]
    [InlineData(Messages, true, Messages)]
    [InlineData(Web, true, Responses)]
    public async Task OpenRouter_uses_the_native_upstream_contract(string route, bool stream, string upstream)
    {
        await using var host = await FixtureHost.StartAsync(request => FixtureHost.Reply(request with { Path = upstream }), media: true);
        const string model = "openrouter/vendor/Some-Model:free";
        var body = Body(route, model, stream);
        if (route != Web) body["provider"] = JsonNode.Parse("""{"sort":"latency"}""");
        using var response = await host.PostAsync(route, body.ToJsonString(), ("X-OpenRouter-Key", "router-key"),
            ("X-OpenAI-Key", "unrelated"), ("anthropic-beta", "example-beta"));
        var wire = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, wire);
        Assert.Contains("hello", wire);
        Assert.Contains(model, wire);
        var sent = Assert.Single(host.Requests);
        Assert.Equal("openrouter.ai", sent.Host);
        Assert.Equal("/api" + upstream, sent.Path);
        Assert.Equal("vendor/Some-Model:free", sent.Body!["model"]!.GetValue<string>());
        Assert.Equal("Bearer router-key", sent.Headers["Authorization"]);
        Assert.DoesNotContain("X-OpenAI-Key", sent.Headers.Keys, StringComparer.OrdinalIgnoreCase);
        if (route != Web) Assert.Equal("latency", sent.Body["provider"]!["sort"]!.GetValue<string>());
        if (route == Messages) Assert.Equal("example-beta", sent.Headers["anthropic-beta"]);
        if (route == Web) Assert.Contains("v1", response.Headers.GetValues("x-vercel-ai-ui-message-stream"));
    }

    public static TheoryData<string, string, string> CompatibleProviders => new()
    {
        { "deepseek", "api.deepseek.com", "" }, { "groq", "api.groq.com", "/openai/v1" },
        { "together", "api.together.ai", "/v1" }, { "cerebras", "api.cerebras.ai", "/v1" }
    };

    [Theory]
    [MemberData(nameof(CompatibleProviders))]
    public async Task Compatible_provider_lists_models_and_answers_every_route(string provider, string upstreamHost, string prefix)
    {
        await using var host = await FixtureHost.StartAsync(request => request.Path.EndsWith("/models", StringComparison.Ordinal)
            ? FixtureHost.Json(provider == "together" ? """[{"id":"vendor/Model","type":"chat"},{"id":"embedding","type":"embedding"}]"""
                : """{"data":[{"id":"vendor/Model"}]}""")
            : FixtureHost.Reply(request with { Path = Chat }), compatibleChat: true);
        var key = ($"X-{provider}-Key", "provider-key");
        var model = $"{provider}/vendor/Model";
        using var list = await host.GetAsync("/v1/models", key);
        Assert.True(list.IsSuccessStatusCode);
        var models = JsonNode.Parse(await list.Content.ReadAsStringAsync())!["data"]!.AsArray();
        Assert.Equal("language", (string?)Assert.Single(models, item => (string?)item?["id"] == model)!["type"]);
        if (provider == "together")
            Assert.Equal("embedding", (string?)Assert.Single(models, item => (string?)item?["id"] == "together/embedding")!["type"]);
        else Assert.Single(models);
        var discovery = Assert.Single(host.Requests);
        Assert.Equal(upstreamHost, discovery.Host);
        Assert.Equal(prefix + "/models", discovery.Path);
        Assert.Equal("Bearer provider-key", discovery.Headers["Authorization"]);

        foreach (var (route, stream) in new[] { (Chat, true), (Chat, false), (Responses, true), (Responses, false),
                     (Messages, true), (Messages, false), (Web, true) })
        {
            using var response = await host.PostAsync(route, Body(route, model, stream).ToJsonString(), key, ("X-OpenRouter-Key", "unrelated"));
            var wire = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, wire);
            Assert.Contains("hello", wire);
            Assert.Contains(model, wire);
            if (stream) Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
            if (route == Messages && stream)
            {
                var frames = wire.Split("\n\n");
                Assert.Contains("\"input_tokens\":0", Assert.Single(frames, frame => frame.StartsWith("event: message_start\n")));
                Assert.Contains("\"output_tokens\":0", Assert.Single(frames, frame => frame.StartsWith("event: message_delta\n")));
            }
            var sent = host.Requests.Last();
            Assert.Equal(upstreamHost, sent.Host);
            Assert.Equal(prefix + "/chat/completions", sent.Path);
            Assert.Equal(stream, sent.Stream);
            Assert.Equal("vendor/Model", sent.Body!["model"]!.GetValue<string>());
            Assert.False(sent.Body.AsObject().ContainsKey("store"));
            Assert.Equal("Bearer provider-key", sent.Headers["Authorization"]);
            Assert.DoesNotContain("X-OpenRouter-Key", sent.Headers.Keys, StringComparer.OrdinalIgnoreCase);
        }
        Assert.Equal(8, host.Requests.Count);
    }

    [Theory]
    [InlineData(Responses, "response.function_call_arguments.delta", "response.completed")]
    [InlineData(Messages, "input_json_delta", "message_stop")]
    [InlineData(Web, "tool-input-delta", "tool-input-available")]
    public async Task Translated_streams_preserve_function_ids_split_arguments_and_late_usage(string route, string delta, string terminal)
    {
        await using var host = await FixtureHost.StartAsync(_ => ToolStream(), compatibleChat: true);
        var body = Body(route, "groq/vendor/Model", true);
        body["tools"] = JsonNode.Parse(route switch
        {
            Responses => """[{"type":"function","name":"lookup","parameters":{"type":"object"}}]""",
            Messages => """[{"name":"lookup","input_schema":{"type":"object"}}]""",
            _ => """[{"name":"lookup","inputSchema":{"type":"object"}}]"""
        });
        using var response = await host.PostAsync(route, body.ToJsonString(), ("X-Groq-Key", "fixture-key"));
        var wire = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, wire);
        Assert.Contains(delta, wire);
        Assert.Contains(terminal, wire);
        Assert.Contains("call_1", wire);
        Assert.Contains("call_2", wire);
        Assert.Contains("Paris", wire);
        Assert.Contains("Rome", wire);
        Assert.Contains(route switch { Responses => "\"input_tokens\":11", Messages => "\"input_tokens\":11", _ => "\"promptTokens\":11" }, wire);
        var upstream = Assert.Single(host.Requests).Body!;
        Assert.Equal("lookup", upstream["tools"]![0]!["function"]!["name"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(Responses, """{"model":"deepseek/model","input":[{"role":"user","content":"lookup"},{"type":"function_call","call_id":"call_1","name":"lookup","arguments":"{\"id\":1}"},{"type":"function_call_output","call_id":"call_1","output":"found"}]}""")]
    [InlineData(Messages, """{"model":"deepseek/model","max_tokens":128,"messages":[{"role":"user","content":"lookup"},{"role":"assistant","content":[{"type":"tool_use","id":"call_1","name":"lookup","input":{"id":1}}]},{"role":"user","content":[{"type":"tool_result","tool_use_id":"call_1","content":"found"}]}]}""")]
    [InlineData(Web, """{"model":"deepseek/model","messages":[{"id":"u","role":"user","parts":[{"type":"text","text":"lookup"}]},{"id":"a","role":"assistant","parts":[{"type":"dynamic-tool","toolName":"lookup","toolCallId":"call_1","state":"output-available","input":{"id":1},"output":"found"}]}]}""")]
    public async Task Translated_history_replays_standard_tool_results(string route, string body)
    {
        await using var host = await FixtureHost.StartAsync(request => FixtureHost.Reply(request with { Path = Chat }), compatibleChat: true);
        using var response = await host.PostAsync(route, body, ("X-DeepSeek-Key", "fixture-key"));
        var wire = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, wire);
        Assert.Contains("hello", wire);
        var messages = Assert.IsType<JsonArray>(Assert.Single(host.Requests).Body!["messages"]);
        Assert.Equal("call_1", messages[1]!["tool_calls"]![0]!["id"]!.GetValue<string>());
        Assert.Equal("{\"id\":1}", messages[1]!["tool_calls"]![0]!["function"]!["arguments"]!.GetValue<string>());
        Assert.Equal("tool", messages[2]!["role"]!.GetValue<string>());
        Assert.Equal("call_1", messages[2]!["tool_call_id"]!.GetValue<string>());
        Assert.Equal("found", messages[2]!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task Provider_specific_option_is_rejected_before_using_a_translation()
    {
        await using var host = await FixtureHost.StartAsync(compatibleChat: true);
        using var response = await host.PostAsync(Responses,
            """{"model":"cerebras/model","input":"hello","tools":[{"type":"web_search"}]}""", ("X-Cerebras-Key", "fixture-key"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(host.Requests);
    }

    private static JsonObject Body(string route, string model, bool stream)
    {
        var body = JsonNode.Parse(route switch
        {
            Responses => """{"input":"test"}""",
            Messages => """{"max_tokens":128,"messages":[{"role":"user","content":"test"}]}""",
            Web => """{"messages":[{"id":"u","role":"user","parts":[{"type":"text","text":"test"}]}]}""",
            _ => """{"messages":[{"role":"user","content":"test"}]}"""
        })!.AsObject();
        body["model"] = model;
        if (route != Web) body["stream"] = stream;
        return body;
    }

    private static HttpResponseMessage ToolStream() => FixtureHost.Sse(
        """{"id":"chat_1","model":"vendor/Model","choices":[{"index":0,"delta":{"role":"assistant","content":"Checking"}}]}""",
        """{"id":"chat_1","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_1","type":"function","function":{"name":"lookup","arguments":"{\"city\":\""}},{"index":1,"id":"call_2","type":"function","function":{"name":"lookup","arguments":"{\"city\":\""}}]}}]}""",
        """{"id":"chat_1","choices":[{"index":0,"delta":{"tool_calls":[{"index":1,"function":{"arguments":"Rome\"}"}},{"index":0,"function":{"arguments":"Paris\"}"}}]},"finish_reason":"tool_calls"}]}""",
        """{"id":"chat_1","choices":[],"x_groq":{"usage":{"prompt_tokens":11,"completion_tokens":3,"total_tokens":14}}}""", "[DONE]");
}
