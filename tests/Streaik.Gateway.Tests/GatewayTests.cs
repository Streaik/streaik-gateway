using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Streaik.Catalog;
using Streaik.Messages;
using Streaik.OpenAI.Models;
using Streaik.Providers;

namespace Streaik.Gateway.Tests;

public sealed class GatewayTests
{
    private const string Chat = "/v1/chat/completions";
    private const string Responses = "/v1/responses";
    private const string Messages = "/v1/messages";
    private const string Vercel = "/api/chat";
    private const string Models = "/v1/models";
    private const string InvalidOption = "Unsupported parameter: reasoning.effort is not supported with this model.";

    [Theory]
    [InlineData("openai", Chat, Chat, false)]
    [InlineData("openai", Chat, Chat, true)]
    [InlineData("openai", Responses, Responses, false)]
    [InlineData("openai", Responses, Responses, true)]
    [InlineData("openai", Messages, Responses, false)]
    [InlineData("openai", Messages, Responses, true)]
    [InlineData("openai", Vercel, Responses, true)]
    [InlineData("anthropic", Chat, Messages, false)]
    [InlineData("anthropic", Chat, Messages, true)]
    [InlineData("anthropic", Responses, Messages, false)]
    [InlineData("anthropic", Responses, Messages, true)]
    [InlineData("anthropic", Messages, Messages, false)]
    [InlineData("anthropic", Messages, Messages, true)]
    [InlineData("anthropic", Vercel, Messages, true)]
    public async Task Matrix_uses_selected_native_protocol_and_preserves_public_id(string provider, string route, string upstream, bool stream)
    {
        await using var host = await FixtureHost.StartAsync();
        var model = $"{provider}/vendor/Model";
        var payload = Request(route, model, stream);
        if (route == upstream) payload["future_request"] = JsonNode.Parse("""{"nested":[1,null,"extra"]}""");
        using var response = await host.PostAsync(route, payload.ToJsonString(), ChatHeaders(provider, ($"X-{provider}-Key", "fixture-key"), ("X-Title", "fixture")));
        var body = await Success(response);
        Assert.Contains("hello", body);
        var record = Assert.Single(host.Requests);
        Assert.Equal(upstream, record.Path);
        Assert.Equal("vendor/Model", record.Body!["model"]!.GetValue<string>());
        Assert.Equal("fixture", record.Headers["X-Title"]);
        Assert.Equal(stream, record.Stream);
        if (upstream == Responses) Assert.False(record.Body["store"]!.GetValue<bool>());
        if (route == upstream)
        {
            Assert.True(JsonNode.DeepEquals(payload["future_request"], record.Body["future_request"]));
            Assert.Contains(route == Messages && stream ? "future_content" : "future_response", body);
        }
        if (!stream) Assert.Equal(model, JsonNode.Parse(body)!["model"]!.GetValue<string>());
        else
        {
            Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
            Assert.Contains(model, body);
            if (route == Chat) Assert.EndsWith("data: [DONE]\n\n", body);
            if (route == Responses) Assert.Contains("event: response.completed\n", body);
            if (route == Messages) Assert.Contains("event: message_stop\n", body);
            if (route == Vercel)
            {
                Assert.Equal("v1", Assert.Single(response.Headers.GetValues("x-vercel-ai-ui-message-stream")));
                Assert.EndsWith("data: [DONE]\n\n", body);
                var finish = Part(body, "finish")["messageMetadata"]!;
                Assert.Equal(model, finish["model"]!.GetValue<string>());
                Assert.Equal(11, finish["usage"]!["totalTokens"]!.GetValue<int>());
                Assert.NotNull(finish["providerMetadata"]![provider]);
            }
        }
    }

