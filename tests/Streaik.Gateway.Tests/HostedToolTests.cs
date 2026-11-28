using System.IO.Compression;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Streaik.Providers;
using Streaik.Tools;

namespace Streaik.Gateway.Tests;

public sealed class HostedToolTests
{
    private static readonly (string Name, string Value) Key = ("x-openai-key", "fixture-key");
    private static readonly Uri Endpoint = new("https://inventory.example.test/mcp");

    [Theory]
    [InlineData("Development", "127.0.0.1", null, true)]
    [InlineData("Development", "::1", "http://localhost:3000", true)]
    [InlineData("Development", "127.0.0.1", "https://external.example.com", false)]
    [InlineData("Development", "192.168.1.9", null, false)]
    [InlineData("Production", "127.0.0.1", null, false)]
    public async Task Sample_setup_limits_loopback_to_local_development_requests(string environment, string remote, string? origin, bool loopback)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.Services.AddStreaikPublicHostedTools();
        await using var app = builder.Build();
        using var scope = app.Services.CreateScope();
        var accessor = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(remote);
        if (origin is not null) context.Request.Headers.Origin = origin;
        accessor.HttpContext = context;
        try
        {
            var access = scope.ServiceProvider.GetRequiredService<HostedToolAccess>();
            Assert.Equal(loopback, await access.AuthorizeMcp!("local", new Uri("http://localhost:8123/mcp"), ToolCaller.Direct, default) is not null);
            var grant = await access.AuthorizeMcp("public", Endpoint, ToolCaller.Direct, default);
            Assert.NotNull(grant);
            Assert.True(grant.AllowAllRequestHeaders);
            Assert.True(grant.AllowRequestAuthorization);
            Assert.Empty(grant.AllowedRequestHeaders);
            Assert.Null(await access.AuthorizeMcp("private", new Uri("https://192.168.1.9/mcp"), ToolCaller.Direct, default));
        }
        finally { accessor.HttpContext = null; }
    }

    [Fact]
    public async Task Hosted_skill_references_use_the_selected_provider_credential_without_rebinding_bearer_auth()
    {
        var reader = new SkillReader();
        await using var host = await FixtureHost.StartAsync(configure: services =>
        {
            services.AddStreaikPublicHostedTools();
            services.AddKeyedSingleton<ISkillReadProvider>("anthropic", reader);
        });
        const string body = """
            {"model":"openai/test","input":"hello","tools":[{"name":"skills","type":"skills","skills":[
              {"type":"skill_reference","skill_id":"anthropic/fixture","version":"1"}]}]}
            """;
        using var rejected = await host.PostAsync("/v1/responses", body, ("Authorization", "Bearer inference-only"));
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        Assert.Equal(0, reader.Downloads);
        using var accepted = await host.PostAsync("/v1/responses", body, Key, ("X-Anthropic-Key", "skill-only"));
        Assert.True(accepted.IsSuccessStatusCode, await accepted.Content.ReadAsStringAsync());
        Assert.Equal(1, reader.Downloads);
        Assert.Contains("streaik_activate_skill", host.Requests.Last().Body!["tools"]!.ToJsonString());
    }

    private sealed class SkillReader : ISkillReadProvider
    {
        public int Downloads { get; private set; }
        public IAsyncEnumerable<ProviderSkill> ListSkillsAsync(CancellationToken ct) => AsyncEnumerable.Empty<ProviderSkill>();
        public IAsyncEnumerable<ProviderSkillVersion> ListSkillVersionsAsync(string skillId, CancellationToken ct) => AsyncEnumerable.Empty<ProviderSkillVersion>();

        public Task<byte[]> DownloadSkillAsync(string skillId, string? version, CancellationToken ct)
        {
            Assert.Equal("fixture", skillId);
            Assert.Equal("1", version);
            Downloads++;
            using var memory = new MemoryStream();
            using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
            using (var entry = new StreamWriter(archive.CreateEntry("fixture/SKILL.md").Open()))
                entry.Write("---\nname: fixture\ndescription: Fixture skill\n---\nRead me.");
            return Task.FromResult(memory.ToArray());
        }
    }

    [Theory]
    [InlineData("/v1/responses", """{"model":"openai/test","input":"hello","tools":[{"name":"mcp","type":"mcp","server_label":"inventory","server_url":"https://inventory.example.test/mcp"}]}""")]
    [InlineData("/api/chat", """{"model":"openai/test","id":"one","messages":[{"id":"u","role":"user","parts":[{"type":"text","text":"hello"}]}],"tools":[{"name":"mcp","inputSchema":{"type":"object","mcp":{"server_label":"inventory","server_url":"https://inventory.example.test/mcp"}}}]}""")]
    [InlineData("/api/chat", """{"model":"openai/test","id":"one","messages":[{"id":"u","role":"user","parts":[{"type":"text","text":"hello"}]}],"tools":[{"name":"resource_search","inputSchema":{"type":"object","resource_search":{}}}]}""")]
    public async Task Named_hosted_tools_fail_closed_when_disabled(string route, string body)
    {
        await using var host = await FixtureHost.StartAsync();
        using var result = await host.PostAsync(route, body, Key);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, result.StatusCode);
        Assert.Empty(host.Requests);
    }

    private static JsonObject McpRequest(string route, string? execution, string model = "openai/test")
    {
        var descriptor = new JsonObject
        {
            ["server_label"] = "inventory", ["server_url"] = Endpoint.AbsoluteUri, ["allowed_tools"] = new JsonArray("lookup"),
            ["headers"] = new JsonObject { ["Authorization"] = "Bearer mcp-only", ["X-Region"] = "eu" }
        };
        if (execution is not null) descriptor["execution"] = execution;
        if (route == "/api/chat") return new JsonObject
        {
            ["model"] = model, ["id"] = "chat-provider-mcp",
            ["messages"] = JsonNode.Parse("""[{"id":"u","role":"user","parts":[{"type":"text","text":"hello"}]}]"""),
            ["tools"] = new JsonArray(new JsonObject
            { ["name"] = "mcp", ["inputSchema"] = new JsonObject { ["type"] = "object", ["mcp"] = descriptor } })
        };
        descriptor["type"] = "mcp";
        descriptor["name"] = "mcp";
        return new JsonObject { ["model"] = model, ["input"] = "hello", ["tools"] = new JsonArray(descriptor) };
    }

    [Theory]
    [InlineData("/api/chat", null)]
    [InlineData("/v1/responses", null)]
    [InlineData("/api/chat", "client")]
    [InlineData("/v1/responses", "client")]
    public async Task Hosted_MCP_forwards_explicit_headers_to_the_MCP_endpoint_only(string route, string? execution)
    {
        var calls = 0;
        await using var host = await FixtureHost.StartAsync(configure: services =>
        {
            services.AddStreaikPublicHostedTools();
            var access = PublicMcpAccess.Create(allowLoopback: false, allowRequestCredentials: true);
            services.AddSingleton(access with { CreateMcpHandler = _ => new McpFixture(() => { }, observe: outgoing =>
            {
                calls++;
                Assert.Equal(Endpoint, outgoing.RequestUri);
                Assert.Equal("Bearer mcp-only", outgoing.Headers.GetValues("Authorization").Single());
                Assert.Equal("eu", outgoing.Headers.GetValues("X-Region").Single());
                Assert.False(outgoing.Headers.Contains(Key.Name));
            }) });
        });
        using var result = await host.PostAsync(route, McpRequest(route, execution).ToJsonString(), Key);
        await GatewayTests.Success(result);
        Assert.True(calls > 0);
        var body = host.Requests.Single().Body!.ToJsonString();
        Assert.Contains("streaik_mcp_", body);
        Assert.DoesNotContain("mcp-only", body);
        Assert.DoesNotContain("X-Region", body);
    }

    [Theory]
    [InlineData("/api/chat")]
    [InlineData("/v1/responses")]
    public async Task Provider_MCP_forwards_the_native_descriptor_without_a_local_harness(string route)
    {
        await using var host = await FixtureHost.StartAsync(configure: services => services.AddSingleton(
            new HostedToolAccess((_, _, _, _) => throw new InvalidOperationException("Must not authorize an MCP connection."))));
        using var result = await host.PostAsync(route, McpRequest(route, "server").ToJsonString(), Key);
        await GatewayTests.Success(result);
        var outbound = host.Requests.Single().Body!;
        var tool = Assert.Single(outbound["tools"]!.AsArray())!;
        Assert.Equal("mcp", (string?)tool["type"]);
        Assert.Equal("inventory", (string?)tool["server_label"]);
        Assert.Equal(Endpoint.AbsoluteUri, (string?)tool["server_url"]);
        Assert.Equal("lookup", (string?)tool["allowed_tools"]![0]);
        Assert.Equal("Bearer mcp-only", (string?)tool["headers"]!["Authorization"]);
        Assert.Equal("eu", (string?)tool["headers"]!["X-Region"]);
        Assert.Null(tool["execution"]);
        Assert.Null(tool["name"]);
    }

    [Theory]
    [InlineData("/api/chat")]
    [InlineData("/v1/responses")]
    public async Task Anthropic_provider_MCP_maps_bearer_auth_and_rejects_unrepresentable_headers(string route)
    {
        await using var host = await FixtureHost.StartAsync();
        var request = McpRequest(route, "server", "anthropic/test");
        var descriptor = route == "/api/chat" ? request["tools"]![0]!["inputSchema"]!["mcp"]! : request["tools"]![0]!;
        request[route == "/api/chat" ? "maxOutputTokens" : "max_output_tokens"] = 256;
        var key = ("x-anthropic-key", "inference-only");
        var version = ("anthropic-version", "2023-06-01");
        using var unsupported = await host.PostAsync(route, request.ToJsonString(), key, version);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unsupported.StatusCode);
        Assert.Empty(host.Requests);

        descriptor["headers"]!.AsObject().Remove("X-Region");
        using var forwarded = await host.PostAsync(route, request.ToJsonString(), key, version);
        await GatewayTests.Success(forwarded);
        var upstream = Assert.Single(host.Requests);
        Assert.EndsWith("/v1/messages", upstream.Path);
        Assert.Contains("mcp-client-2025-11-20", upstream.Headers["anthropic-beta"]);
        var endpoint = Assert.Single(upstream.Body!["mcp_servers"]!.AsArray())!;
        Assert.Equal(Endpoint.AbsoluteUri, (string?)endpoint["url"]);
        Assert.Equal("mcp-only", (string?)endpoint["authorization_token"]);
        var toolset = Assert.Single(upstream.Body["tools"]!.AsArray())!;
        Assert.Equal("mcp_toolset", (string?)toolset["type"]);
        Assert.True((bool?)toolset["configs"]!["lookup"]!["enabled"]);
    }

    [Theory]
    [InlineData("/api/chat", true, false)]
    [InlineData("/api/chat", true, true)]
    [InlineData("/v1/responses", true, false)]
    [InlineData("/v1/responses", true, true)]
    [InlineData("/v1/responses", false, true)]
    public async Task Anthropic_hosted_MCP_preserves_native_rounds_and_keeps_credentials_separate(string route, bool stream, bool mixed)
    {
        const string providerUrl = "https://provider.example.test/mcp";
        const string listingBeta = "mcp-client-2026-09-15";
        var calls = 0;
        await using var host = await FixtureHost.StartAsync(upstream =>
        {
            Assert.Equal("/v1/messages", upstream.Path);
            var body = upstream.Body!;
            Assert.Equal(512, (int?)body["max_tokens"]);
            Assert.Equal("2026-10-21", upstream.Headers["anthropic-version"]);
            Assert.DoesNotContain(Endpoint.AbsoluteUri, upstream.RawBody!);
            Assert.DoesNotContain("mcp-only", upstream.RawBody!);
            Assert.DoesNotContain("X-Region", upstream.RawBody!);
            Assert.Null(body["include"]);
            if (route == "/api/chat")
            {
                Assert.Equal("adaptive", (string?)body["thinking"]!["type"]);
                Assert.Equal("medium", (string?)body["output_config"]!["effort"]);
            }
            var tools = body["tools"]!.AsArray();
            var function = Assert.Single(tools, tool => ((string?)tool?["name"])?.StartsWith("streaik_mcp_", StringComparison.Ordinal) == true)!;
            Assert.NotNull(function["input_schema"]);
            Assert.True((bool?)body["tool_choice"]!["disable_parallel_tool_use"]);
            if (mixed)
            {
                Assert.Equal(listingBeta, upstream.Headers["anthropic-beta"]);
                var endpoint = Assert.Single(body["mcp_servers"]!.AsArray())!;
                Assert.Equal(providerUrl, (string?)endpoint["url"]);
                Assert.Equal("upstream-only", (string?)endpoint["authorization_token"]);
                Assert.Equal("upstream", (string?)Assert.Single(tools, tool => (string?)tool?["type"] == "mcp_toolset")!["mcp_server_name"]);
            }
            else Assert.Null(body["mcp_servers"]);

            var messages = body["messages"]!.AsArray();
            if (messages.Count == 1)
            {
                var blocks = new List<string>();
                if (mixed)
                {
                    blocks.Add("""{"type":"mcp_tool_listing","mcp_server_name":"upstream","tools":[{"name":"status","input_schema":{"type":"object"}}]}""");
                    blocks.Add("""{"type":"mcp_tool_use","id":"mcptoolu_fixture","name":"status","server_name":"upstream","input":{}}""");
                    blocks.Add("""{"type":"mcp_tool_result","tool_use_id":"mcptoolu_fixture","is_error":false,"content":[{"type":"text","text":"Upstream result"}]}""");
                }
                blocks.Add("""{"type":"thinking","thinking":"fixture thinking","signature":"fixture signature"}""");
                blocks.Add($$$"""{"type":"tool_use","id":"toolu_gateway","name":{{{function["name"]!.ToJsonString()}}},"input":{}}""");
                return AnthropicMcpFixtureReply(stream, "tool_use", blocks);
            }

            Assert.Equal(3, messages.Count);
            var assistant = messages[1]!["content"]!.AsArray();
            var expected = mixed ? new[] { "mcp_tool_listing", "mcp_tool_use", "mcp_tool_result", "thinking", "tool_use" }
                : new[] { "thinking", "tool_use" };
            Assert.Equal(expected, assistant.Select(block => (string?)block!["type"]));
            Assert.Equal("fixture signature", (string?)assistant[^2]!["signature"]);
            if (mixed) Assert.Equal("mcptoolu_fixture", (string?)assistant[2]!["tool_use_id"]);
            var result = Assert.Single(messages[2]!["content"]!.AsArray())!;
            Assert.Equal("tool_result", (string?)result["type"]);
            Assert.Equal("toolu_gateway", (string?)result["tool_use_id"]);
            Assert.Contains("Found", result["content"]!.ToJsonString());
            return FixtureHost.Reply(upstream);
        }, services =>
        {
            services.AddStreaikPublicHostedTools();
            var access = PublicMcpAccess.Create(allowLoopback: false, allowRequestCredentials: true);
            services.AddSingleton(access with { CreateMcpHandler = endpoint =>
            {
                Assert.Equal(Endpoint, endpoint);
                return new McpFixture(() => calls++, observe: outgoing =>
                {
                    Assert.Equal("Bearer mcp-only", outgoing.Headers.GetValues("Authorization").Single());
                    Assert.Equal("eu", outgoing.Headers.GetValues("X-Region").Single());
                    Assert.False(outgoing.Headers.Contains("x-api-key"));
                });
            } });
        });

        var request = McpRequest(route, "client", "anthropic/test");
        var chat = route == "/api/chat";
        JsonNode Descriptor(JsonNode entry) => chat ? entry["inputSchema"]!["mcp"]! : entry;
        Descriptor(request["tools"]![0]!)["require_approval"] = "never";
        if (chat)
        {
            request["providerMetadata"] = JsonNode.Parse("""{"anthropic":{"max_tokens":512}}""");
            request["reasoning"] = "medium";
        }
        else
        {
            request["max_output_tokens"] = 512;
            request["stream"] = stream;
        }
        var headers = new List<(string, string)> { ("x-anthropic-key", "inference-only"), ("anthropic-version", "2026-10-21") };
        if (mixed)
        {
            var tool = McpRequest(route, "server")["tools"]![0]!.DeepClone();
            var descriptor = Descriptor(tool);
            descriptor["server_label"] = "upstream";
            descriptor["server_url"] = providerUrl;
            descriptor["headers"] = new JsonObject { ["Authorization"] = "Bearer upstream-only" };
            request["tools"]!.AsArray().Add(tool);
            headers.Add(("anthropic-beta", listingBeta));
        }
        using var response = await host.PostAsync(route, request.ToJsonString(), headers.ToArray());
        var wire = await GatewayTests.Success(response);
        Assert.Contains("hello", wire);
        if (mixed)
        {
            Assert.Contains("mcptoolu_fixture", wire);
            Assert.Contains(chat ? "mcp_tool_listing" : "mcp_list_tools", wire);
        }
        Assert.Equal(1, calls);
        Assert.Equal(2, host.Requests.Count);
    }

    private static HttpResponseMessage AnthropicMcpFixtureReply(bool stream, string stop, IReadOnlyList<string> blocks)
    {
        if (!stream) return FixtureHost.Json($$$"""
            {"type":"message","id":"msg_mcp","model":"upstream-revision","role":"assistant","content":[{{{string.Join(",", blocks)}}}],"stop_reason":"{{{stop}}}","usage":{"input_tokens":10,"output_tokens":3}}
            """);
        var events = new List<string> { FixtureHost.MessageStart };
        for (var index = 0; index < blocks.Count; index++)
        {
            events.Add($$"""{"type":"content_block_start","index":{{index}},"content_block":{{blocks[index]}}}""");
            events.Add($$"""{"type":"content_block_stop","index":{{index}}}""");
        }
        events.Add($$$"""{"type":"message_delta","delta":{"stop_reason":"{{{stop}}}"},"usage":{"output_tokens":3}}""");
        events.Add(FixtureHost.MessageStop);
        return FixtureHost.Sse(events.ToArray());
    }

    [Theory]
    [InlineData("/api/chat", true)]
    [InlineData("/v1/responses", true)]
    [InlineData("/v1/responses", false)]
    public async Task Anthropic_provider_MCP_replays_new_beta_listings_and_results_without_local_execution(string route, bool stream)
    {
        const string listing = """{"type":"mcp_tool_listing","mcp_server_name":"inventory","tools":[{"name":"lookup","input_schema":{"type":"object"}}]}""";
        const string invocation = """{"type":"mcp_tool_use","id":"mcptoolu_lookup","name":"lookup","server_name":"inventory","input":{}}""";
        const string result = """{"type":"mcp_tool_result","tool_use_id":"mcptoolu_lookup","is_error":false,"content":[{"type":"text","text":"Upstream result"}]}""";
        await using var host = await FixtureHost.StartAsync(upstream =>
        {
            Assert.Equal("other-beta,mcp-client-2026-09-15", upstream.Headers["anthropic-beta"]);
            var messages = upstream.Body!["messages"]!.AsArray();
            if (messages.Count == 1) return AnthropicMcpFixtureReply(stream, "end_turn", [listing, invocation, result]);
            var assistant = messages[1]!["content"]!.AsArray();
            Assert.Equal(new[] { "mcp_tool_listing", "mcp_tool_use", "mcp_tool_result" }, assistant.Select(block => (string?)block!["type"]));
            Assert.Equal("lookup", (string?)assistant[0]!["tools"]![0]!["name"]);
            Assert.Equal("mcptoolu_lookup", (string?)assistant[1]!["id"]);
            Assert.Equal("mcptoolu_lookup", (string?)assistant[2]!["tool_use_id"]);
            Assert.Equal("Upstream result", (string?)assistant[2]!["content"]![0]!["text"]);
            return FixtureHost.Reply(upstream);
        }, services => services.AddSingleton(new HostedToolAccess((_, _, _, _) =>
            throw new InvalidOperationException("Provider MCP must not open a gateway MCP session."))));

        var request = McpRequest(route, "server", "anthropic/test");
        var chat = route == "/api/chat";
        var descriptor = chat ? request["tools"]![0]!["inputSchema"]!["mcp"]! : request["tools"]![0]!;
        descriptor["headers"]!.AsObject().Remove("X-Region");
        request[chat ? "maxOutputTokens" : "max_output_tokens"] = 512;
        if (!chat) request["stream"] = stream;
        (string, string)[] headers = [("x-anthropic-key", "inference-only"), ("anthropic-version", "2026-10-21"),
            ("anthropic-beta", "other-beta,mcp-client-2026-09-15")];
        using var first = await host.PostAsync(route, request.ToJsonString(), headers);
        var wire = await GatewayTests.Success(first);
        if (chat)
        {
            var input = GatewayTests.Part(wire, "tool-input-available");
            var output = GatewayTests.Part(wire, "tool-output-available");
            var metadata = GatewayTests.Part(wire, "message-metadata")["messageMetadata"]!;
            Assert.True((bool?)input["providerExecuted"]);
            request["messages"]!.AsArray().Add(new JsonObject
            {
                ["id"] = "a", ["role"] = "assistant", ["metadata"] = metadata.DeepClone(),
                ["parts"] = new JsonArray(new JsonObject
                {
                    ["type"] = "dynamic-tool", ["state"] = "output-available", ["providerExecuted"] = true,
                    ["toolCallId"] = input["toolCallId"]!.DeepClone(), ["toolName"] = input["toolName"]!.DeepClone(),
                    ["input"] = input["input"]!.DeepClone(), ["output"] = output["output"]!.DeepClone(),
                    ["callProviderMetadata"] = input["providerMetadata"]!.DeepClone(), ["resultProviderMetadata"] = output["providerMetadata"]!.DeepClone()
                })
            });
            request["messages"]!.AsArray().Add(JsonNode.Parse("""{"id":"u2","role":"user","parts":[{"type":"text","text":"follow up"}]}"""));
        }
        else
        {
            var response = stream ? GatewayTests.Part(wire, "response.completed")["response"]! : JsonNode.Parse(wire)!;
            var output = response["output"]!.AsArray();
            Assert.Equal(new[] { "mcp_list_tools", "mcp_call" }, output.Select(item => (string?)item!["type"]));
            request["input"] = new JsonArray(JsonNode.Parse("""{"role":"user","content":"hello"}"""),
                output[0]!.DeepClone(), output[1]!.DeepClone(), JsonNode.Parse("""{"role":"user","content":"follow up"}"""));
        }
        using var replayed = await host.PostAsync(route, request.ToJsonString(), headers);
        Assert.Contains("hello", await GatewayTests.Success(replayed));
        Assert.Equal(2, host.Requests.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Provider_MCP_API_Chat_replays_execution_approvals_upstream_only(bool approved)
    {
        await using var host = await FixtureHost.StartAsync(request =>
        {
            if (request.Body!["input"]!.AsArray().Any(item => (string?)item?["type"] == "mcp_approval_response")) return FixtureHost.Reply(request);
            return FixtureHost.Sse($$"""{"type":"response.completed","sequence_number":0,"response":{{Result(new
                { type = "mcp_approval_request", id = "native_approval", server_label = "inventory", name = "lookup", arguments = "{\"sku\":\"A-1\"}" })}}}""");
        }, services =>
        {
            services.AddStreaikHostedTools();
            services.AddSingleton(new HostedToolAccess((_, _, _, _) => throw new InvalidOperationException("Must not authorize an MCP connection.")));
        });
        var request = McpRequest("/api/chat", "server");
        using var first = await host.PostAsync("/api/chat", request.ToJsonString(), Key);
        var wire = await GatewayTests.Success(first);
        Assert.DoesNotContain("mcp-only", wire);
        var call = GatewayTests.Part(wire, "tool-input-available");
        Assert.Equal("streaik_hosted_mcp_approval", (string?)call["toolName"]);
        var approval = GatewayTests.Part(wire, "tool-approval-request");
        Assert.Null(approval["approvalDescriptor"]!["output"]);
        var decision = new JsonObject
        {
            ["type"] = "dynamic-tool", ["toolName"] = call["toolName"]!.DeepClone(), ["toolCallId"] = call["toolCallId"]!.DeepClone(),
            ["providerExecuted"] = true, ["input"] = call["input"]!.DeepClone(), ["state"] = "approval-responded",
            ["toolMetadata"] = call["toolMetadata"]!.DeepClone(),
            ["approval"] = new JsonObject { ["id"] = approval["approvalId"]!.DeepClone(), ["approved"] = approved }
        };
        request["messages"]!.AsArray().Add(new JsonObject { ["id"] = "a", ["role"] = "assistant", ["parts"] = new JsonArray(decision) });
        using var resumed = await host.PostAsync("/api/chat", request.ToJsonString(), Key);
        await GatewayTests.Success(resumed);
        var input = host.Requests.Last().Body!["input"]!.AsArray();
        var forwarded = Assert.Single(input, item => (string?)item?["type"] == "mcp_approval_response")!;
        Assert.Equal("native_approval", (string?)forwarded["approval_request_id"]);
        Assert.Equal(approved, (bool?)forwarded["approve"]);
        Assert.Contains(input, item => (string?)item?["type"] == "mcp_approval_request" && (string?)item?["id"] == "native_approval");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Nested_API_Chat_MCP_uses_hosted_execution_without_browser_approval(bool allowUnreviewedResults)
    {
        var invoked = 0;
        var endpoint = new Uri("https://mcp.aihappey.net/github-restcountries");
        await using var host = await FixtureHost.StartAsync(request =>
        {
            var tools = request.Body!["tools"]!.AsArray();
            var function = Assert.Single(tools, tool => ((string?)tool?["name"])?.StartsWith("streaik_mcp_", StringComparison.Ordinal) == true)!;
            Assert.Contains(tools, tool => (string?)tool?["name"] == "streaik_search_resources");
            Assert.DoesNotContain(tools, tool => (string?)tool?["name"] is "mcp" or "resource_search");
            Assert.DoesNotContain(endpoint.AbsoluteUri, tools.ToJsonString());

            var output = request.Body["input"]!.AsArray().SingleOrDefault(item => (string?)item?["type"] == "function_call_output");
            if (output is not null)
            {
                Assert.Contains("Found", (string?)output["output"]);
                return FixtureHost.Reply(request);
            }
            var result = Result(new { type = "function_call", call_id = "call_fixture", name = (string?)function["name"], arguments = "{}" });
            return FixtureHost.Sse($$"""{"type":"response.completed","sequence_number":0,"response":{{result}}}""");
        }, services =>
        {
            services.AddStreaikHostedTools();
            services.AddSingleton(new HostedToolAccess((label, url, _, _) => ValueTask.FromResult(
                label == "github-restcountries" && url == endpoint ? new McpEndpointAccess
                {
                    ToolNames = new HashSet<string> { "lookup" }, AllowUnreviewedResults = allowUnreviewedResults
                } : null), CreateMcpHandler: _ => new McpFixture(() => invoked++)));
        });

        using var response = await host.PostAsync("/api/chat", """
            {
              "id":"test-mcp-001","model":"openai/test",
              "messages":[{"id":"msg-001","role":"user","parts":[{"type":"text","text":"Use the MCP server to find documentation about streaming responses."}]}],
              "tools":[
                {"name":"mcp","inputSchema":{"type":"object","mcp":{
                  "server_label":"github-restcountries","server_url":"https://mcp.aihappey.net/github-restcountries",
                  "defer_loading":false,"require_approval":"never"
                }}},
                {"name":"resource_search","inputSchema":{"type":"object","resource_search":{}}}
              ]
            }
            """, Key);
        var wire = await response.Content.ReadAsStringAsync();
        Assert.Equal(allowUnreviewedResults ? HttpStatusCode.OK : HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain("tool-approval-request", wire);
        Assert.Equal(allowUnreviewedResults ? 1 : 0, invoked);
        Assert.Equal(allowUnreviewedResults ? 2 : 1, host.Requests.Count);
        if (allowUnreviewedResults)
        {
            var call = GatewayTests.Part(wire, "tool-input-available");
            var result = GatewayTests.Part(wire, "tool-output-available");
            Assert.Equal("call_fixture", (string?)call["toolCallId"]);
            Assert.Equal("call_fixture", (string?)result["toolCallId"]);
            Assert.StartsWith("streaik_mcp_lookup_", (string?)call["toolName"]);
            Assert.True((bool?)call["providerExecuted"]);
            Assert.True((bool?)result["providerExecuted"]);
            Assert.Equal("Found", (string?)result["output"]!["content"]![0]!["text"]);
        }
        else Assert.DoesNotContain("tool-input-available", wire);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Hosted_activity_reaches_the_client_before_the_tool_returns(bool relay, bool fail)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = await FixtureHost.StartAsync(request =>
        {
            if (request.Body!["input"]!.AsArray().Any(item => (string?)item?["type"] == "function_call_output"))
                return FixtureHost.Reply(request);
            var name = (string?)Assert.Single(request.Body["tools"]!.AsArray())!["name"];
            var result = Result(new { type = "function_call", call_id = "call_live", name, arguments = "{\"sku\":\"A-1\"}" });
            return FixtureHost.Sse($$"""{"type":"response.completed","sequence_number":0,"response":{{result}}}""");
        }, services =>
        {
            services.AddStreaikHostedTools();
            services.AddSingleton(new HostedToolAccess((label, url, _, _) => ValueTask.FromResult(
                label == "inventory" && url == Endpoint ? new McpEndpointAccess { ToolNames = new HashSet<string> { "lookup" }, AllowUnreviewedResults = true } : null),
                CreateMcpHandler: _ => new McpFixture(() => entered.TrySetResult(), async ct =>
                {
                    await release.Task.WaitAsync(ct);
                    if (fail) throw new HttpRequestException("Private downstream failure details");
                })));
        });
        const string body = """
            {"model":"openai/test","id":"live","messages":[{"id":"u","role":"user","parts":[{"type":"text","text":"hello"}]}],
             "tools":[{"name":"mcp","inputSchema":{"type":"object","mcp":{"server_label":"inventory","server_url":"https://inventory.example.test/mcp","defer_loading":false,"require_approval":"never"}}}]}
            """;
        const string native = """
            {"model":"openai/test","input":"hello","stream":true,"tools":[{"type":"mcp","name":"mcp",
              "server_label":"inventory","server_url":"https://inventory.example.test/mcp","defer_loading":false,"require_approval":"never"}]}
            """;
        using var request = new HttpRequestMessage(HttpMethod.Post, relay ? "/v1/responses" : "/api/chat")
        {
            Content = new StringContent(relay ? native : body, System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.Add(Key.Item1, Key.Item2);
        if (relay) request.Headers.Add(HostedToolActivity.ResponsesHeader, "1");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            using var response = await host.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
                Assert.Fail($"{response.StatusCode}: {await response.Content.ReadAsStringAsync(timeout.Token)}");
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
            var expected = relay ? HostedToolActivity.ResponsesEvent : "tool-input-available";
            JsonNode? call = null;
            while (await reader.ReadLineAsync(timeout.Token) is { } line)
            {
                if (!line.StartsWith("data: {", StringComparison.Ordinal)) continue;
                var frame = JsonNode.Parse(line[6..])!;
                if ((string?)frame["type"] == expected) { call = frame; break; }
            }
            Assert.NotNull(call);
            var data = relay ? call["activity"]! : call;
            Assert.Equal("call_live", (string?)data[relay ? "callId" : "toolCallId"]);
            Assert.Equal("A-1", (string?)data["input"]!["sku"]);
            await entered.Task.WaitAsync(timeout.Token);
            Assert.Single(host.Requests); // The follow-up model request cannot start until the tool returns.
            release.SetResult();
            var rest = await reader.ReadToEndAsync(timeout.Token);
            Assert.DoesNotContain("tool-approval-request", rest);
            if (fail)
            {
                var output = GatewayTests.Part(rest, "tool-output-error");
                Assert.Equal("call_live", (string?)output["toolCallId"]);
                Assert.True((bool?)output["providerExecuted"]);
                Assert.DoesNotContain("Private downstream failure details", rest);
                Assert.Contains("[DONE]", rest);
                Assert.Single(host.Requests);
            }
            else
            {
                Assert.Contains(relay ? HostedToolActivity.ResponsesEvent : "tool-output-available", rest);
                Assert.Contains("Found", rest);
                Assert.Equal(2, host.Requests.Count);
            }
        }
        finally { release.TrySetResult(); }
    }

    [Theory]
    [InlineData("""{"name":"mcp","inputSchema":{"type":"object","mcp":null}}""")]
    [InlineData("""{"name":"mcp","inputSchema":{"type":"object","resource_search":{}}}""")]
    [InlineData("""{"name":"mcp","inputSchema":{"type":"object","mcp":{},"resource_search":{}}}""")]
    [InlineData("""{"name":"mcp","inputSchema":{"type":"object","x-streaik":{}}}""")]
    [InlineData("""{"name":"mcp","server_label":"inventory","server_url":"https://inventory.example.test/mcp"}""")]
    public async Task Malformed_hosted_descriptors_do_not_fall_through_to_browser_tools(string descriptor)
    {
        await using var host = await FixtureHost.StartAsync();
        using var response = await host.PostAsync("/api/chat", $$"""
            {"model":"openai/test","id":"one","messages":[{"id":"u","role":"user","parts":[{"type":"text","text":"hello"}]}],"tools":[{{descriptor}}]}
            """, Key);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(host.Requests);
    }

    [Fact]
    public async Task Native_mcp_and_an_ordinary_function_named_mcp_still_pass_through()
    {
        await using var host = await FixtureHost.StartAsync();
        using var native = await host.PostAsync("/v1/responses", """
            {"model":"openai/test","input":"hello","tools":[{"type":"mcp","server_label":"inventory","server_url":"https://inventory.example.test/mcp"}]}
            """, Key);
        native.EnsureSuccessStatusCode();
        using var chat = await host.PostAsync("/api/chat", """
            {"model":"openai/test","id":"one","messages":[{"id":"u","role":"user","parts":[{"type":"text","text":"hello"}]}],"tools":[{"name":"mcp","inputSchema":{"type":"object"}}]}
            """, Key);
        chat.EnsureSuccessStatusCode();
        var tools = host.Requests.Where(request => request.Body?["tools"] is JsonArray).Select(request => request.Body!["tools"]![0]!).ToArray();
        Assert.Equal("mcp", (string?)tools[0]["type"]);
        Assert.Equal("function", (string?)tools[1]["type"]);
        Assert.Equal("mcp", (string?)tools[1]["name"]);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task Hosted_Responses_approval_resumes_on_another_instance(bool approve, bool stream)
    {
        var invoked = 0;
        var keys = new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider();
        void Configure(IServiceCollection services)
        {
            services.AddStreaikHostedTools();
            services.AddSingleton<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>(keys);
            services.AddSingleton(new HostedToolAccess((label, endpoint, _, _) => ValueTask.FromResult(
                label == "inventory" && endpoint == Endpoint ? new McpEndpointAccess { ToolNames = new HashSet<string> { "lookup" } } : null),
                CreateMcpHandler: _ => new McpFixture(() => invoked++)));
        }
        await using var host = await FixtureHost.StartAsync(request =>
        {
            var function = request.Body!["tools"]![0]!["name"]!.GetValue<string>();
            Assert.StartsWith("streaik_mcp_", function);
            var result = Result(new { type = "function_call", id = "fc_fixture", call_id = "call_fixture", name = function, arguments = "{\"country\":\"Poland\"}" });
            return stream ? FixtureHost.Sse($$"""{"type":"response.completed","sequence_number":0,"response":{{result}}}""") : FixtureHost.Json(result);
        }, Configure);
        await using var replica = await FixtureHost.StartAsync(request =>
        {
            Assert.Null(request.Body?["streaik_mcp_approvals"]);
            var input = request.Body!["input"]!.AsArray();
            Assert.DoesNotContain(input, item => ((string?)item?["type"])?.StartsWith("mcp_approval_", StringComparison.Ordinal) == true);
            var call = Assert.Single(input, item => (string?)item?["type"] == "function_call")!;
            var output = Assert.Single(input, item => (string?)item?["type"] == "function_call_output")!;
            Assert.Equal("call_fixture", (string?)call["call_id"]);
            Assert.Equal("call_fixture", (string?)output["call_id"]);
            Assert.Equal(approve, ((string?)output["output"])!.Contains("Found", StringComparison.Ordinal));
            return FixtureHost.Reply(request);
        }, Configure);

        const string tools = """[{"type":"mcp","name":"mcp","server_label":"inventory","server_url":"https://inventory.example.test/mcp","require_approval":"always"}]""";
        const string original = """{"role":"user","content":[{"type":"input_text","text":"look up an item"}]}""";
        using var first = await host.PostAsync("/v1/responses", $$"""{"model":"openai/test","stream":{{stream.ToString().ToLowerInvariant()}},"input":[{{original}}],"tools":{{tools}}}""", Key);
        var body = await GatewayTests.Success(first);
        var response = stream ? GatewayTests.Part(body, "response.completed")["response"]! : JsonNode.Parse(body)!;
        var pending = response["output"]!.AsArray().Single()!;
        Assert.Equal("mcp_approval_request", (string?)pending["type"]);
        Assert.Equal("lookup", (string?)pending["name"]);
        Assert.Null(pending["output"]);
        if (stream) Assert.Equal((string?)pending["id"], (string?)GatewayTests.Part(body, "response.output_item.done")["item"]!["id"]);
        Assert.DoesNotContain("streaik_mcp_inventory", body);
        Assert.Equal(0, invoked);

        var proof = response["metadata"]!["streaik_mcp_approvals"];
        var continuation = JsonNode.Parse($$"""{"model":"openai/test","input":[{{original}},{{pending.ToJsonString()}},{"type":"mcp_approval_response","approval_request_id":{{pending["id"]!.ToJsonString()}},"approve":{{approve.ToString().ToLowerInvariant()}}}],"tools":{{tools}},"streaik_mcp_approvals":{{proof!.ToJsonString()}}}""")!;
        var tampered = continuation.DeepClone();
        tampered["input"]![1]!["arguments"] = "{}";
        using var altered = await replica.PostAsync("/v1/responses", tampered.ToJsonString(), Key);
        Assert.Equal(HttpStatusCode.Forbidden, altered.StatusCode);
        using var wrongCaller = await replica.PostAsync("/v1/responses", continuation.ToJsonString(), (Key.Name, "another-key"));
        Assert.Equal(HttpStatusCode.Forbidden, wrongCaller.StatusCode);
        Assert.Equal(0, invoked);

        using var resumed = await replica.PostAsync("/v1/responses", continuation.ToJsonString(), Key);
        var resumedResponse = JsonNode.Parse(await GatewayTests.Success(resumed))!;
        var completed = resumedResponse["output"]!.AsArray().Single(item => (string?)item?["type"] == "mcp_call")!;
        Assert.Equal((string?)pending["id"], (string?)completed["approval_request_id"]);
        Assert.Equal(approve ? "completed" : "failed", (string?)completed["status"]);
        Assert.Equal(approve ? 1 : 0, invoked);

        completed.AsObject().Remove("approval_request_id");
        var historyProof = resumedResponse["metadata"]!["streaik_mcp_history"]!;
        using var replay = await replica.PostAsync("/v1/responses", $$"""{"model":"openai/test","input":[{{original}},{{completed.ToJsonString()}}],"tools":{{tools}},"streaik_mcp_history":{{historyProof.ToJsonString()}}}""", Key);
        await GatewayTests.Success(replay);
        Assert.Equal(approve ? 1 : 0, invoked);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Native_Responses_forwards_a_real_approval_with_inline_context(bool approve)
    {
        var dispatches = 0;
        await using var host = await FixtureHost.StartAsync(request =>
        {
            var decision = request.Body?["input"]?.AsArray().FirstOrDefault(item => (string?)item?["type"] == "mcp_approval_response");
            if (decision is null) return FixtureHost.Json(Result(new { type = "mcp_approval_request", id = "native_approval", server_label = "inventory", name = "lookup", arguments = "{}" }));
            Assert.Equal("native_approval", (string?)decision["approval_request_id"]);
            Assert.Contains(request.Body!["input"]!.AsArray(), item => (string?)item?["type"] == "mcp_approval_request" && (string?)item?["id"] == "native_approval");
            Assert.Null(request.Body["streaik_mcp_approvals"]);
            if ((bool?)decision["approve"] == true) dispatches++;
            return FixtureHost.Reply(request);
        }, configure: services => services.AddStreaikReferences(), configureApp: Identify);
        const string tools = """[{"type":"mcp","server_label":"inventory","server_url":"https://inventory.example.test/mcp"}]""";
        const string original = """{"role":"user","content":[{"type":"input_text","text":"look up an item"}]}""";
        var continuation = JsonNode.Parse($$"""{"model":"openai/test","input":[{{original}},{"type":"mcp_approval_response","approval_request_id":"native_approval","approve":{{(approve ? "true" : "false")}}}],"tools":{{tools}}}""")!;
        using var unknown = await host.PostAsync("/v1/responses", continuation.ToJsonString(), Key);
        Assert.Equal(HttpStatusCode.Forbidden, unknown.StatusCode);
        using var first = await host.PostAsync("/v1/responses", $$"""{"model":"openai/test","input":[{{original}}],"tools":{{tools}}}""", Key);
        var response = JsonNode.Parse(await GatewayTests.Success(first))!;
        var item = response["output"]![0]!;
        Assert.Equal("native_approval", (string?)item["id"]);
        Assert.Equal("{}", (string?)item["arguments"]);
        Assert.Equal(0, dispatches);
        continuation["input"]!.AsArray().Insert(1, item.DeepClone());
        continuation["streaik_mcp_approvals"] = response["metadata"]!["streaik_mcp_approvals"]!.DeepClone();
        using var resumed = await host.PostAsync("/v1/responses", continuation.ToJsonString(), Key);
        Assert.True(resumed.IsSuccessStatusCode, await resumed.Content.ReadAsStringAsync());
        Assert.Equal(approve ? 1 : 0, dispatches);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task Hosted_API_Chat_resumes_across_instances_and_replays_results_without_execution(bool approve, bool signed)
    {
        var invoked = 0;
        var authorized = true;
        Task<FixtureHost> Start() => FixtureHost.StartAsync(request =>
        {
            if (request.Body!["input"]!.AsArray().Any(item => (string?)item?["type"] == "function_call_output"))
                return FixtureHost.Reply(request);
            var name = (string?)request.Body["tools"]![0]!["name"];
            var result = Result(new { type = "function_call", call_id = "call_review", name, arguments = "{\"country\":\"Poland\"}" });
            return FixtureHost.Sse($$"""{"type":"response.completed","sequence_number":0,"response":{{result}}}""");
        }, services =>
        {
            services.AddStreaikHostedTools();
            if (signed) services.AddSingleton(new Streaik.Vercel.Security.ToolApprovalSigner("local-test-signing-key"));
            services.AddSingleton(new HostedToolAccess((label, url, _, _) => ValueTask.FromResult(
                authorized && label == "inventory" && url == Endpoint ? new McpEndpointAccess { ToolNames = new HashSet<string> { "lookup" } } : null),
                CreateMcpHandler: _ => new McpFixture(() => invoked++)));
        });
        await using var host = await Start();
        await using var resumeHost = await Start();
        var request = JsonNode.Parse("""
            {"model":"openai/test","id":"chat-review","messages":[{"id":"u","role":"user","parts":[{"type":"text","text":"Poland"}]}],
              "tools":[{"name":"mcp","inputSchema":{"type":"object","mcp":{"server_label":"inventory",
                "server_url":"https://inventory.example.test/mcp","defer_loading":false,"require_approval":"always"}}}]}
            """)!;
        using var first = await host.PostAsync("/api/chat", request.ToJsonString(), Key);
        var wire = await GatewayTests.Success(first);
        var call = GatewayTests.Part(wire, "tool-input-available");
        var approval = GatewayTests.Part(wire, "tool-approval-request");
        Assert.True((bool?)call["providerExecuted"]);
        Assert.Equal("Poland", (string?)call["input"]!["country"]);
        Assert.Null(approval["approvalDescriptor"]!["output"]);
        Assert.Equal(signed, approval["signature"] is not null);
        Assert.DoesNotContain("tool-output-available", wire);
        Assert.DoesNotContain("Found", host.Requests.Single().Body!.ToJsonString());
        Assert.Equal(0, invoked);

        var decision = HostedDecision(call, approval, approve);
        decision["output"] = "client-selected result";
        request["messages"]!.AsArray().Add(new JsonObject { ["id"] = "a", ["role"] = "assistant", ["parts"] = new JsonArray(decision) });
        if (signed)
        {
            using var wrongCaller = await resumeHost.PostAsync("/api/chat", request.ToJsonString(), (Key.Name, "different-credential"));
            Assert.False(wrongCaller.IsSuccessStatusCode);
            decision["input"]!["country"] = "tampered";
            using var tampered = await resumeHost.PostAsync("/api/chat", request.ToJsonString(), Key);
            Assert.False(tampered.IsSuccessStatusCode);
            decision["input"] = call["input"]!.DeepClone();
            decision["approval"]!.AsObject().Remove("signature");
            using var unsigned = await resumeHost.PostAsync("/api/chat", request.ToJsonString(), Key);
            Assert.False(unsigned.IsSuccessStatusCode);
            decision["approval"]!["signature"] = approval["signature"]!.DeepClone();
        }
        if (approve)
        {
            authorized = false;
            using var denied = await resumeHost.PostAsync("/api/chat", request.ToJsonString(), Key);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            authorized = true;
        }
        Assert.Equal(0, invoked);
        using var resumed = await resumeHost.PostAsync("/api/chat", request.ToJsonString(), Key);
        var completed = await GatewayTests.Success(resumed);
        var input = resumeHost.Requests.Last().Body!["input"]!.AsArray();
        var forwarded = Assert.Single(input, item => (string?)item?["type"] == "function_call_output")!;
        if (approve) Assert.Contains("Found", (string?)forwarded["output"]);
        else Assert.Equal("The user declined to run the MCP tool.", (string?)forwarded["output"]);
        Assert.DoesNotContain("client-selected result", input.ToJsonString());
        Assert.Equal(approve ? 1 : 0, invoked);
        var receipt = GatewayTests.Part(completed, "tool-input-available");
        Assert.Equal("streaik_hosted_mcp_result", (string?)receipt["toolName"]);
        receipt["type"] = "dynamic-tool";
        receipt["state"] = "output-available";
        receipt["output"] = GatewayTests.Part(completed, "tool-output-available")["output"]!.DeepClone();
        decision.Parent!.AsArray().Add(receipt);
        request["messages"]!.AsArray().Add(JsonNode.Parse("""{"id":"u2","role":"user","parts":[{"type":"text","text":"Continue"}]}"""));
        using var next = await host.PostAsync("/api/chat", request.ToJsonString(), Key);
        await GatewayTests.Success(next);
        Assert.Contains(host.Requests.Last().Body!["input"]!.AsArray(), item => (string?)item?["type"] == "function_call_output");
        Assert.Equal(approve ? 1 : 0, invoked);
    }

    private static JsonObject HostedDecision(JsonNode call, JsonNode approval, bool approved) => new()
    {
        ["type"] = "dynamic-tool", ["toolName"] = call["toolName"]!.DeepClone(), ["toolCallId"] = call["toolCallId"]!.DeepClone(),
        ["state"] = "approval-responded", ["providerExecuted"] = true, ["input"] = call["input"]!.DeepClone(),
        ["toolMetadata"] = call["toolMetadata"]!.DeepClone(),
        ["approval"] = new JsonObject { ["id"] = approval["approvalId"]!.DeepClone(), ["approved"] = approved,
            ["signature"] = approval["signature"]?.DeepClone() }
    };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Converted_API_Chat_approval_only_dispatches_after_permission(bool approve)
    {
        var invoked = 0;
        var rounds = 0;
        await using var host = await FixtureHost.StartAsync(request =>
        {
            if (++rounds != 1) return FixtureHost.Reply(request);
            return FixtureHost.Sse($$"""{"type":"response.completed","sequence_number":0,"response":{{Result(new
                { type = "mcp_approval_request", id = "native_approval", server_label = "inventory", name = "lookup", arguments = "{\"sku\":\"A-1\"}" })}}}""");
        }, configure: services =>
        {
            services.AddStreaikHostedTools();
            services.AddSingleton(new HostedToolAccess((label, endpoint, _, _) => ValueTask.FromResult(
                label == "inventory" && endpoint == Endpoint ? new McpEndpointAccess { ToolNames = new HashSet<string> { "lookup" } } : null),
                CreateMcpHandler: _ => new McpFixture(() => invoked++)));
        }, configureApp: Identify);
        const string provider = """
            "providerMetadata":{"openai":{"tools":[{"type":"mcp","server_label":"inventory","server_url":"https://inventory.example.test/mcp","allowed_tools":["lookup"]}]}}
            """;
        const string user = """{"id":"u","role":"user","parts":[{"type":"text","text":"hello"}]}""";
        using var first = await host.PostAsync("/api/chat", $$"""{"model":"openai/test","id":"chat-1","messages":[{{user}}],{{provider}}}""", Key);
        var sse = await first.Content.ReadAsStringAsync();
        Assert.True(first.IsSuccessStatusCode, sse);
        var approvalEvent = sse.Split('\n').Where(line => line.StartsWith("data: {", StringComparison.Ordinal)).Select(line => JsonNode.Parse(line[6..])!)
            .Single(part => (string?)part["type"] == "tool-approval-request");
        Assert.Equal("A-1", (string?)GatewayTests.Part(sse, "tool-input-available")["input"]!["sku"]);
        Assert.Null(approvalEvent["approvalDescriptor"]!["output"]);
        Assert.Equal(0, invoked);
        var decision = HostedDecision(GatewayTests.Part(sse, "tool-input-available"), approvalEvent, approve);
        decision["output"] = "client-selected result";
        var messages = JsonSerializer.Serialize(new[] { new { id = "assistant", role = "assistant", parts = new[] { decision } } });
        using var resumed = await host.PostAsync("/api/chat", $$"""{"model":"openai/test","id":"chat-1","messages":[{{user}},{{messages[1..^1]}}],{{provider}}}""", Key);
        Assert.True(resumed.IsSuccessStatusCode, await resumed.Content.ReadAsStringAsync());
        var replay = Assert.Single(host.Requests.Last().Body!["input"]!.AsArray(), item => (string?)item?["type"] == "mcp_call")!;
        Assert.Equal("mcp_call", (string?)replay["type"]);
        var forwarded = replay["output"]?.GetValue<string>();
        if (approve) Assert.Contains("Found", forwarded);
        else Assert.Null(forwarded);
        Assert.Equal(approve ? 1 : 0, invoked);
    }

    private static void Identify(WebApplication app) => app.Use(async (context, next) =>
    {
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "fixture-user")], "fixture"));
        await next(context);
    });

    private static string Result(object item) => JsonSerializer.Serialize(new { id = "resp_test", @object = "response", model = "test", status = "completed", output = new[] { item } });

    internal sealed class McpFixture(Action invoked, Func<CancellationToken, Task>? beforeResult = null, Action<HttpRequestMessage>? observe = null) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Content is null) return new(HttpStatusCode.NoContent);
            observe?.Invoke(request);
            using var document = JsonDocument.Parse(await request.Content.ReadAsStringAsync(ct));
            var json = document.RootElement;
            if (!json.TryGetProperty("id", out var id)) return new(HttpStatusCode.Accepted);
            var method = json.GetProperty("method").GetString();
            if (method == "server/discover") return FixtureHost.Json($$$"""{"jsonrpc":"2.0","id":{{{id.GetRawText()}}},"error":{"code":-32601,"message":"Unknown method"}}""");
            var result = method switch
            {
                "initialize" => """{"protocolVersion":"2025-06-18","capabilities":{"tools":{}},"serverInfo":{"name":"fixture","version":"1"}}""",
                "tools/list" => """{"tools":[{"name":"lookup","inputSchema":{"type":"object"}}]}""",
                "tools/call" => """{"content":[{"type":"text","text":"Found"}]}""",
                _ => throw new InvalidOperationException("Unexpected MCP request.")
            };
            if (method == "tools/call")
            {
                invoked();
                if (beforeResult is not null) await beforeResult(ct);
            }
            return FixtureHost.Json($$"""{"jsonrpc":"2.0","id":{{id.GetRawText()}},"result":{{result}}}""");
        }
    }
}
