using System.Net;
using System.Text.Json.Nodes;

namespace Streaik.Gateway.Tests;

public sealed class ReasoningBridgeTests
{
    private const string Chat = "/v1/chat/completions";
    private const string Messages = "/v1/messages";
    private const string Responses = "/v1/responses";

    public static IEnumerable<object[]> Bridges()
    {
        foreach (var (ingress, provider, upstream) in new[]
        {
            (Chat, "anthropic", Messages), (Responses, "anthropic", Messages),
            (Chat, "bananapeel", Responses), (Messages, "openai", Responses),
            (Messages, "groq", Chat), (Responses, "groq", Chat),
        })
            foreach (var stream in new[] { false, true }) yield return [ingress, provider, upstream, stream];
    }

    [Theory]
    [MemberData(nameof(Bridges))]
    public async Task Portable_effort_reaches_upstream_and_visible_reasoning_stays_separate(string ingress, string provider, string upstream, bool stream)
    {
        await using var host = await FixtureHost.StartAsync(_ => Reply(upstream, stream),
            compatibleChat: provider == "groq", doublewordBananaPeelCreora: provider == "bananapeel");
        var request = GatewayTests.Request(ingress, $"{provider}/test", stream);
        switch (ingress)
        {
            case Chat: request["reasoning_effort"] = "high"; break;
            case Responses: request["reasoning"] = new JsonObject { ["effort"] = "high" }; break;
            case Messages:
                request["thinking"] = new JsonObject { ["type"] = "adaptive" };
                request["output_config"] = new JsonObject { ["effort"] = "high" };
                // Simulate a client replaying a display block from a previous Messages reply.
                request["messages"]!.AsArray().Insert(0, JsonNode.Parse("""
                    {"role":"assistant","content":[{"type":"thinking","thinking":"old-reasoning","signature":"opaque-history"},{"type":"text","text":"prior reply"}]}
                    """));
                break;
        }
        using var response = await host.PostAsync(ingress, request.ToJsonString(), ($"X-{provider}-Key", "fixture"), GatewayTests.AnthropicVersion);
        var reply = await GatewayTests.Success(response);
        var sent = Assert.Single(host.Requests);
        Assert.EndsWith(upstream, sent.Path);
        var body = sent.Body!;
        var effort = upstream switch
        {
            Messages => body["output_config"]?["effort"], Responses => body["reasoning"]?["effort"], _ => body["reasoning_effort"],
        };
        Assert.Equal("high", (string?)effort);
        if (upstream == Messages) Assert.Equal("adaptive", (string?)body["thinking"]?["type"]);
        Assert.DoesNotContain("old-reasoning", sent.RawBody!);
        Assert.DoesNotContain("opaque-history", sent.RawBody!);
        Assert.Contains("visible-reasoning", reply);
        Assert.Contains("visible-answer", reply);
        Assert.Contains(ingress switch
        {
            Messages => stream ? "thinking_delta" : "\"type\":\"thinking\"",
            Responses => stream ? "response.reasoning_summary_text.delta" : "summary_text",
            _ => "reasoning_content",
        }, reply);
        Assert.DoesNotContain("opaque-reasoning", reply);
        Assert.DoesNotContain("\"type\":\"error\"", reply);
    }

    [Fact]
    public async Task Native_only_thinking_is_not_silently_dropped_when_bridging()
    {
        await using var host = await FixtureHost.StartAsync();
        var request = GatewayTests.Request(Messages, "openai/test", false);
        request["thinking"] = new JsonObject { ["type"] = "enabled", ["budget_tokens"] = 1024 };
        request["output_config"] = new JsonObject { ["effort"] = "high" };
        using var response = await host.PostAsync(Messages, request.ToJsonString(), ("X-OpenAI-Key", "fixture"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(host.Requests);
    }

    private static HttpResponseMessage Reply(string upstream, bool stream) => (upstream, stream) switch
    {
        (Messages, true) => FixtureHost.Sse(FixtureHost.MessageStart,
            """{"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":"","signature":""}}""",
            """{"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"visible-reasoning"}}""",
            """{"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"opaque-reasoning"}}""",
            """{"type":"content_block_stop","index":0}""",
            """{"type":"content_block_start","index":1,"content_block":{"type":"redacted_thinking","data":"opaque-reasoning"}}""",
            """{"type":"content_block_stop","index":1}""",
            """{"type":"content_block_start","index":2,"content_block":{"type":"text","text":"visible-answer"}}""",
            """{"type":"content_block_stop","index":2}""",
            """{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":3}}""", FixtureHost.MessageStop),
        (Messages, false) => FixtureHost.Json("""
            {"id":"msg_f","type":"message","role":"assistant","model":"test","stop_reason":"end_turn","content":[
              {"type":"thinking","thinking":"visible-reasoning","signature":"opaque-reasoning"},
              {"type":"redacted_thinking","data":"opaque-reasoning"},{"type":"text","text":"visible-answer"}]}
            """),
        (Responses, true) => FixtureHost.Sse(
            """{"type":"response.created","response":{"id":"resp_f","status":"in_progress","model":"test","output":[]}}""",
            """{"type":"response.output_item.added","output_index":0,"item":{"type":"reasoning","id":"rs_1","summary":[]}}""",
            """{"type":"response.reasoning_summary_text.delta","output_index":0,"item_id":"rs_1","summary_index":0,"delta":"visible-reasoning"}""",
            """{"type":"response.reasoning_summary_text.done","output_index":0,"item_id":"rs_1","summary_index":0,"text":"visible-reasoning"}""",
            """{"type":"response.output_item.done","output_index":0,"item":{"type":"reasoning","id":"rs_1","summary":[],"encrypted_content":"opaque-reasoning"}}""",
            """{"type":"response.output_text.delta","output_index":1,"content_index":0,"item_id":"msg_f","delta":"visible-answer"}""",
            """{"type":"response.completed","response":{"id":"resp_f","status":"completed","model":"test","output":[]}}"""),
        (Responses, false) => FixtureHost.Json("""
            {"id":"resp_f","status":"completed","model":"test","output":[
              {"type":"reasoning","id":"rs_1","summary":[{"type":"summary_text","text":"visible-reasoning"}],"encrypted_content":"opaque-reasoning"},
              {"type":"message","id":"msg_f","role":"assistant","content":[{"type":"output_text","text":"visible-answer","annotations":[]}]}]}
            """),
        (Chat, true) => FixtureHost.Sse(
            """{"id":"chatcmpl_f","model":"test","created":1,"choices":[{"index":0,"delta":{"role":"assistant","reasoning_content":"visible-reasoning"}}]}""",
            """{"id":"chatcmpl_f","model":"test","created":1,"choices":[{"index":0,"delta":{"content":"visible-answer"},"finish_reason":"stop"}]}""", "[DONE]"),
        _ => FixtureHost.Json("""
            {"id":"chatcmpl_f","model":"test","created":1,"choices":[{"index":0,"message":{"role":"assistant","content":"visible-answer","reasoning_content":"visible-reasoning"},"finish_reason":"stop"}]}
            """),
    };
}