    [Fact]
    public async Task Model_listing_pages_and_uses_only_request_scoped_provider_keys()
    {
        await using var host = await FixtureHost.StartAsync();
        using var without = await host.GetAsync(Models, ("Authorization", "Bearer not-for-listing"));
        Assert.Empty(JsonNode.Parse(await Success(without))!["data"]!.AsArray());
        Assert.Empty(host.Requests);

        using var response = await host.GetAsync(Models, ("X-OpenAI-Key", "open"), ("X-Anthropic-Key", "anth"));
        var data = JsonNode.Parse(await Success(response))!["data"]!.AsArray();
        Assert.Equal(new[] { "openai/vendor/Model", "anthropic/vendor/Model", "anthropic/second" },
            data.Select(model => model!["id"]!.GetValue<string>()));
        Assert.Equal(3, host.Requests.Count);
        Assert.Equal("openai", ProviderCatalog.ReadProviderIndex()[0]!["id"]!.GetValue<string>());
        Assert.Equal("OpenAI", ProviderCatalog.ReadProvider("openai")!["name"]!.GetValue<string>());
        Assert.NotEmpty(ProviderCatalog.ReadPricing("anthropic")!);
    }

    [Fact]
    public async Task Header_passthrough_isolated_across_concurrent_native_and_adapted_calls()
    {
        await using var host = await FixtureHost.StartAsync();
        async Task Send(string provider, int index)
        {
            var open = provider == "openai";
            var route = Chat; // Native for OpenAI; adapted for Anthropic.
            using var response = await host.PostAsync(route, Request(route, $"{provider}/vendor/{index}", false).ToJsonString(),
                ("Authorization", $"Bearer key-{provider}-{index}"), ("X-OpenAI-Key", "not-open"), ("X-Anthropic-Key", "not-anth"),
                ("X-OpenAI-Trace", "trace-open"), ("X-Anthropic-Trace", "trace-anth"), ("Anthropic-Beta", "tools-version"), AnthropicVersion,
                ("X-Title", "common"), ("Cookie", "session=private"), ("X-Forwarded-For", "192.0.2.1"),
                ("Connection", $"x-{provider}-drop"), ($"X-{provider}-Drop", "hop-local"));
            await Success(response);
        }
        await Task.WhenAll(Enumerable.Range(0, 6).SelectMany(i => new[] { Send("openai", i), Send("anthropic", i) }));
        Assert.Equal(12, host.Requests.Count);
        foreach (var request in host.Requests)
        {
            var index = request.Body!["model"]!.GetValue<string>().Split('/')[1];
            var h = request.Headers;
            Assert.Equal("common", h["X-Title"]);
            foreach (var blocked in new[] { "Cookie", "Connection", "X-Forwarded-For", "X-OpenAI-Key", "X-Anthropic-Key", "X-OpenAI-Drop", "X-Anthropic-Drop" })
                Assert.False(h.ContainsKey(blocked), blocked);
            if (request.Host == "api.openai.com")
            {
                Assert.Equal($"Bearer key-openai-{index}", h["Authorization"]);
                Assert.Equal("trace-open", h["X-OpenAI-Trace"]);
                Assert.False(h.ContainsKey("X-Anthropic-Trace"));
                Assert.False(h.ContainsKey("X-Api-Key"));
            }
            else
            {
                Assert.Equal($"key-anthropic-{index}", h["X-Api-Key"]);
                Assert.Equal("trace-anth", h["X-Anthropic-Trace"]);
                Assert.Equal("tools-version", h["Anthropic-Beta"]);
                Assert.False(h.ContainsKey("X-OpenAI-Trace"));
                Assert.False(h.ContainsKey("Authorization"));
            }
        }
    }

