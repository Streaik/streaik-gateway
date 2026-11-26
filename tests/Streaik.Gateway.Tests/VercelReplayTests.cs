using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Streaik.Vercel.Security;

namespace Streaik.Gateway.Tests;

public sealed class VercelReplayTests
{
    private const string Route = "/api/chat";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Hosted_tool_search_is_visible_and_replays_a_paired_native_call_and_output(bool itemEvents)
    {
        const string searchCall = """{"type":"tool_search_call","id":"tsc_1","execution":"server","call_id":null,"status":"completed","arguments":{"paths":["inventory"]}}""";
        const string searchOutput = """{"type":"tool_search_output","id":"tso_1","execution":"server","call_id":null,"status":"completed","tools":[{"type":"function","name":"lookup","defer_loading":true,"parameters":{"type":"object"}}]}""";
        var key = ("X-OpenAI-Key", "fixture-key");
        var request = GatewayTests.Request(Route, "openai/test", true);
        request["tools"] = JsonNode.Parse("""[{"name":"lookup","defer_loading":true,"inputSchema":{"type":"object"}}]""");
        request["providerMetadata"] = JsonNode.Parse("""{"openai":{"tools":[{"type":"tool_search","execution":"server"}]}}""");
        JsonObject saved;
        await using (var first = await FixtureHost.StartAsync(_ => itemEvents ? FixtureHost.Sse(
            $$"""{"type":"response.output_item.done","output_index":0,"item":{{searchCall}}}""",
            $$"""{"type":"response.output_item.done","output_index":1,"item":{{searchOutput}}}""",
            $$$"""{"type":"response.completed","response":{"id":"resp_search","model":"native","status":"completed","output":[{{{searchCall}}},{{{searchOutput}}}]}}""")
            : EndResponse($"{searchCall},{searchOutput}")))
        {
            using var response = await first.PostAsync(Route, request.ToJsonString(), key);
            var wire = await GatewayTests.Success(response);
            var input = GatewayTests.Part(wire, "tool-input-available");
            var output = GatewayTests.Part(wire, "tool-output-available");
            Assert.Equal("tool_search", (string?)input["toolName"]);
            Assert.Equal("tsc_1", (string?)input["toolCallId"]);
            Assert.Equal("tsc_1", (string?)output["toolCallId"]);
            Assert.True((bool?)input["providerExecuted"]);
            Assert.True((bool?)output["providerExecuted"]);
            Assert.DoesNotContain("tool-approval-request", wire);
            saved = new JsonObject { ["type"] = "dynamic-tool", ["toolName"] = "tool_search", ["toolCallId"] = "tsc_1",
                ["providerExecuted"] = true, ["state"] = "output-available", ["input"] = input["input"]!.DeepClone(),
                ["output"] = output["output"]!.DeepClone(), ["callProviderMetadata"] = input["providerMetadata"]!.DeepClone(),
                ["resultProviderMetadata"] = output["providerMetadata"]!.DeepClone() };
            var tools = Assert.Single(first.Requests).Body!["tools"]!.AsArray();
            Assert.Contains(tools, tool => (string?)tool?["type"] == "tool_search" && (string?)tool?["execution"] == "server");
            Assert.Contains(tools, tool => (string?)tool?["name"] == "lookup" && (bool?)tool?["defer_loading"] == true);
        }

        request["messages"]!.AsArray().Add(new JsonObject { ["id"] = "searched", ["role"] = "assistant", ["parts"] = new JsonArray(saved, saved.DeepClone()) });
        request["messages"]!.AsArray().Add(JsonNode.Parse("""{"id":"next","role":"user","parts":[{"type":"text","text":"Continue"}]}"""));
        await using var fresh = await FixtureHost.StartAsync();
        using var continuation = await fresh.PostAsync(Route, request.ToJsonString(), key);
        await GatewayTests.Success(continuation);
        var upstream = Assert.Single(fresh.Requests).Body!;
        Assert.False((bool?)upstream["store"]);
        Assert.Null(upstream["previous_response_id"]);
        var history = upstream["input"]!.AsArray();
        Assert.Equal(new[] { "message", "tool_search_call", "tool_search_output", "message" }, history.Select(item => (string?)item?["type"]));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(searchCall), history[1]));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(searchOutput), history[2]));
    }

    [Fact]
    public async Task Approved_client_tool_search_replays_as_native_items_and_uses_the_request_catalog()
    {
        const string item = """{"type":"tool_search_call","id":"tsc_client","execution":"client","call_id":"call_search","status":"completed","arguments":{"goal":"stock"}}""";
        var key = ("X-OpenAI-Key", "fixture-key");
        var request = GatewayTests.Request(Route, "openai/test", true);
        request["tools"] = JsonNode.Parse("""[{"name":"lookup","defer_loading":true,"inputSchema":{"type":"object"}},{"name":"eager"}]""");
        request["providerMetadata"] = JsonNode.Parse("""{"openai":{"tools":[{"type":"tool_search","execution":"client","description":"Search","parameters":{"type":"object"}}]}}""");
        JsonObject invocation;
        await using (var host = await FixtureHost.StartAsync(_ => EndResponse(item)))
        {
            using var response = await host.PostAsync(Route, request.ToJsonString(), key);
            var wire = await GatewayTests.Success(response);
            var input = GatewayTests.Part(wire, "tool-input-available");
            var approval = GatewayTests.Part(wire, "tool-approval-request");
            Assert.Equal("call_search", (string?)input["toolCallId"]);
            Assert.Equal("call_search", (string?)approval["toolCallId"]);
            Assert.False((bool?)input["providerExecuted"]);
            var tools = Assert.Single(host.Requests).Body!["tools"]!.AsArray();
            Assert.DoesNotContain(tools, tool => (string?)tool?["name"] == "lookup");
            Assert.Contains(tools, tool => (string?)tool?["name"] == "eager");
            invocation = new JsonObject { ["type"] = "dynamic-tool", ["toolName"] = "tool_search", ["toolCallId"] = "call_search",
                ["state"] = "output-available", ["input"] = input["input"]!.DeepClone(), ["callProviderMetadata"] = input["providerMetadata"]!.DeepClone(),
                ["approval"] = new JsonObject { ["id"] = approval["approvalId"]!.DeepClone(), ["approved"] = true },
                ["output"] = JsonNode.Parse("""{"tools":[{"name":"lookup","parameters":{"untrusted":true}},{"name":"eager"},{"name":"unknown"}]}""") };
        }

        request["messages"]!.AsArray().Add(new JsonObject { ["id"] = "searched", ["role"] = "assistant", ["parts"] = new JsonArray(invocation) });
        await using var fresh = await FixtureHost.StartAsync();
        using var continuation = await fresh.PostAsync(Route, request.ToJsonString(), key);
        await GatewayTests.Success(continuation);
        var history = Assert.Single(fresh.Requests).Body!["input"]!.AsArray();
        Assert.Equal(new[] { "message", "tool_search_call", "tool_search_output" }, history.Select(entry => (string?)entry?["type"]));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(item), history[1]));
        var result = history[2]!.AsObject();
        Assert.False(result.ContainsKey("id"));
        Assert.Equal("call_search", (string?)result["call_id"]);
        var loaded = Assert.Single(result["tools"]!.AsArray());
        Assert.Equal("lookup", (string?)loaded?["name"]);
        Assert.Equal("object", (string?)loaded?["parameters"]?["type"]);
        Assert.Null(loaded?["parameters"]?["untrusted"]);
    }

    [Fact]
    public async Task Client_tool_options_namespaces_and_approval_survive_the_Responses_bridge()
    {
        var request = GatewayTests.Request(Route, "openai/test", true);
        request["tools"] = JsonNode.Parse("""[{"name":"ordinary","inputSchema":{"type":"object"},"allowed_callers":["programmatic"],"defer_loading":true}]""");
        request["providerMetadata"] = JsonNode.Parse("""
            {"openai":{"tools":[{"type":"namespace","name":"inventory","description":"Inventory tools",
            "tools":[{"type":"function","name":"lookup","parameters":{"type":"object"},"allowed_callers":["direct"]}]}]}}
            """);
        var key = ("X-OpenAI-Key", "fixture-key");
        JsonNode metadata;
        JsonNode approvalId;
        await using (var host = await FixtureHost.StartAsync(_ => EndResponse("""
            {"type":"function_call","id":"fc_1","call_id":"call_1","name":"lookup","namespace":"inventory","arguments":"{}"}
            """)))
        {
            using var response = await host.PostAsync(Route, request.ToJsonString(), key);
            var wire = await GatewayTests.Success(response);
            var input = GatewayTests.Part(wire, "tool-input-available");
            var approval = GatewayTests.Part(wire, "tool-approval-request");
            Assert.Equal("call_1", (string?)approval["toolCallId"]);
            approvalId = approval["approvalId"]!.DeepClone();
            Assert.Equal("inventory", (string?)input["toolMetadata"]?["namespace"]);
            metadata = input["toolMetadata"]!.DeepClone();
            var tools = Assert.Single(host.Requests).Body!["tools"]!.AsArray();
            Assert.Equal("programmatic", (string?)tools[0]?["allowed_callers"]?[0]);
            Assert.True(tools[0]!["defer_loading"]!.GetValue<bool>());
            Assert.Equal("direct", (string?)tools[1]?["tools"]?[0]?["allowed_callers"]?[0]);
        }

        var invocation = JsonNode.Parse("""
            {"type":"dynamic-tool","toolName":"lookup","toolCallId":"call_1","state":"output-available","input":{},"output":{"ok":true}}
            """)!;
        invocation["toolMetadata"] = metadata;
        invocation["approval"] = new JsonObject { ["id"] = approvalId, ["approved"] = true };
        request["messages"]!.AsArray().Add(new JsonObject { ["id"] = "reply", ["role"] = "assistant", ["parts"] = new JsonArray(invocation) });
        await using var replay = await FixtureHost.StartAsync();
        using var continuation = await replay.PostAsync(Route, request.ToJsonString(), key);
        await GatewayTests.Success(continuation);
        var upstream = Assert.Single(replay.Requests).Body!["input"]!.AsArray();
        var call = Assert.Single(upstream, item => (string?)item?["type"] == "function_call");
        Assert.Equal("inventory", (string?)call?["namespace"]);
        Assert.Equal("call_1", (string?)call?["call_id"]);
        Assert.Contains(upstream, item => (string?)item?["type"] == "function_call_output" && (string?)item?["call_id"] == "call_1");
    }

    [Theory]
    [InlineData(null, """["reasoning.encrypted_content"]""", true)]
    [InlineData("""["file_search_call.results","reasoning.encrypted_content"]""", """["file_search_call.results","reasoning.encrypted_content"]""", true)]
    [InlineData("[]", "[]", false)]
    [InlineData("""["file_search_call.results"]""", """["file_search_call.results"]""", false)]
    public async Task OpenAI_encrypted_reasoning_include_controls_stream_metadata_and_history(string? requested, string expected, bool enabled)
    {
        var request = GatewayTests.Request(Route, "openai/test", true);
        if (requested is not null)
            request["providerMetadata"] = new JsonObject { ["openai"] = new JsonObject { ["include"] = JsonNode.Parse(requested) } };

        request["messages"]!.AsArray().Add(JsonNode.Parse("""
            {"id":"old_reply","role":"assistant","parts":[{"type":"reasoning","text":"Previous summary",
              "providerMetadata":{"openai":{"nativeItem":{"type":"reasoning","id":"rs_old","summary":[],"encrypted_content":"old_opaque"}}}},
              {"type":"text","text":"Previous reply"}]}
            """));
        request["messages"]!.AsArray().Add(JsonNode.Parse("""{"id":"next","role":"user","parts":[{"type":"text","text":"Continue"}]}"""));

        // Even if the provider returns encrypted content unasked, don't retain it while opted out.
        await using var host = await FixtureHost.StartAsync(_ => EndResponse("""
            {"type":"reasoning","id":"rs_new","summary":[{"type":"summary_text","text":"Visible summary"}],"encrypted_content":"new_opaque"}
            """));
        using var response = await host.PostAsync(Route, request.ToJsonString(), ("X-OpenAI-Key", "fixture-key"));
        var wire = await GatewayTests.Success(response);
        Assert.Equal("Visible summary", (string?)GatewayTests.Part(wire, "reasoning-delta")["delta"]);
        var reasoningEnd = GatewayTests.Part(wire, "reasoning-end");
        Assert.Equal(enabled ? "new_opaque" : null, (string?)reasoningEnd["providerMetadata"]?["openai"]?["nativeItem"]?["encrypted_content"]);
        if (!enabled) Assert.DoesNotContain("new_opaque", wire);

        var upstream = Assert.Single(host.Requests).Body!;
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), upstream["include"]));
        Assert.Equal(enabled, upstream["input"]!.AsArray().Any(item => (string?)item?["encrypted_content"] == "old_opaque"));
    }

    [Theory]
    [InlineData("anthropic", "server_tool_use", "srv_1")]
    [InlineData("openai", "file_search_call", "fs_1")]
    public async Task Provider_executed_handles_survive_fresh_host_without_becoming_client_calls(string provider, string type, string id)
    {
        var key = ($"X-{provider}-Key", "fixture-key");
        var initial = GatewayTests.Request(Route, $"{provider}/test", true);
        initial["tools"] = JsonNode.Parse("""[{"name":"lookup"}]""");
        JsonNode providerPart;
        await using (var first = await FixtureHost.StartAsync(_ => ToolStream(provider)))
        {
            using var response = await first.PostAsync(Route, initial.ToJsonString(), key);
            var sse = await GatewayTests.Success(response);
            var input = GatewayTests.Part(sse, "tool-input-available");
            var output = GatewayTests.Part(sse, "tool-output-available");
            Assert.Equal(id, input["toolCallId"]!.GetValue<string>());
            Assert.Equal(id, output["toolCallId"]!.GetValue<string>());
            Assert.True(input["providerExecuted"]!.GetValue<bool>());
            Assert.True(output["providerExecuted"]!.GetValue<bool>());
            Assert.DoesNotContain("tool-approval-request", sse);
            providerPart = new JsonObject { ["type"] = "dynamic-tool", ["toolCallId"] = id,
                ["toolName"] = input["toolName"]!.DeepClone(), ["providerExecuted"] = true, ["state"] = "output-available",
                ["input"] = input["input"]!.DeepClone(), ["output"] = output["output"]!.DeepClone(),
                ["callProviderMetadata"] = input["providerMetadata"]!.DeepClone(), ["resultProviderMetadata"] = output["providerMetadata"]!.DeepClone() };
        }
        initial["messages"]!.AsArray().Add(new JsonObject { ["id"] = "assistant_2", ["role"] = "assistant", ["parts"] = new JsonArray(providerPart,
            JsonNode.Parse("""{"type":"dynamic-tool","toolName":"lookup","toolCallId":"client_1","state":"output-available","input":{"n":5},"output":{"ok":true},"approval":{"id":"review_1","approved":true}}""")) });
        initial["messages"]!.AsArray().Add(JsonNode.Parse("""{"id":"user_2","role":"user","parts":[{"type":"text","text":"next"}]}"""));

        await using var fresh = await FixtureHost.StartAsync();
        using var replay = await fresh.PostAsync(Route, initial.ToJsonString(), key);
        await GatewayTests.Success(replay);
        var actual = Assert.Single(fresh.Requests).Body!;
        var blocks = provider == "openai" ? actual["input"]!.AsArray().OfType<JsonObject>().ToList()
            : actual["messages"]!.AsArray().SelectMany(message => message!["content"]!.AsArray()).OfType<JsonObject>().ToList();
        Assert.Contains(blocks, block => block["type"]?.GetValue<string>() == type && block["id"]?.GetValue<string>() == id);
        Assert.Contains(blocks, block => (block["id"] ?? block["call_id"])?.GetValue<string>() == "client_1");
        Assert.DoesNotContain(blocks, block => block["type"]?.GetValue<string>() is "tool_use" or "function_call"
            && (block["id"] ?? block["call_id"])?.GetValue<string>() == id);

        var other = provider == "openai" ? "anthropic" : "openai";
        initial["model"] = $"{other}/test";
        using var switched = await fresh.PostAsync(Route, initial.ToJsonString(), ($"X-{other}-Key", "other-key"));
        await GatewayTests.Success(switched);
        Assert.DoesNotContain(id, fresh.Requests.Last().Body!.ToJsonString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Provider_execution_consent_is_not_exposed_or_granted_by_api_chat(bool signing)
    {
        const string secret = "integration-test-only-approval-secret";
        var key = ("X-OpenAI-Key", "fixture-key");
        var initial = GatewayTests.Request(Route, "openai/test", true);
        await using var host = await FixtureHost.StartAsync(_ => EndResponse("""
            {"type":"mcp_approval_request","id":"mcpr_1","server_label":"fixture","name":"lookup","arguments":"{\"n\":1}"}
            """), Signing(signing, secret));
        using var response = await host.PostAsync(Route, initial.ToJsonString(), key);
        var sse = await GatewayTests.Success(response);
        Assert.DoesNotContain("tool-approval-request", sse);
        Assert.DoesNotContain("tool-input-available", sse);
        Assert.Contains("provider tool requires execution approval", GatewayTests.Part(sse, "error")["errorText"]!.GetValue<string>());

        initial["messages"]!.AsArray().Add(JsonNode.Parse("""
            {"role":"assistant","id":"reply","parts":[{"type":"dynamic-tool","toolCallId":"mcpr_1","toolName":"lookup",
              "state":"approval-responded","providerExecuted":true,"input":{"n":1},"approval":{"id":"mcpr_1","approved":true}}]}
            """));
        using var replay = await host.PostAsync(Route, initial.ToJsonString(), key);
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        Assert.Single(host.Requests);
    }

    [Theory]
    [InlineData("openai", true, true)]
    [InlineData("openai", false, true)]
    [InlineData("openai", true, false)]
    [InlineData("openai", false, false)]
    [InlineData("anthropic", true, true)]
    [InlineData("anthropic", false, true)]
    [InlineData("anthropic", true, false)]
    [InlineData("anthropic", false, false)]
    public async Task Client_result_review_only_releases_approved_output_with_or_without_signing(string provider, bool approved, bool signing)
    {
        const string secret = "integration-test-only-client-result-secret";
        const string output = "fixture-private-tool-result";
        var key = ($"X-{provider}-Key", "fixture-key");
        var request = ReviewRequest(provider);
        JsonObject reviewed;
        await using (var first = await FixtureHost.StartAsync(_ => ClientToolStream(provider), Signing(signing, secret)))
        {
            using var response = await first.PostAsync(Route, request.ToJsonString(), key);
            var stream = await GatewayTests.Success(response);
            var call = GatewayTests.Part(stream, "tool-input-available");
            var approval = GatewayTests.Part(stream, "tool-approval-request");
            Assert.Equal(call["toolCallId"]!.GetValue<string>(), approval["toolCallId"]!.GetValue<string>());
            Assert.False(call["providerExecuted"]!.GetValue<bool>());
            var decision = new JsonObject { ["id"] = approval["approvalId"]!.DeepClone(), ["approved"] = approved };
            if (signing)
            {
                Assert.StartsWith("v1.", approval["signature"]!.GetValue<string>());
                decision["signature"] = approval["signature"]!.DeepClone();
            }
            else Assert.Null(approval["signature"]);
            Assert.DoesNotContain("tool-output-available", stream);
            reviewed = new JsonObject { ["type"] = "dynamic-tool", ["toolCallId"] = call["toolCallId"]!.DeepClone(),
                ["toolName"] = call["toolName"]!.DeepClone(), ["input"] = call["input"]!.DeepClone(), ["providerExecuted"] = false,
                ["state"] = approved ? "output-available" : "approval-responded", ["output"] = output,
                ["approval"] = decision };
        }
        request["messages"]!.AsArray().Add(new JsonObject { ["role"] = "assistant", ["id"] = "tool-review", ["parts"] = new JsonArray(reviewed) });
        await using var fresh = await FixtureHost.StartAsync(configure: Signing(signing, secret));
        using var replay = await fresh.PostAsync(Route, request.ToJsonString(), key);
        await GatewayTests.Success(replay);
        var upstream = Assert.Single(fresh.Requests).Body!.ToJsonString();
        Assert.Contains(provider == "openai" ? "function_call_output" : "tool_result", upstream);
        if (approved) Assert.Contains(output, upstream);
        else
        {
            Assert.DoesNotContain(output, upstream);
            Assert.Contains("The user declined to share this tool result.", upstream);
        }

        var decisionForReplay = reviewed["approval"]!.DeepClone();
        reviewed.Remove("approval");
        using var missing = await fresh.PostAsync(Route, request.ToJsonString(), key);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        reviewed["approval"] = decisionForReplay;
        if (signing)
        {
            reviewed["input"]!["n"] = 2;
            using var tampered = await fresh.PostAsync(Route, request.ToJsonString(), key);
            Assert.Equal(HttpStatusCode.BadRequest, tampered.StatusCode);
            reviewed["input"]!["n"] = 1;
            ((JsonObject)reviewed["approval"]!).Remove("signature");
            using var unsigned = await fresh.PostAsync(Route, request.ToJsonString(), key);
            Assert.Equal(HttpStatusCode.BadRequest, unsigned.StatusCode);
        }
        Assert.Single(fresh.Requests);
    }

    [Fact]
    public async Task A_registered_signer_without_a_subject_does_not_downgrade_to_unsigned()
    {
        await using var host = await FixtureHost.StartAsync(configure: s => s.AddScoped<ToolApprovalSigner>(_ => null!));
        using var reply = await host.PostAsync(Route, ReviewRequest("openai").ToJsonString(), ("X-OpenAI-Key", "fixture-key"));
        Assert.Equal(HttpStatusCode.BadRequest, reply.StatusCode);
        Assert.Empty(host.Requests);
    }

    private static Action<IServiceCollection>? Signing(bool enabled, string secret)
        => enabled ? services => services.AddSingleton(new ToolApprovalSigner(secret)) : null;

    private static JsonObject ReviewRequest(string provider)
    {
        var request = GatewayTests.Request(Route, $"{provider}/test", true);
        request["tools"] = JsonNode.Parse("""[{"name":"lookup","inputSchema":{"type":"object"}}]""");
        return request;
    }

    private static HttpResponseMessage ClientToolStream(string provider) => provider == "openai"
        ? EndResponse("""{"type":"function_call","id":"fc_1","call_id":"client_1","name":"lookup","arguments":"{\"n\":1}"}""")
        : FixtureHost.Sse(FixtureHost.MessageStart,
            """{"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"client_1","name":"lookup","input":{}}}""",
            """{"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{\"n\":1}"}}""",
            """{"type":"content_block_stop","index":0}""", FixtureHost.MessageDelta, FixtureHost.MessageStop);

    private static HttpResponseMessage ToolStream(string provider) => provider == "openai"
        ? EndResponse("""{"type":"file_search_call","id":"fs_1","status":"completed","queries":["test"],"results":[]}""")
        : FixtureHost.Sse(FixtureHost.MessageStart,
            """{"type":"content_block_start","index":0,"content_block":{"type":"server_tool_use","id":"srv_1","name":"web_search","input":{}}}""",
            """{"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{\"query\":\"test\"}"}}""",
            """{"type":"content_block_stop","index":0}""",
            """{"type":"content_block_start","index":1,"content_block":{"type":"web_search_tool_result","tool_use_id":"srv_1","content":[]}}""",
            """{"type":"content_block_stop","index":1}""", FixtureHost.MessageDelta, FixtureHost.MessageStop);

    private static HttpResponseMessage EndResponse(string item) => FixtureHost.Sse(
        $$$"""{"type":"response.completed","response":{"id":"resp_tools","model":"native","status":"completed","created_at":1700000000,"output":[{{{item}}}]}}""");
}
