using System.Text.Json.Nodes;
using Xunit;

namespace Streaik.Gateway.Tests;

public sealed class OpenAIMultiAgentTests
{
    private const string Model = "openai/gpt-6-luna";

    private static JsonArray Output() => JsonNode.Parse("""
        [
          {"id":"mac_1","type":"multi_agent_call","action":"spawn_agent","call_id":"spawn","arguments":"{\"task_name\":\"research\",\"message\":\"opaque-task\"}","agent":{"agent_name":"/root"}},
          {"id":"msg_user","type":"message","role":"user","status":"completed","content":[{"type":"input_text","text":"user context"}],"agent":{"agent_name":"/root/research"}},
          {"id":"amsg_1","type":"agent_message","author":"/root","recipient":"/root/research","agent":{"agent_name":"/root/research"},"content":[{"type":"encrypted_content","encrypted_content":"opaque-agent-message"}]},
          {"id":"maco_1","type":"multi_agent_call_output","action":"spawn_agent","call_id":"spawn","agent":{"agent_name":"/root"},"output":[{"type":"output_text","text":"{\"task_name\":\"/root/research\"}","annotations":[],"logprobs":[]}]},
          {"id":"rs_1","type":"reasoning","agent":{"agent_name":"/root/research"},"summary":[],"encrypted_content":"opaque-reasoning"},
          {"id":"ws_1","type":"web_search_call","agent":{"agent_name":"/root/research"},"status":"completed","action":{"type":"search","query":"seed bank","queries":["botanical gardens","seed bank"]}},
          {"id":"msg_child","type":"message","role":"assistant","phase":"final_answer","status":"completed","agent":{"agent_name":"/root/research"},"content":[{"type":"output_text","text":"Child findings","annotations":[],"logprobs":[]}]},
          {"id":"fc_1","type":"function_call","call_id":"call_lookup","name":"lookup","arguments":"{\"value\":1}","status":"completed","agent":{"agent_name":"/root/research"}},
          {"id":"msg_root","type":"message","role":"assistant","phase":"final_answer","status":"completed","agent":{"agent_name":"/root"},"content":[{"type":"output_text","text":"Root conclusion","annotations":[],"logprobs":[]}]}
        ]
        """)!.AsArray();