    [Fact]
    public async Task Native_responses_preserves_unknown_item_and_nested_extensions()
    {
        await using var host = await FixtureHost.StartAsync();
        using var response = await host.PostAsync(Responses, """
            {"model":"openai/vendor/Model","input":[{"type":"future_item","id":"item_x","unrecognized":{"n":4}},
             {"type":"message","role":"user","content":[{"type":"input_text","text":"hello","additional":[true]}]}]}
            """, ("X-OpenAI-Key", "fixture-key"));
        var body = JsonNode.Parse(await Success(response));
        var input = Assert.Single(host.Requests).Body!["input"]!;
        Assert.Equal(4, input[0]!["unrecognized"]!["n"]!.GetValue<int>());
        Assert.True(input[1]!["content"]![0]!["additional"]![0]!.GetValue<bool>());
        Assert.Equal(4, body!["output"]![0]!["content"]![0]!["future_content"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_third_native_messages_provider_can_reuse_both_adapters()
    {
        await using var host = await FixtureHost.StartAsync(configure: services => services.AddProvider<LocalMessages>("fixture"));
        foreach (var route in new[] { Chat, Responses })
        {
            using var response = await host.PostAsync(route, Request(route, "fixture/org/Anything", false).ToJsonString());
            Assert.Contains("org/Anything", await Success(response));
        }
        using var listing = await host.GetAsync(Models);
        Assert.Contains("fixture/org/Anything", await Success(listing));
        Assert.Empty(host.Requests);
    }

    [Fact]
    public async Task Reject_stateful_responses_and_unsupported_translation_before_upstream()
    {
        await using var host = await FixtureHost.StartAsync();
        using var stored = await host.PostAsync(Responses, """{"model":"openai/test","previous_response_id":"resp_old","input":"hello"}""",
            ("X-OpenAI-Key", "test"));
        Assert.Equal(HttpStatusCode.BadRequest, stored.StatusCode);
        using var unsupported = await host.PostAsync(Chat, """{"model":"anthropic/test","messages":[{"role":"user","content":"hello"}],"max_completion_tokens":500,"n":2}""",
            ("X-Anthropic-Key", "test"), AnthropicVersion);
        Assert.Equal(HttpStatusCode.BadRequest, unsupported.StatusCode);
        Assert.Empty(host.Requests);
    }

    [Fact]
    public async Task Missing_credentials_and_upstream_error_have_appropriate_statuses()
    {
        await using var host = await FixtureHost.StartAsync(_ => FixtureHost.Json("""{"error":{"message":"fixture"}}""", HttpStatusCode.TooManyRequests));
        var payload = Request(Vercel, "openai/model", true).ToJsonString();
        using var missing = await host.PostAsync(Vercel, payload);
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        using var rate = await host.PostAsync(Vercel, payload, ("X-OpenAI-Key", "test"));
        Assert.Equal(HttpStatusCode.TooManyRequests, rate.StatusCode);
        Assert.Equal("application/json", rate.Content.Headers.ContentType?.MediaType);
        Assert.False(rate.Headers.Contains("x-vercel-ai-ui-message-stream"));
        Assert.Equal("fixture", JsonNode.Parse(await rate.Content.ReadAsStringAsync())?["error"]?["message"]?.GetValue<string>());
    }

    [Theory]
    [InlineData(Chat, "openai", false)]
    [InlineData(Responses, "openai", true)]
    [InlineData(Vercel, "openai", true)]
    [InlineData(Messages, "anthropic", true)]
    public async Task Provider_rejection_keeps_status_envelope_and_message(string route, string provider, bool stream)
    {
        await using var host = await FixtureHost.StartAsync(_ => FixtureHost.Json(
            $$$"""{"error":{"message":"{{{InvalidOption}}}","type":"invalid_request_error","debug":"not for clients"}}""", HttpStatusCode.BadRequest));
        using var response = await host.PostAsync(route, Request(route, $"{provider}/model", stream).ToJsonString(),
            ChatHeaders(provider, ($"X-{provider}-Key", "fixture-key")));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.False(response.Headers.Contains("x-vercel-ai-ui-message-stream"));
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonNode.Parse(body)!;
        Assert.Equal(route == Messages ? "error" : null, json["type"]?.GetValue<string>());
        Assert.Equal("invalid_request_error", json["error"]?["type"]?.GetValue<string>());
        Assert.Equal(InvalidOption, json["error"]?["message"]?.GetValue<string>());
        Assert.DoesNotContain("not for clients", body);
    }

    [Theory]
    [InlineData("<html>private contents</html>", "The upstream provider rejected the request.")]
    [InlineData("""{"error":{"message":"\uD800"}}""", "The upstream provider rejected the request.")]
    [InlineData("""{"error":{"message":"API key fixture-secret-123 is invalid."}}""", "API key [redacted] is invalid.")]
    public async Task Provider_error_bodies_do_not_expose_raw_contents_or_credentials(string upstream, string expected)
    {
        await using var host = await FixtureHost.StartAsync(_ => FixtureHost.Json(upstream, HttpStatusCode.Unauthorized));
        using var response = await host.PostAsync(Vercel, Request(Vercel, "openai/model", true).ToJsonString(), ("X-OpenAI-Key", "fixture-secret-123"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(expected, JsonNode.Parse(body)?["error"]?["message"]?.GetValue<string>());
        Assert.DoesNotContain("fixture-secret-123", body);
    }

    [Theory]
    [InlineData("error", false)]
    [InlineData("error", true)]
    [InlineData("response.failed", true)]
    public async Task Api_chat_reports_responses_stream_errors_after_partial_output(string type, bool nested)
    {
        var error = type == "response.failed"
            ? $$$$"""{"type":"response.failed","sequence_number":3,"response":{"id":"resp_1","model":"model","status":"failed","output":[],"error":{"message":"{{{{InvalidOption}}}}"}}}"""
            : nested ? $$$"""{"type":"error","sequence_number":3,"error":{"message":"{{{InvalidOption}}}"}}"""
                : $$"""{"type":"error","sequence_number":3,"message":"{{InvalidOption}}"}""";
        await using var host = await FixtureHost.StartAsync(_ => FixtureHost.Sse(
            """{"type":"response.created","sequence_number":0,"response":{"id":"resp_1","model":"model","status":"in_progress","output":[]}}""",
            """{"type":"response.output_text.delta","sequence_number":1,"item_id":"msg_r","content_index":0,"output_index":0,"delta":"partial reply"}""", error));
        using var response = await host.PostAsync(Vercel, Request(Vercel, "openai/model", true).ToJsonString(), ("X-OpenAI-Key", "fixture-key"));
        var sse = await Success(response);
        Assert.Contains("partial reply", sse);
        Assert.Equal(InvalidOption, Part(sse, "error")["errorText"]?.GetValue<string>());
        Assert.DoesNotContain("\"type\":\"finish\"", sse);
        Assert.EndsWith("data: [DONE]\n\n", sse);
    }

    [Fact]
    public async Task Interrupted_messages_stream_does_not_report_a_successful_vercel_finish()
    {
        await using var host = await FixtureHost.StartAsync(_ => FixtureHost.Sse(FixtureHost.MessageStart));
        using var response = await host.PostAsync(Vercel, Request(Vercel, "anthropic/test", true).ToJsonString(), ("X-Anthropic-Key", "test"), AnthropicVersion);
        var sse = await Success(response);
        Assert.NotNull(Part(sse, "error")["errorText"]);
        Assert.DoesNotContain("\"type\":\"finish\"", sse);
        Assert.EndsWith("data: [DONE]\n\n", sse);
    }

    [Theory]
    [InlineData(Chat, "tool_calls")]
    [InlineData(Responses, "response.function_call_arguments.delta")]
    public async Task Messages_client_tool_deltas_reach_both_openai_protocols(string route, string expected)
    {
        await using var host = await FixtureHost.StartAsync(_ => FixtureHost.Sse(FixtureHost.MessageStart,
            """{"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"call_fixture","name":"lookup","input":{}}}""",
            """{"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{\"number\":8}"}}""",
            """{"type":"content_block_stop","index":0}""",
            """{"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":3}}""", FixtureHost.MessageStop));
        using var response = await host.PostAsync(route, Request(route, "anthropic/test", true).ToJsonString(), ("X-Anthropic-Key", "test"), AnthropicVersion);
        var sse = await Success(response);
        Assert.Contains("call_fixture", sse);
        Assert.Contains(expected, sse);
        Assert.Contains("number", sse);
        Assert.DoesNotContain("upstream stream ended", sse);
    }

    [Fact]
    public async Task Responses_final_tool_arguments_without_deltas_are_sent_once_to_messages()
    {
        await using var host = await FixtureHost.StartAsync(_ => FixtureHost.Sse(
            """{"type":"response.created","response":{"id":"resp_f","status":"in_progress","model":"native","output":[]}}""",
            """{"type":"response.output_item.added","output_index":0,"item":{"type":"function_call","id":"fc_1","call_id":"call_f","name":"lookup","arguments":""}}""",
            """{"type":"response.output_item.done","output_index":0,"item":{"type":"function_call","id":"fc_1","call_id":"call_f","name":"lookup","arguments":"{\"number\":8}"}}""",
            """{"type":"response.completed","response":{"id":"resp_f","status":"completed","model":"native","output":[]}}"""));
        using var response = await host.PostAsync(Messages, Request(Messages, "openai/test", true).ToJsonString(), ("X-OpenAI-Key", "test"));
        var sse = await Success(response);
        Assert.Contains("call_f", sse);
        Assert.Contains("number", sse);
        Assert.Contains("event: message_stop", sse);
        Assert.Equal(1, sse.Split('\n').Count(line => line == "event: content_block_delta"));
    }

    private sealed class LocalMessages : IMessagesProvider, IModelListingProvider
    {
        public Task<MessagesResponse> CreateMessageAsync(MessagesRequest request, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
            => Task.FromResult(new MessagesResponse { Id = "local", Content = [new MessageContentBlock { Type = "text", Text = request.Model }], StopReason = "end_turn" });

        public IAsyncEnumerable<MessageStreamPart> StreamMessageAsync(MessagesRequest request, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
            => throw new InvalidOperationException("Unexpected stream.");

        public async IAsyncEnumerable<Model> ListModelsAsync([EnumeratorCancellation] CancellationToken ct)
        { await Task.CompletedTask; yield return new Model { Id = "org/Anything" }; }
    }

    internal static readonly (string, string) AnthropicVersion = ("anthropic-version", "2026-10-21");

    internal static (string, string)[] ChatHeaders(string provider, params (string, string)[] headers)
        => provider == "anthropic" ? [.. headers, AnthropicVersion] : headers;

    internal static JsonObject Request(string route, string model, bool stream)
    {
        var body = route switch
        {
            Responses => new JsonObject { ["input"] = "hello" },
            Vercel => JsonNode.Parse("""{"messages":[{"id":"user_1","role":"user","parts":[{"type":"text","text":"hello"}]}]}""")!.AsObject(),
            _ => JsonNode.Parse("""{"messages":[{"role":"user","content":"hello"}]}""")!.AsObject()
        };
        if (route == Messages) body["max_tokens"] = 500;
        else if (model.StartsWith("anthropic/", StringComparison.Ordinal))
            body[route == Vercel ? "maxOutputTokens" : route == Responses ? "max_output_tokens" : "max_completion_tokens"] = 500;
        if (route != Vercel) body["stream"] = stream;
        body["model"] = model;
        return body;
    }

    internal static async Task<string> Success(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");
        return body;
    }

    internal static JsonNode Part(string sse, string type)
        => sse.Split('\n').Where(line => line.StartsWith("data: {", StringComparison.Ordinal)).Select(line => JsonNode.Parse(line[6..])!)
            .FirstOrDefault(item => item["type"]?.GetValue<string>() == type)
            ?? throw new InvalidOperationException($"No {type} in stream: {sse}");
}
