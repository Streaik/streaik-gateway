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
            ? FixtureHost.Json(provider == "together" ? """[{"id":"vendor/Model","created_at":1700000000,"type":"chat"},{"id":"embedding","type":"embedding"}]"""
                : """{"data":[{"id":"vendor/Model","created":1700000000}]}""")
            : FixtureHost.Reply(request with { Path = Chat }), compatibleChat: true);
        var key = ($"X-{provider}-Key", "provider-key");
        var model = $"{provider}/vendor/Model";
        using var list = await host.GetAsync("/v1/models", key);
        Assert.True(list.IsSuccessStatusCode);
        var models = JsonNode.Parse(await list.Content.ReadAsStringAsync())!["data"]!.AsArray();
        var chat = Assert.Single(models, item => (string?)item?["id"] == model)!;
        Assert.Equal("language", (string?)chat["type"]);
        Assert.Equal(1700000000L, (long)chat["created"]!);
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
    [InlineData("cerebras", "gpt-oss-120b", """{"reasoning_effort":"high","seed":0,"parallel_tool_calls":false}""")]
    [InlineData("together", "openai/gpt-oss-120b", """{"reasoning_effort":"medium"}""")]
    public async Task Vercel_native_options_override_generation_not_the_chat_envelope(string provider, string model, string fields)
    {
        await using var host = await FixtureHost.StartAsync(FixtureHost.Reply, compatibleChat: true);
        var body = Body(Web, $"{provider}/{model}", true);
        body["reasoning"] = "low";
        var options = JsonNode.Parse(fields)!.AsObject();
        options["temperature"] = 0.25;
        options["future_option"] = JsonNode.Parse("""{"values":[0,false,null,{"native_field":"unchanged"}]}""");
        body["providerMetadata"] = new JsonObject
        {
            ["openai"] = new JsonObject { ["not_for_this_provider"] = true },
            [provider] = options.DeepClone()
        };
        var supplied = (JsonObject)body["providerMetadata"]![provider]!;
        supplied["MODEL"] = "not-the-model";
        supplied["messages"] = new JsonArray();
        supplied["stream"] = false;
        supplied["store"] = true;
        supplied["headers"] = new JsonObject { ["Authorization"] = "not-the-key" };

        using var response = await host.PostAsync(Web, body.ToJsonString(), ($"X-{provider}-Key", "fixture-key"));
        Assert.Contains("hello", await GatewayTests.Success(response));
        var sent = Assert.Single(host.Requests);
        Assert.Equal(Chat, sent.Path);
        Assert.Equal(model, (string?)sent.Body!["model"]);
        Assert.True(sent.Stream);
        Assert.Contains("test", sent.Body["messages"]!.ToJsonString());
        Assert.Equal("Bearer fixture-key", sent.Headers["Authorization"]);
        foreach (var (key, expected) in options)
            Assert.True(JsonNode.DeepEquals(expected, sent.Body[key]), $"Unexpected native field {key}: {sent.Body[key]}");
        Assert.All(new[] { "MODEL", "store", "headers", "not_for_this_provider" }, key => Assert.False(sent.Body.AsObject().ContainsKey(key)));
    }

    [Fact]
    public async Task Vercel_responses_preserves_native_options_and_combines_native_and_client_tools()
    {
        await using var host = await FixtureHost.StartAsync();
        var body = Body(Web, "openai/gpt-5", true);
        body["maxOutputTokens"] = 16;
        body["tools"] = JsonNode.Parse("""[{"name":"lookup","inputSchema":{"type":"object"}}]""");
        var options = JsonNode.Parse("""
            {"reasoning":{"effort":"xhigh","summary":"auto","context":"all_turns","mode":"pro"},"service_tier":"flex","parallel_tool_calls":false,
             "context_management":[{"type":"compaction","compact_threshold":4096}],"max_output_tokens":32,
             "moderation":{"model":"omni-moderation-latest","policy":{"input":{"mode":"block"}}},
             "prompt_cache_options":{"mode":"implicit","ttl":"30m"},"native_future":{"with_null":null,"enabled":false},
             "multi_agent":{"enabled":true,"max_concurrent_subagents":4},"text":{"verbosity":"high"}}
            """)!.AsObject();
        var supplied = (JsonObject)options.DeepClone();
        var settingsTools = JsonNode.Parse("""
            [{"type":"web_search","search_context_size":"low","filters":{"allowed_domains":["example.org"]},"external_web_access":false},
             {"type":"file_search","vector_store_ids":["vs_fixture"],"max_num_results":4},
             {"type":"image_generation","output_format":"gif","frame_count":9,"frame_duration_ms":200},
             {"type":"shell","allowed_callers":["direct","programmatic"],"environment":{"type":"container_auto","memory_limit":"4g",
                "skills":[{"type":"skill_reference","skill_id":"skill_fixture","version":"2"}]}},
              {"type":"programmatic_tool_calling"},{"type":"tool_search"},{"type":"apply_patch"}]
            """)!.AsArray();
        supplied["tools"] = settingsTools;
        supplied["include"] = JsonNode.Parse("""["web_search_call.action.sources","file_search_call.results"]""");
        supplied["input"] = "not-the-user-input";
        supplied["Store"] = true;
        supplied["previous_response_id"] = "resp_unused";
        body["providerMetadata"] = new JsonObject { ["openai"] = supplied };

        using var response = await host.PostAsync(Web, body.ToJsonString(), ("X-OpenAI-Key", "fixture-key"), ("OpenAI-Beta", "responses_multi_agent=v1"));
        Assert.Contains("hello", await GatewayTests.Success(response));
        var sent = Assert.Single(host.Requests);
        Assert.Equal(Responses, sent.Path);
        Assert.Equal("responses_multi_agent=v1", sent.Headers["OpenAI-Beta"]);
        Assert.True(sent.Stream);
        Assert.False((bool)sent.Body!["store"]!);
        Assert.False(sent.Body.AsObject().ContainsKey("previous_response_id"));
        Assert.Contains("test", sent.Body["input"]!.ToJsonString());
        foreach (var (key, expected) in options)
            Assert.True(JsonNode.DeepEquals(expected, sent.Body[key]), $"Unexpected native field {key}: {sent.Body[key]}");
        Assert.Equal("lookup", (string?)sent.Body["tools"]![0]!["name"]);
        Assert.Equal(settingsTools.Count + 1, sent.Body["tools"]!.AsArray().Count);
        for (var i = 0; i < settingsTools.Count; i++)
            Assert.True(JsonNode.DeepEquals(settingsTools[i], sent.Body["tools"]![i + 1]), $"Unexpected native tool: {sent.Body["tools"]![i + 1]}");
        Assert.True(JsonNode.DeepEquals(supplied["include"], sent.Body["include"]));
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
    [InlineData(Web, """{"model":"deepseek/model","messages":[{"id":"u","role":"user","parts":[{"type":"text","text":"lookup"}]},{"id":"a","role":"assistant","parts":[{"type":"dynamic-tool","toolName":"lookup","toolCallId":"call_1","state":"output-available","input":{"id":1},"output":"found","approval":{"id":"review_1","approved":true}}]}]}""")]
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
