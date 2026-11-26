using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Streaik.Providers;
using Streaik.Vercel.Security;
using Xunit;

namespace Streaik.Gateway.Tests;

public sealed class EchoTests
{
    private const string InputModel = "echo/echo-input-required";

    [Theory]
    [InlineData("/v1/chat/completions", "reasoning_content")]
    [InlineData("/v1/responses", "summary_text")]
    [InlineData("/v1/messages", "thinking")]
    [InlineData("/api/chat", "reasoning-delta")]
    public async Task Language_protocols_echo_only_the_latest_user_and_stream_reasoning(string path, string reasoningField)
    {
        await using var host = await FixtureHost.StartAsync(configure: services => services.AddEcho());
        var request = GatewayTests.Request(path, "echo/echo-reasoning", true);
        var message = (path == "/v1/responses" ? "input" : "messages");
        if (request[message] is JsonArray history && history.Count > 0) history.Insert(0, history[0]!.DeepClone());
        foreach (var stream in path == "/api/chat" ? new[] { true } : new[] { true, false })
        {
            request["stream"] = stream;
            var wire = await Send(host, path, request.ToJsonString());
            Assert.Contains(reasoningField, wire);
            Assert.Contains("hello", wire, StringComparison.OrdinalIgnoreCase);
        }
        request["model"] = "echo/echo";
        var plain = await Send(host, path, request.ToJsonString());
        Assert.DoesNotContain(reasoningField, plain);
        Assert.Empty(host.Requests);
    }

    [Theory]
    [InlineData("accept", "form answer")]
    [InlineData("decline", "decline")]
    [InlineData("cancel", "cancel")]
    public async Task Api_chat_asks_for_MCP_form_and_replays_signed_result_at_frontier(string action, string expected)
    {
        await using var host = await FixtureHost.StartAsync(configure: services =>
        {
            services.AddEcho();
            services.AddSingleton(new ToolApprovalSigner("echo-test-only-signing-key"));
        });
        const string path = "/api/chat";
        var request = GatewayTests.Request(path, InputModel, true);
        var initial = await Send(host, path, request.ToJsonString());
        var call = GatewayTests.Part(initial, "tool-input-available");
        var approval = GatewayTests.Part(initial, "tool-approval-request");
        Assert.Equal("ai_input_required", (string?)call["toolName"]);
        Assert.Equal("form", (string?)call["input"]?["mode"]);
        Assert.Equal("message", (string?)call["input"]?["requestedSchema"]?["required"]?[0]);
        Assert.Equal("tool-calls", (string?)GatewayTests.Part(initial, "finish")["finishReason"]);
        var messages = request["messages"]!.AsArray();
        var invocation = new JsonObject
        {
            ["type"] = "dynamic-tool", ["toolName"] = call["toolName"]!.DeepClone(), ["toolCallId"] = call["toolCallId"]!.DeepClone(),
            ["input"] = call["input"]!.DeepClone(), ["state"] = "output-available", ["providerExecuted"] = false,
            ["approval"] = new JsonObject { ["id"] = approval["approvalId"]!.DeepClone(), ["signature"] = approval["signature"]!.DeepClone(), ["approved"] = true },
            ["output"] = new JsonObject { ["action"] = action, ["content"] = action == "accept" ? new JsonObject { ["message"] = expected } : null }
        };
        messages.Add(new JsonObject { ["id"] = "form", ["role"] = "assistant", ["parts"] = new JsonArray(invocation) });
        var continued = await Send(host, path, request.ToJsonString());
        Assert.Contains(expected, (string?)GatewayTests.Part(continued, "text-delta")["delta"]);
        Assert.DoesNotContain("tool-input-available", continued);

        messages.Add(JsonNode.Parse("""{"id":"next","role":"user","parts":[{"type":"text","text":"new turn"}]}"""));
        var next = await Send(host, path, request.ToJsonString());
        Assert.NotEqual((string?)call["toolCallId"], (string?)GatewayTests.Part(next, "tool-input-available")["toolCallId"]);

        messages.RemoveAt(messages.Count - 1);
        invocation["approval"]!["signature"] = "bad";
        using var tampered = await host.PostAsync(path, request.ToJsonString());
        Assert.Equal(HttpStatusCode.BadRequest, tampered.StatusCode);
        Assert.Empty(host.Requests);
    }

    [Theory]
    [InlineData("/v1/chat/completions", "tool_calls")]
    [InlineData("/v1/responses", "function_call")]
    [InlineData("/v1/messages", "tool_use")]
    public async Task Native_language_protocols_have_form_calls_in_both_modes(string path, string discriminator)
    {
        await using var host = await FixtureHost.StartAsync(configure: services => services.AddEcho());
        foreach (var stream in new[] { false, true })
        {
            var wire = await Send(host, path, GatewayTests.Request(path, InputModel, stream).ToJsonString());
            Assert.Contains(discriminator, wire);
            Assert.Contains("ai_input_required", wire);
            Assert.Contains("requestedSchema", wire);
        }
    }