    [Fact]
    public async Task Vercel_round_trips_a_saved_native_response_and_keeps_the_root_transcript_separate()
    {
        var output = Output();
        await using var host = await FixtureHost.StartAsync(_ => FixtureHost.Sse(
            """{"type":"response.created","response":{"id":"resp_agents","model":"gpt-6-luna","created_at":1,"status":"in_progress","output":[]}}""",
            $$"""{"type":"response.output_item.added","output_index":0,"item":{{output[0]!.ToJsonString()}}}""",
            """{"type":"response.output_item.added","output_index":6,"item":{"type":"message","id":"msg_child","role":"assistant","content":[],"agent":{"agent_name":"/root/research"}}}""",
            """{"type":"response.output_text.delta","output_index":6,"item_id":"msg_child","content_index":0,"delta":"Child findings"}""",
            """{"type":"response.output_text.delta","output_index":8,"item_id":"msg_root","content_index":0,"delta":"Root conclusion"}""",
            $$"""{"type":"response.completed","response":{"id":"resp_agents","model":"gpt-6-luna","created_at":1,"status":"completed","output":{{output.ToJsonString()}} } }"""));
        var request = JsonNode.Parse("""
            {"model":"openai/gpt-6-luna","providerMetadata":{"openai":{"multi_agent":{"enabled":true}}},
             "tools":[{"name":"lookup","inputSchema":{"type":"object"}}],
             "messages":[{"id":"u","role":"user","parts":[{"type":"text","text":"Research the seed bank"}]}]}
            """)!.AsObject();
        async Task<string> Post(string route, JsonNode body)
        {
            using var response = await host.PostAsync(route, body.ToJsonString(), ("X-OpenAI-Key", "fixture-key"), ("OpenAI-Beta", "responses_multi_agent=v1"));
            return await GatewayTests.Success(response);
        }

        var wire = await Post("/api/chat", request);
        var parts = wire.Split('\n').Where(line => line.StartsWith("data: {", StringComparison.Ordinal))
            .Select(line => JsonNode.Parse(line[6..])!).ToArray();
        Assert.Equal("Root conclusion", string.Concat(parts.Where(p => (string?)p["type"] == "text-delta").Select(p => (string?)p["delta"])));
        Assert.DoesNotContain(parts, part => ((string?)part["type"])?.StartsWith("data-", StringComparison.Ordinal) == true);
        var toolInputs = parts.Where(p => (string?)p["type"] == "tool-input-available").ToArray();
        var hosted = toolInputs.Where(p => (bool?)p["providerExecuted"] == true).ToArray();
        Assert.Equal(new[] { "spawn_agent", "web_search" }, hosted.Select(p => (string?)p["toolName"]));
        Assert.Equal(new[] { "spawn", "ws_1" }, hosted.Select(p => (string?)p["toolCallId"]));
        Assert.Equal("/root/research", (string?)hosted[1]["input"]!["agent"]);
        Assert.DoesNotContain("opaque-task", hosted[0]["input"]!.ToJsonString());
        Assert.Single(toolInputs.Where(p => (bool?)p["providerExecuted"] != true));
        var results = parts.Where(p => (string?)p["type"] == "tool-output-available").ToArray();
        Assert.Equal("/root/research", (string?)results.First(p => (string?)p["toolCallId"] == "spawn")["output"]!["task_name"]);
        var saved = Assert.Single(results.Where(p => p["providerMetadata"]?["openai"]?["responsesReplay"] is not null));
        Assert.True(JsonNode.DeepEquals(output, saved["providerMetadata"]!["openai"]!["responsesReplay"]!["items"]));

        // A new request reconstructs everything from the saved UI parts, not gateway memory.
        var assistant = JsonNode.Parse("""
            {"id":"a","role":"assistant","parts":[{"type":"step-start"},{"type":"text","text":"Root conclusion"},
            {"type":"dynamic-tool","toolName":"lookup","toolCallId":"call_lookup","state":"output-available","input":{"value":1},"output":{"checked":true}}]}
            """)!;
        var anchor = Assert.Single(hosted.Where(p => (string?)p["toolCallId"] == (string?)saved["toolCallId"]));
        assistant["parts"]!.AsArray().Add(new JsonObject
        {
            ["type"] = "dynamic-tool", ["providerExecuted"] = true, ["state"] = "output-available",
            ["toolCallId"] = anchor["toolCallId"]!.DeepClone(), ["toolName"] = anchor["toolName"]!.DeepClone(),
            ["input"] = anchor["input"]!.DeepClone(), ["output"] = saved["output"]!.DeepClone(),
            ["callProviderMetadata"] = anchor["providerMetadata"]!.DeepClone(),
            ["resultProviderMetadata"] = saved["providerMetadata"]!.DeepClone()
        });
        request["messages"]!.AsArray().Add(assistant);
        await Post("/api/chat", request);
        var replay = host.Requests.Last().Body!;
        Assert.False((bool)replay["store"]!);
        Assert.Null(replay["previous_response_id"]);
        var input = replay["input"]!.AsArray();
        Assert.Equal(output.Select(item => (string?)item!["id"]), input.Skip(1).Take(output.Count).Select(item => (string?)item!["id"]));
        Assert.Equal("opaque-agent-message", (string?)input[3]!["content"]![0]!["encrypted_content"]);
        Assert.Equal("/root/research", (string?)input[8]!["agent"]!["agent_name"]);
        Assert.Equal(new[] { "function_call", "function_call_output" }, input.Where(p => (string?)p!["call_id"] == "call_lookup").Select(p => (string?)p!["type"]));

        var native = new JsonObject { ["model"] = Model, ["stream"] = true, ["store"] = false, ["input"] = input.DeepClone(),
            ["multi_agent"] = new JsonObject { ["enabled"] = true } };
        var nativeWire = await Post("/v1/responses", native);
        Assert.Contains("opaque-agent-message", nativeWire);
        Assert.Contains("response.completed", nativeWire);
        Assert.Equal("/root/research", (string?)host.Requests.Last().Body!["input"]![8]!["agent"]!["agent_name"]);
    }
}
