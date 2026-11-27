using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Streaik.Gateway.Tests;

public sealed class AnthropicOptionsTests
{
    private const string Chat = "/api/chat";
    private static readonly (string, string) Key = ("X-Anthropic-Key", "fixture-key");

    [Theory]
    [InlineData("/v1/messages", "max_tokens", false)]
    [InlineData("/v1/messages", "max_tokens", true)]
    [InlineData("/v1/responses", "max_output_tokens", false)]
    [InlineData("/v1/responses", "max_output_tokens", true)]
    [InlineData("/v1/chat/completions", "max_completion_tokens", false)]
    [InlineData("/v1/chat/completions", "max_completion_tokens", true)]
    [InlineData(Chat, "maxOutputTokens", true)]
    public async Task Missing_required_inputs_are_rejected_without_upstream_requests(string route, string tokenKey, bool stream)
    {
        await using var host = await FixtureHost.StartAsync();
        var request = GatewayTests.Request(route, "anthropic/test", stream);
        using var noVersion = await host.PostAsync(route, request.ToJsonString(), Key);
        Assert.Equal(HttpStatusCode.BadRequest, noVersion.StatusCode);
        Assert.Contains("anthropic-version", await noVersion.Content.ReadAsStringAsync());

        request.Remove(tokenKey);
        using var noTokens = await host.PostAsync(route, request.ToJsonString(), Key, GatewayTests.AnthropicVersion);
        Assert.Equal(HttpStatusCode.BadRequest, noTokens.StatusCode);
        request[tokenKey] = 0;
        using var zero = await host.PostAsync(route, request.ToJsonString(), Key, GatewayTests.AnthropicVersion);
        Assert.Equal(HttpStatusCode.BadRequest, zero.StatusCode);
        Assert.Contains("max_tokens", await zero.Content.ReadAsStringAsync());
        Assert.Empty(host.Requests);
    }

    [Fact]
    public async Task Selected_native_options_are_forwarded_verbatim_except_for_routing_and_tool_policy()
    {
        await using var host = await FixtureHost.StartAsync();
        var request = GatewayTests.Request(Chat, "anthropic/test", true);
        request.Remove("maxOutputTokens");
        var native = JsonNode.Parse("""
            {"max_tokens":128000,"cache_control":{"type":"ephemeral","ttl":"1h"},
             "output_config":{"task_budget":{"type":"tokens","total":5000},"format":null},
             "thinking":{"type":"adaptive","block_binding":{"prefix_mismatch_behavior":"drop_block"}},
             "container":{"id":"container_1","skills":[{"type":"anthropic","skill_id":"pdf","version":"latest"}]},
             "context_management":{"edits":[{"type":"clear_tool_uses_20250919","clear_tool_inputs":false}]},
             "budget":{"type":"limit","max_list_cost":{"amount":"125","currency":"USD"}},
             "future_option":{"values":[null,0,false,{}]}, "model":"ignored", "stream":false, "messages":[], "metadata":null,
             "anthropic-version":"not-a-header", "headers":{"x-api-key":"not-a-key"},
             "tools":[{"name":"web_fetch","type":"web_fetch_20260318","max_uses":2,"future_field":null}]}
            """)!.AsObject();
        request["tools"] = JsonNode.Parse("""[{"name":"client_tool","inputSchema":{"type":"object"}}]""");
        request["reasoning"] = "medium";
        request["providerMetadata"] = new JsonObject { ["anthropic"] = native, ["openai"] = new JsonObject { ["unselected"] = true } };
        using var response = await host.PostAsync(Chat, request.ToJsonString(), Key, GatewayTests.AnthropicVersion, ("anthropic-beta", "example-beta"));
        await GatewayTests.Success(response);
        var sent = Assert.Single(host.Requests);
        Assert.Equal("2026-10-21", sent.Headers["anthropic-version"]);
        Assert.Equal("example-beta", sent.Headers["anthropic-beta"]);
        using var wire = JsonDocument.Parse(sent.RawBody!);
        Assert.All(wire.RootElement.EnumerateObject().GroupBy(property => property.Name, StringComparer.Ordinal), group => Assert.Single(group));
        Assert.Equal(JsonValueKind.Null, wire.RootElement.GetProperty("metadata").ValueKind);
        var body = sent.Body!.AsObject();
        foreach (var field in new[] { "max_tokens", "cache_control", "output_config", "thinking", "container", "context_management", "budget", "future_option" })
            Assert.True(JsonNode.DeepEquals(native[field], body[field]), field);
        Assert.Equal("test", (string?)body["model"]);
        Assert.True((bool?)body["stream"]);
        Assert.Single(body["messages"]!.AsArray());
        Assert.Equal("client_tool", (string?)body["tools"]![0]!["name"]);
        Assert.True(JsonNode.DeepEquals(native["tools"]![0], body["tools"]![1]));
        foreach (var field in new[] { "providerMetadata", "anthropic", "openai", "headers", "anthropic-version" })
            Assert.False(body.ContainsKey(field));

        request["toolChoice"] = "none";
        using var noTools = await host.PostAsync(Chat, request.ToJsonString(), Key, GatewayTests.AnthropicVersion);
        await GatewayTests.Success(noTools);
        Assert.Null(host.Requests.Last().Body!["tools"]);
    }
}