    [Theory]
    [InlineData("/v1/images/generations", """{"model":"echo/echo-image","prompt":"sample","stream":true}""", "image_generation.completed")]
    [InlineData("/v1/images/edits", """{"model":"echo/echo-image","prompt":"sample","images":[{"image_url":"https://fixture.invalid/ignore.png"}]}""", "iVBORw0KGgo")]
    [InlineData("/api/images", """{"model":"echo/echo-image","prompt":"sample"}""", "iVBORw0KGgo")]
    [InlineData("/api/speech", """{"model":"echo/echo-speech","text":"sample"}""", "UklGR")]
    [InlineData("/v1/audio/speech", """{"model":"echo/echo-speech","input":"sample","response_format":"wav"}""", "RIFF")]
    [InlineData("/api/speech/stream", """{"model":"echo/echo-speech","text":[{"type":"text","text":"sample"}]}""", "UklGR")]
    [InlineData("/api/transcriptions", """{"model":"echo/echo-transcription","audio":"AQID","mediaType":"audio/wav"}""", "Echo transcription fixture")]
    [InlineData("/api/transcriptions/stream", """{"model":"echo/echo-transcription","audio":"AQID","inputAudioFormat":{"type":"wav"}}""", "Echo transcription fixture")]
    [InlineData("/v1/embeddings", """{"model":"echo/echo-embedding","input":["a","b"],"dimensions":4}""", "embedding")]
    [InlineData("/api/embeddings", """{"model":"echo/echo-embedding","values":["a","b"]}""", "embeddings")]
    [InlineData("/api/rerank", """{"model":"echo/echo-rerank","documents":{"type":"text","values":["x","sample"]},"query":"sample","topN":1}""", "relevanceScore")]
    [InlineData("/v1/decisions", """{"model":"echo/echo-decision","input":"x","questions":[{"type":"predicate","name":"fixture","predicate":"x"}]}""", "probability")]
    [InlineData("/api/decisions", """{"model":"echo/echo-decision","input":["x"],"questions":{"fixture":{"type":"boolean"}}}""", "probability")]
    [InlineData("/api/realtime/token/echo/echo-realtime", "{}", "echo_fixture_not_connectable")]
    [InlineData("/api/realtime/session/echo/echo-realtime", """{"sdp":"fixture-offer"}""", "Echo fixture (not connectable)")]
    [InlineData("/v1/realtime/client_secrets", """{"session":{"type":"realtime","model":"echo/echo-realtime"}}""", "echo_fixture_not_connectable")]
    [InlineData("/v1/live/sessions", """{"session":{"model":"echo/echo-realtime"},"transport":{"type":"webrtc","sdp":"fixture-offer"}}""", "echo_fixture_not_connectable")]
    public async Task Fixture_posts_use_native_gateway_contracts_without_keys(string path, string request, string expected)
    {
        await using var host = await FixtureHost.StartAsync(configure: services => services.AddEcho());
        Assert.Contains(expected, await Send(host, path, request));
        Assert.Empty(host.Requests);
    }

    [Fact]
    public async Task Catalog_video_and_skill_fixtures_are_readable_and_upload_does_not_persist()
    {
        await using var host = await FixtureHost.StartAsync(configure: services => services.AddEcho().AddStreaikReferences());
        var catalog = await host.Client.GetFromJsonAsync<JsonObject>("/v1/models");
        var models = catalog!["data"]!.AsArray();
        Assert.Equal(3, models.Count(m => (string?)m?["owned_by"] == "echo" && (string?)m?["type"] == "language"));
        var started = JsonNode.Parse(await Send(host, "/api/videos", """{"model":"echo/echo-video","prompt":"sample"}"""));
        var operation = (string?)started?["operation"];
        Assert.StartsWith("echo/", operation);
        var result = await host.Client.GetFromJsonAsync<JsonObject>($"/api/videos/{operation}");
        Assert.Equal("completed", (string?)result?["status"]);
        Assert.Equal("video/webm", (string?)result?["videos"]?[0]?["mediaType"]);
        var skills = await host.Client.GetStringAsync("/v1/skills");
        Assert.Contains("echo/echo-fixture", skills);
        Assert.Contains("echo-fixture-v1", await host.Client.GetStringAsync("/v1/skills/echo/echo-fixture/versions"));
        var archive = await host.Client.GetByteArrayAsync("/v1/skills/echo/echo-fixture/versions/1/content");
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(archive), "files", "fixture.zip");
        using var upload = new HttpRequestMessage(HttpMethod.Post, "/v1/skills") { Content = form };
        upload.Headers.Add("X-Streaik-Provider", "echo");
        using var response = await host.Client.SendAsync(upload);
        Assert.Contains("no content was stored", await GatewayTests.Success(response));
        Assert.Equal(skills, await host.Client.GetStringAsync("/v1/skills"));
        Assert.Empty(host.Requests);
    }

    [Theory]
    [InlineData(false, "application/json")]
    [InlineData(true, "text/event-stream")]
    public async Task Native_multipart_transcription_can_stream(bool stream, string mediaType)
    {
        await using var host = await FixtureHost.StartAsync(configure: services => services.AddEcho());
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent([1, 2, 3]), "file", "audio.wav");
        form.Add(new StringContent("echo/echo-transcription"), "model");
        if (stream) form.Add(new StringContent("true"), "stream");
        using var response = await host.Client.PostAsync("/v1/audio/transcriptions", form);
        Assert.Contains("Echo transcription fixture", await GatewayTests.Success(response));
        Assert.Equal(mediaType, response.Content.Headers.ContentType?.MediaType);
        Assert.Empty(host.Requests);
    }

    private static async Task<string> Send(FixtureHost host, string path, string json)
    {
        using var response = await host.PostAsync(path, json);
        return await GatewayTests.Success(response);
    }
}
