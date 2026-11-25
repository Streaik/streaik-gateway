using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Streaik.Vercel.Security;

namespace Streaik.Gateway.Tests;

public sealed class VercelReplayTests
{
    private const string Route = "/api/chat";

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
        initial["clientToolResultApproval"] = true;
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
            JsonNode.Parse("""{"type":"dynamic-tool","toolName":"lookup","toolCallId":"client_1","state":"output-available","input":{"n":5},"output":{"ok":true}}""")) });
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

        if (signing)
        {
            reviewed["input"]!["n"] = 2;
            using var tampered = await fresh.PostAsync(Route, request.ToJsonString(), key);
            Assert.Equal(HttpStatusCode.BadRequest, tampered.StatusCode);
            reviewed["input"]!["n"] = 1;
            ((JsonObject)reviewed["approval"]!).Remove("signature");
            using var unsigned = await fresh.PostAsync(Route, request.ToJsonString(), key);
            Assert.Equal(HttpStatusCode.BadRequest, unsigned.StatusCode);
            Assert.Single(fresh.Requests);
        }
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
        request["clientToolResultApproval"] = true;
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
