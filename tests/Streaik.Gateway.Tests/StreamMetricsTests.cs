using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;

namespace Streaik.Gateway.Tests;

public sealed class StreamMetricsTests
{
    private const string Role = """{"id":"completion","model":"test","choices":[{"index":0,"delta":{"role":"assistant","content":""}}]}""";
    private const string First = """{"id":"completion","model":"test","choices":[{"index":0,"delta":{"content":"hel"}}]}""";
    private const string Last = """{"id":"completion","model":"test","choices":[{"index":0,"delta":{"content":"lo"}}]}""";
    private const string Finish = """{"id":"completion","model":"test","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}""";

    [Fact]
    public async Task Chat_finishes_in_order_and_enriches_a_late_usage_frame_with_reported_zero_cost()
    {
        await using var host = await FixtureHost.StartAsync(_ => FixtureHost.Sse(Role, First, Last, Finish, """
            {"id":"completion","choices":[],"usage":{"prompt_tokens":2,"completion_tokens":6,"total_tokens":8,"cost":"0"},"metadata":{"gateway":{"upstream":"keep"},"extra":true}}
            """, "[DONE]"), configure: Clock, media: true);
        var options = GatewayTests.Request("/v1/chat/completions", "openrouter/vendor/model", true);
        options["stream_options"] = new JsonObject { ["include_usage"] = true };
        using var response = await host.PostAsync("/v1/chat/completions", options.ToJsonString(), ("X-OpenRouter-Key", "fixture"));
        var frames = Frames(await GatewayTests.Success(response));
        Assert.Equal(5, frames.Length);
        Assert.Null(frames[0]["metadata"]);
        Assert.Equal(2, (double)frames[3]["metadata"]!["gateway"]!["ttft"]!);
        Assert.Null(frames[3]["metadata"]!["gateway"]!["tps"]);
        var metadata = frames[4]["metadata"]!;
        Assert.True((bool)metadata["extra"]!);
        Assert.Equal("keep", (string?)metadata["gateway"]!["upstream"]);
        Check(metadata["gateway"]!, 0m);
        Assert.Null(metadata["gateway"]!["estimated"]);
    }

    [Theory]
    [InlineData("/v1/messages")]
    [InlineData("/v1/responses")]
    [InlineData("/api/chat")]
    public async Task Groq_usage_and_catalog_cost_survive_bridging(string route)
    {
        const string model = "groq/openai/gpt-oss-20b";
        await using var host = await FixtureHost.StartAsync(request =>
        {
            Assert.Equal("openai/gpt-oss-20b", (string?)request.Body!["model"]);
            return FixtureHost.Sse(Role, First, Last, Finish, """
                {"id":"completion","choices":[],"x_groq":{"usage":{"prompt_tokens":2,"completion_tokens":6,"total_tokens":8}}}
                """, "[DONE]");
        }, configure: Clock, compatibleChat: true);
        using var response = await host.PostAsync(route, GatewayTests.Request(route, model, true).ToJsonString(), ("X-Groq-Key", "fixture"));
        var gateway = Frames(await GatewayTests.Success(response)).Select(frame => (frame["response"] ?? frame)["metadata"]?["gateway"]
            ?? frame["messageMetadata"]?["providerMetadata"]?["gateway"]).Last(candidate => candidate?["cost"] is not null)!;
        Check(gateway, 0.00000195m);
        Assert.Equal("USD", gateway["currency"]?.GetValue<string>());
        Assert.True(gateway["estimated"]?.GetValue<bool>());
    }

    [Theory]
    [InlineData("/v1/responses")]
    [InlineData("/api/chat")]
    public async Task Response_cost_and_metrics_share_the_existing_terminal_metadata(string route)
    {
        const string result = """{"id":"resp","model":"vendor/model","status":"completed","object":"response","created_at":1700000000,"output":[],"usage":{"input_tokens":2,"output_tokens":6,"total_tokens":8,"cost":0.125}}""";
        await using var host = await FixtureHost.StartAsync(_ => FixtureHost.Sse(
            $$"""{"type":"response.created","sequence_number":0,"response":{{result}}}""",
            """{"type":"response.output_text.delta","sequence_number":1,"item_id":"m","output_index":0,"content_index":0,"delta":"one"}""",
            """{"type":"response.output_text.delta","sequence_number":2,"item_id":"m","output_index":0,"content_index":0,"delta":"two"}""",
            $$"""{"type":"response.completed","sequence_number":3,"response":{{result}}}"""), configure: Clock, media: true);
        using var response = await host.PostAsync(route, GatewayTests.Request(route, "openrouter/vendor/model", true).ToJsonString(), ("X-OpenRouter-Key", "fixture"));
        var finish = Frames(await GatewayTests.Success(response)).Last();
        Check((finish["response"]?["metadata"]?["gateway"] ?? finish["messageMetadata"]?["providerMetadata"]?["gateway"])!, 0.125m);
    }

    [Fact]
    public async Task Control_only_stream_does_not_invent_timing()
    {
        await using var host = await FixtureHost.StartAsync(_ => FixtureHost.Sse(Role, Finish, "[DONE]"), configure: Clock);
        const string route = "/v1/chat/completions";
        using var response = await host.PostAsync(route, GatewayTests.Request(route, "openai/test", true).ToJsonString(), ("X-OpenAI-Key", "fixture"));
        Assert.All(Frames(await GatewayTests.Success(response)), frame => Assert.Null(frame["metadata"]));
    }

    [Fact]
    public async Task Bridged_chat_finish_is_measured_without_adding_an_unrequested_usage_frame()
    {
        const string route = "/v1/chat/completions";
        await using var host = await FixtureHost.StartAsync(_ => FixtureHost.Sse(FixtureHost.MessageStart,
            """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}""",
            """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"hel"}}""",
            """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"lo"}}""",
            """{"type":"content_block_stop","index":0}""", FixtureHost.MessageDelta, FixtureHost.MessageStop), configure: Clock);
        using var response = await host.PostAsync(route, GatewayTests.Request(route, "anthropic/claude-mythos-5-1", true).ToJsonString(), ("X-Anthropic-Key", "fixture"));
        var frames = Frames(await GatewayTests.Success(response));
        Assert.Equal(4, frames.Length);
        Assert.All(frames, frame => Assert.Null(frame["usage"]));
        var gateway = frames[^1]["metadata"]!["gateway"]!;
        Assert.Equal(2d, (double)gateway["ttft"]!);
        Assert.Equal(1.5d, (double)gateway["tps"]!);
        Assert.Equal(0.0002105m, (decimal)gateway["cost"]!);
    }

    private static void Check(JsonNode gateway, decimal cost)
    {
        Assert.Equal(2d, (double)gateway["ttft"]!);
        Assert.Equal(3d, (double)gateway["tps"]!);
        Assert.Equal(cost, (decimal)gateway["cost"]!);
    }

    private static void Clock(IServiceCollection services) => services.AddSingleton<TimeProvider, StepClock>();

    private sealed class StepClock : TimeProvider
    {
        private long tick;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Add(ref tick, 2 * TimeSpan.TicksPerSecond);
    }

    private static JsonNode[] Frames(string sse) => sse.Split('\n').Where(line => line.StartsWith("data: {", StringComparison.Ordinal))
        .Select(line => JsonNode.Parse(line[6..])!).ToArray();
}
