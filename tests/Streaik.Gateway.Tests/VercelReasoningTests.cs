using System.Net;
using System.Text.Json.Nodes;

namespace Streaik.Gateway.Tests;

public sealed class VercelReasoningTests
{
    private const string Route = "/api/chat";

    [Theory]
    [InlineData("openai", "low", """{"effort":"low"}""")]
    [InlineData("anthropic", "medium", """{"effort":"medium"}""")]
    [InlineData("cerebras", "high", "\"high\"")]
    [InlineData("openai", null, null)]
    [InlineData("anthropic", null, null)]
    [InlineData("cerebras", null, null)]
    public async Task Reasoning_is_optional_and_maps_to_each_native_protocol(string provider, string? effort, string? expected)
    {
        await using var host = await FixtureHost.StartAsync(FixtureHost.Reply, compatibleChat: true);
        var request = GatewayTests.Request(Route, $"{provider}/test", true);
        request["reasoning"] = effort;
        request["temperature"] = 0.25;
        request["topP"] = 0.5;
        using var response = await host.PostAsync(Route, request.ToJsonString(), ($"X-{provider}-Key", "fixture-key"));
        await GatewayTests.Success(response);

        var upstream = Assert.Single(host.Requests).Body!.AsObject();
        var field = provider switch { "openai" => "reasoning", "anthropic" => "output_config", _ => "reasoning_effort" };
        if (expected is null) Assert.False(upstream.ContainsKey(field));
        else Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), upstream[field]));
        if (provider == "anthropic") Assert.Equal(effort is null ? null : "adaptive", (string?)upstream["thinking"]?["type"]);
        Assert.False(upstream.ContainsKey("temperature"));
        Assert.False(upstream.ContainsKey("top_p"));
    }

    [Theory]
    [InlineData("openai", """{"reasoning":{"effort":"minimal","summary":"auto"}}""", "reasoning", """{"effort":"minimal","summary":"auto"}""")]
    [InlineData("openai", """{"reasoning":{"summary":"auto"}}""", "reasoning", """{"effort":"high","summary":"auto"}""")]
    [InlineData("openai", """{"reasoning":null}""", "reasoning", null)]
    [InlineData("anthropic", """{"output_config":{"effort":"max"}}""", "output_config", """{"effort":"max"}""")]
    [InlineData("anthropic", """{"thinking":{"type":"disabled"}}""", "thinking", """{"type":"disabled"}""")]
    [InlineData("cerebras", """{"reasoning_effort":"minimal"}""", "reasoning_effort", "\"minimal\"")]
    public async Task Reasoning_respects_native_options(string provider, string options, string field, string? expected)
    {
        await using var host = await FixtureHost.StartAsync(FixtureHost.Reply, compatibleChat: true);
        var request = GatewayTests.Request(Route, $"{provider}/test", true);
        request["reasoning"] = "high";
        request["providerMetadata"] = new JsonObject { [provider] = JsonNode.Parse(options) };
        using var response = await host.PostAsync(Route, request.ToJsonString(), ($"X-{provider}-Key", "fixture-key"));
        await GatewayTests.Success(response);

        var upstream = Assert.Single(host.Requests).Body!;
        Assert.True(JsonNode.DeepEquals(expected is null ? null : JsonNode.Parse(expected), upstream[field]));
        if (field == "thinking") Assert.Null(upstream["output_config"]);
    }

    [Theory]
    [InlineData("\"none\"")]
    [InlineData("\"High\"")]
    [InlineData("\"\"")]
    [InlineData("3")]
    public async Task Invalid_reasoning_is_rejected_before_forwarding(string value)
    {
        await using var host = await FixtureHost.StartAsync();
        var request = GatewayTests.Request(Route, "openai/test", true);
        request["reasoning"] = JsonNode.Parse(value);
        using var response = await host.PostAsync(Route, request.ToJsonString(), ("X-OpenAI-Key", "fixture-key"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(host.Requests);
    }
}
