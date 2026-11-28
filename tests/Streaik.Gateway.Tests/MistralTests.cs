using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Streaik.Tools;
using Xunit;

namespace Streaik.Gateway.Tests;

public sealed class MistralTests
{
    private static readonly (string Name, string Value) Key = ("X-Mistral-Key", "fixture-mistral");

    [Fact]
    public async Task ChatAndResponsesUseTheirOwnNativeRoutes()
    {
        await using var host = await FixtureHost.StartAsync(request => request.Path switch
        {
            "/v1/chat/completions" when request.Stream => FixtureHost.Sse(
                """{"data":{"id":"cmpl_st","object":"chat.completion.chunk","created":1,"model":"mistral-small-latest","choices":[{"index":0,"delta":{"content":[{"type":"thinking","thinking":[{"type":"text","text":"Planning"}]},{"type":"text","text":"Bonjour"}]},"finish_reason":null}]}}""",
                """{"id":"cmpl_st","object":"chat.completion.chunk","created":1,"model":"mistral-small-latest","choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":4,"completion_tokens":2,"total_tokens":6}}""", "[DONE]"),
            "/v1/chat/completions" => FixtureHost.Json("""
                {"id":"cmpl_1","object":"chat.completion","created":1,"model":"mistral-small-latest","choices":[{"index":0,"message":{"role":"assistant","content":[{"type":"text","text":"Bonjour"}]},"finish_reason":"stop"}]}
                """),
            "/v1/responses" => FixtureHost.Reply(request),
            _ => throw new InvalidOperationException(request.Path)
        }, mistral: true);

        using var chat = await host.PostAsync("/v1/chat/completions", """
            {"model":"mistral/mistral-small-latest","messages":[{"role":"developer","content":"Be concise"},{"role":"user","content":"hello"}],
             "seed":7,"max_completion_tokens":12,"stream_options":{"include_usage":true},
             "response_format":{"type":"json_schema","json_schema":{"name":"test","schema":{"type":"object"}}}}
            """, Key, ("X-OpenAI-Key", "not-forwarded"));
        Assert.Equal("Bonjour", (string?)(await Json(chat))["choices"]![0]!["message"]!["content"]);
        var nativeChat = host.Requests.Last();
        Assert.Equal("mistral-small-latest", (string?)nativeChat.Body!["model"]);
        Assert.Equal(7, (int?)nativeChat.Body["random_seed"]);
        Assert.Equal(12, (int?)nativeChat.Body["max_tokens"]);
        Assert.Equal("system", (string?)nativeChat.Body["messages"]![0]!["role"]);
        Assert.Equal("object", (string?)nativeChat.Body["response_format"]!["json_schema"]!["schema_definition"]!["type"]);
        Assert.Null(nativeChat.Body["stream_options"]);
        Assert.Equal("Bearer " + Key.Value, nativeChat.Headers["Authorization"]);
        Assert.DoesNotContain("X-OpenAI-Key", nativeChat.Headers.Keys);

        using var vercel = await host.PostAsync("/api/chat", """
            {"model":"mistral/mistral-small-latest","messages":[{"id":"u","role":"user","parts":[{"type":"text","text":"hello"}]}]}
            """, Key);
        var wire = await GatewayTests.Success(vercel);
        Assert.Contains("Bonjour", wire);
        Assert.Contains("Planning", wire);
        Assert.Equal("/v1/chat/completions", host.Requests.Last().Path);

        const string replay = """
            {"model":"mistral/mistral-small-latest","instructions":"Use the lookup result","input":[{"type":"message","role":"user","content":"hello"},
             {"type":"function_call","id":"fc_1","call_id":"call_1","name":"lookup","arguments":"{}"},
             {"type":"function_call_output","call_id":"call_1","output":"France"}],"tools":[{"type":"function","name":"lookup","parameters":{"type":"object"}}]}
            """;
        using var response = await host.PostAsync("/v1/responses", replay, Key);
        Assert.Equal("resp_1", (string?)(await Json(response))["id"]);
        Assert.Equal("/v1/responses", host.Requests.Last().Path);
        Assert.Equal("France", (string?)host.Requests.Last().Body!["input"]![2]!["output"]);
        var streaming = JsonNode.Parse(replay)!;
        streaming["stream"] = true;
        using var sse = await host.PostAsync("/v1/responses", streaming.ToJsonString(), Key);
        Assert.Contains("response.completed", await GatewayTests.Success(sse));
        Assert.True(host.Requests.Last().Stream);

        streaming["previous_response_id"] = "resp_stored";
        var before = host.Requests.Count;
        using var invalid = await host.PostAsync("/v1/responses", streaming.ToJsonString(), Key);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(before, host.Requests.Count);
    }

    [Fact]
    public async Task VercelChatReplaysAssistantThinkingInMistralChunkFormat()
    {
        var expected = JsonNode.Parse("""
            [{"type":"thinking","thinking":[{"type":"text","text":"Planning"}]},{"type":"text","text":"Bonjour"}]
            """);
        await using var host = await FixtureHost.StartAsync(request =>
        {
            Assert.Equal("/v1/chat/completions", request.Path);
            var messages = request.Body!["messages"]!.AsArray();
            if (messages.Count == 1)
                return FixtureHost.Sse(
                    """{"id":"cmpl_1","object":"chat.completion.chunk","created":1,"model":"mistral-large-latest","choices":[{"index":0,"delta":{"content":[{"type":"thinking","thinking":[{"type":"text","text":"Planning"}]},{"type":"text","text":"Bonjour"}]},"finish_reason":"stop"}]}""",
                    "[DONE]");

            // Reject the string-shaped thinking chunk that used to reach Mistral on turn two.
            if (!JsonNode.DeepEquals(expected, messages[1]!["content"]))
                return FixtureHost.Json("""{"message":"Expected typed thinking chunks."}""", HttpStatusCode.UnprocessableEntity);
            return FixtureHost.Reply(request);
        }, mistral: true);

        const string user = """{"id":"u1","role":"user","parts":[{"type":"text","text":"First turn"}]}""";
        using var first = await host.PostAsync("/api/chat", $$"""
            {"model":"mistral/mistral-large-latest","messages":[{{user}}]}
            """, Key);
        var wire = await GatewayTests.Success(first);
        var metadata = GatewayTests.Part(wire, "reasoning-end")["providerMetadata"];
        Assert.NotNull(metadata);
        using var followUp = await host.PostAsync("/api/chat", $$"""
            {"model":"mistral/mistral-large-latest","messages":[{{user}},
             {"id":"a1","role":"assistant","parts":[{"type":"reasoning","text":"Planning","providerMetadata":{{metadata}}},{"type":"text","text":"Bonjour"}]},
             {"id":"u2","role":"user","parts":[{"type":"text","text":"Follow-up"}]}]}
            """, Key);
        Assert.Equal("hello", (string?)GatewayTests.Part(await GatewayTests.Success(followUp), "text-delta")["delta"]);
        var replay = host.Requests.Last().Body!["messages"]!.AsArray();
        Assert.Equal(3, replay.Count);
        Assert.Equal("Follow-up", (string?)replay[2]!["content"]);
    }

    [Fact]
    public async Task ChatPreservesAlreadyNativeThinkingChunks()
    {
        await using var host = await FixtureHost.StartAsync(FixtureHost.Reply, mistral: true);
        const string thinking = """{"type":"thinking","closed":true,"signature":"fixture-signature","thinking":[{"type":"text","text":"Planning"}]}""";
        using var response = await host.PostAsync("/v1/chat/completions", $$"""
            {"model":"mistral/mistral-large-latest","messages":[{"role":"user","content":"Hi"},
             {"role":"assistant","content":[{{thinking}},{"type":"text","text":"Bonjour"}]},{"role":"user","content":"Hello again"}]}
            """, Key);
        await GatewayTests.Success(response);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(thinking), Assert.Single(host.Requests).Body!["messages"]![1]!["content"]![0]));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ResponsesPreservesCompletionAndUsageWithDecimalTimestamps(bool stream)
    {
        const string message = """
            {"id":"msg_1","type":"message","role":"assistant","status":"completed","content":[{"type":"output_text","text":"Hello","annotations":[],"logprobs":null}]}
            """;
        const string reasoning = """
            {"id":"rs_1","type":"reasoning","status":"completed","summary":[],"content":[{"type":"reasoning_text","text":"Planning"}],"encrypted_content":null}
            """;
        const string completed = $$"""
            {"id":"resp_1","object":"response","model":"mistral-large-latest","status":"completed","created_at":1795852932.0,"completed_at":1795852935.0,
             "output":[{{reasoning}},{{message}}],"usage":{"input_tokens":20,"output_tokens":322,"total_tokens":342,"output_tokens_details":{"reasoning_tokens":317} } }
            """;
        await using var host = await FixtureHost.StartAsync(request => request.Stream ? FixtureHost.Sse(
            """{"type":"response.created","sequence_number":0,"response":{"id":"resp_1","object":"response","model":"mistral-large-latest","status":"in_progress","created_at":1795852932.0,"completed_at":null,"output":[]}}""",
            """{"type":"response.output_text.delta","sequence_number":1,"item_id":"msg_1","content_index":0,"output_index":1,"delta":"Hello"}""",
            $$"""{"type":"response.output_item.done","sequence_number":2,"output_index":0,"item":{{reasoning}}}""",
            $$"""{"type":"response.output_item.done","sequence_number":3,"output_index":1,"item":{{message}}}""",
            $$"""{"type":"response.completed","sequence_number":4,"response":{{completed.ReplaceLineEndings(" ")}}}""")
            : FixtureHost.Json(completed), mistral: true);
        using var response = await host.PostAsync("/v1/responses", $$"""
            {"model":"mistral/mistral-large-latest","input":"Hello","stream":{{(stream ? "true" : "false")}}}
            """, Key);

        var wire = await GatewayTests.Success(response);
        JsonNode result;
        if (stream)
        {
            Assert.DoesNotContain("event: error", wire);
            Assert.Equal("Hello", (string?)GatewayTests.Part(wire, "response.output_text.delta")["delta"]);
            var final = GatewayTests.Part(wire, "response.completed");
            Assert.Equal(4, (int?)final["sequence_number"]);
            result = final["response"]!;
        }
        else result = JsonNode.Parse(wire)!;
        Assert.Equal(1795852932L, (long?)result["created_at"]);
        Assert.Equal(1795852935L, (long?)result["completed_at"]);
        Assert.Equal(342, (int?)result["usage"]!["total_tokens"]);
        Assert.Equal(317, (int?)result["usage"]!["output_tokens_details"]!["reasoning_tokens"]);
        Assert.Equal("reasoning_text", (string?)result["output"]![0]!["content"]![0]!["type"]);
        Assert.Equal("Hello", (string?)result["output"]![1]!["content"]![0]!["text"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResponsesStillRejectsStreamsWithoutResponseLevelTerminalEvent(bool doneSentinel)
    {
        const string itemDone = """
            {"type":"response.output_item.done","sequence_number":1,"output_index":0,"item":{"id":"msg_1","type":"message","role":"assistant","status":"completed","content":[{"type":"output_text","text":"Partial","annotations":[]}]}}
            """;
        await using var host = await FixtureHost.StartAsync(_ => doneSentinel ? FixtureHost.Sse(itemDone, "[DONE]") : FixtureHost.Sse(itemDone), mistral: true);
        using var response = await host.PostAsync("/v1/responses", """
            {"model":"mistral/mistral-large-latest","input":"Hello","stream":true}
            """, Key);
        var wire = await GatewayTests.Success(response);
        Assert.Contains("event: response.output_item.done", wire);
        Assert.DoesNotContain("event: response.completed", wire);
        Assert.Equal("upstream_error", (string?)GatewayTests.Part(wire, "error")["code"]);
    }

    [Fact]
    public async Task ResponsesPreservesExplicitFailureWithDecimalTimestamp()
    {
        await using var host = await FixtureHost.StartAsync(_ => FixtureHost.Sse("""
            {"type":"response.failed","sequence_number":1,"response":{"id":"resp_1","model":"mistral-large-latest","status":"failed","created_at":1795852932.0,"completed_at":1795852935.0,"output":[],"error":{"code":"server_error","message":"Generation failed."}}}
            """), mistral: true);
        using var response = await host.PostAsync("/v1/responses", """
            {"model":"mistral/mistral-large-latest","input":"Hello","stream":true}
            """, Key);
        var wire = await GatewayTests.Success(response);
        Assert.DoesNotContain("event: response.completed", wire);
        var result = GatewayTests.Part(wire, "response.failed")["response"]!;
        Assert.Equal("server_error", (string?)result["error"]!["code"]);
        Assert.Equal("Generation failed.", (string?)result["error"]!["message"]);
    }

    [Fact]
    public async Task HostedVercelChatStillUsesChatCompletions()
    {
        await using var host = await FixtureHost.StartAsync(request =>
        {
            Assert.Equal("/v1/chat/completions", request.Path);
            return FixtureHost.Reply(request);
        }, services =>
        {
            services.AddStreaikHostedTools();
            services.AddSingleton(new HostedToolAccess((_, _, _, _) => throw new InvalidOperationException("The deferred tool must not be accessed.")));
        }, mistral: true);
        using var response = await host.PostAsync("/api/chat", """
            {"model":"mistral/mistral-small-latest","id":"chat","messages":[{"id":"u","role":"user","parts":[{"type":"text","text":"hello"}]}],
             "tools":[{"name":"mcp","inputSchema":{"type":"object","mcp":{"server_label":"inventory","server_url":"https://inventory.example.test/mcp","defer_loading":true}}}]}
            """, Key);
        Assert.Contains("hello", await GatewayTests.Success(response));
        Assert.Single(host.Requests);
    }

    [Fact]
    public async Task OcrUsesLatestUserAttachmentWithoutFetchingItsUrl()
    {
        const string url = "https://documents.example/report.pdf?sig=one%2Btwo";
        await using var host = await FixtureHost.StartAsync(request =>
        {
            Assert.Equal("api.mistral.ai", request.Host);
            Assert.Equal("/v1/ocr", request.Path);
            Assert.Equal(url, (string?)request.Body!["document"]!["document_url"]);
            return FixtureHost.Json("""{"pages":[{"header":"Title","markdown":"# Page one"},{"markdown":"Page two","footer":"End"}],"usage_info":{"pages_processed":2}}""");
        }, mistral: true);
        using var chat = await host.PostAsync("/api/chat", $$"""
            {"model":"mistral/mistral-ocr-latest","messages":[{"id":"u","role":"user","parts":[{"type":"file","mediaType":"application/pdf","url":"{{url}}"}]}]}
            """, Key);
        Assert.Contains("# Page one\\n\\nPage two", await GatewayTests.Success(chat));
        using var response = await host.PostAsync("/v1/responses", $$"""
            {"model":"mistral/mistral-ocr-latest","input":[{"role":"user","content":[{"type":"input_file","file_url":"{{url}}"}]}]}
            """, Key);
        Assert.Equal("Title\n\n# Page one\n\nPage two\n\nEnd", (string?)(await Json(response))["output"]![0]!["content"]![0]!["text"]);
        using var stream = await host.PostAsync("/v1/responses", $$"""
            {"model":"mistral/mistral-ocr-latest","stream":true,"input":[{"role":"user","content":[{"type":"input_file","file_url":"{{url}}"}]}]}
            """, Key);
        Assert.Contains("response.completed", await GatewayTests.Success(stream));
        Assert.Equal(3, host.Requests.Count);
    }

    [Fact]
    public async Task SpeechTranscriptionAndEmbeddingsUseNativeMediaEndpoints()
    {
        await using var host = await FixtureHost.StartAsync(request => request.Path switch
        {
            "/v1/audio/voices?limit=100&offset=0" => FixtureHost.Json("""{"items":[{"id":"voice-id","slug":"alice"}],"total":1}"""),
            "/v1/audio/speech" when request.Stream => FixtureHost.Sse("""{"type":"speech.audio.delta","audio_data":"AQID"}""",
                """{"type":"speech.audio.done","usage":{"prompt_tokens":4}}"""),
            "/v1/audio/speech" => FixtureHost.Json("""{"audio_data":"AQID","audio_format":"wav"}"""),
            "/v1/audio/transcriptions" when request.Stream => FixtureHost.Sse("""{"type":"transcription.text.delta","text":"hello"}""",
                """{"type":"transcription.segment.delta","text":"hello","start":0,"end":1,"speaker_id":"A"}""",
                """{"type":"transcription.done","text":"hello","language":"en","usage":{"prompt_audio_seconds":1}}"""),
            "/v1/audio/transcriptions" => FixtureHost.Json("""{"text":"hello","language":"en","segments":[{"text":"hello","start":0,"end":1,"speaker_id":"A"}]}"""),
            "/v1/embeddings" => FixtureHost.Json("""{"object":"list","model":"mistral-embed","data":[{"index":0,"embedding":[0.5,0.25]}],"usage":{"prompt_tokens":1,"total_tokens":1}}"""),
            _ => throw new InvalidOperationException(request.Path)
        }, mistral: true);
        using var speech = await host.PostAsync("/v1/audio/speech", """
            {"model":"mistral/voxtral-mini-tts-3-0/alice","input":"bonjour","response_format":"wav"}
            """, Key);
        speech.EnsureSuccessStatusCode();
        Assert.Equal("audio/wav", speech.Content.Headers.ContentType!.MediaType);
        Assert.Equal(new byte[] { 1, 2, 3 }, await speech.Content.ReadAsByteArrayAsync());
        Assert.Equal("voice-id", (string?)host.Requests.Last().Body!["voice_id"]);

        using var stream = await host.PostAsync("/api/speech/stream", """
            {"model":"mistral/voxtral-mini-tts-3-0","text":[{"type":"text","text":"bon"},{"type":"text","text":"jour"}],"includeRawChunks":true}
            """, Key);
        Assert.Contains("AQID", await GatewayTests.Success(stream));
        Assert.Equal("bonjour", (string?)host.Requests.Last().Body!["input"]);

        using var transcription = await host.PostAsync("/api/transcriptions/stream", """
            {"model":"mistral/voxtral-mini-transcribe-3-0","audio":"AQID","inputAudioFormat":{"type":"audio/wav"},
             "providerOptions":{"mistral":{"diarize":true,"timestamp_granularities":["segment"],"context_bias":["one","two"]}}}
            """, Key);
        var text = await GatewayTests.Success(transcription);
        var finish = GatewayTests.Part(text, "finish");
        Assert.Equal("hello", (string?)finish["text"]);
        Assert.Equal("A", (string?)finish["providerMetadata"]!["mistral"]!["segments"]![0]!["speaker_id"]);
        Assert.Contains("context_bias", host.Requests.Last().RawBody);

        using var embedding = await host.PostAsync("/api/embeddings", """{"model":"mistral/mistral-embed","values":["hello"]}""", Key);
        Assert.Equal(0.5, (double?)(await Json(embedding))["embeddings"]![0]![0]);
        Assert.Equal("/v1/embeddings", host.Requests.Last().Path);
    }

    [Fact]
    public async Task ListModelsAndMintOnlyShortLivedRealtimeSecrets()
    {
        var token = "rt_fixture";
        var expires = DateTimeOffset.UtcNow.AddMinutes(2);
        await using var host = await FixtureHost.StartAsync(request => request.Path switch
        {
            "/v1/models" => FixtureHost.Json("""
                {"data":[{"id":"mistral-small-latest","max_context_length":128000},{"id":"mistral-ocr-latest"},
                 {"id":"voxtral-mini-transcribe-realtime-3-0"},{"id":"voxtral-mini-tts-3-0"},{"id":"agent/old"}]}
                """),
            "/v1/audio/voices?limit=100&offset=0" => FixtureHost.Json("""{"items":[{"id":"voice-id","slug":"alice"}],"total":1}"""),
            "/v1/client/sessions" => FixtureHost.Json(JsonSerializer.Serialize(new { client_secret = new { value = token, expires_at = expires } })),
            _ => throw new InvalidOperationException(request.Path)
        }, mistral: true);
        using var models = await host.GetAsync("/v1/models", Key);
        var modelBody = await GatewayTests.Success(models);
        Assert.Contains("mistral/mistral-ocr-latest", modelBody);
        Assert.Contains("mistral/voxtral-mini-tts-3-0/alice", modelBody);
        Assert.Contains("mistral/voxtral-mini-transcribe-realtime-3-0", modelBody);
        Assert.DoesNotContain("agent/old", modelBody);

        const string endpoint = "/api/realtime/token/mistral/voxtral-mini-transcribe-realtime-3-0";
        using var tokenResponse = await host.PostAsync(endpoint, """{"sessionConfig":{"providerOptions":{"mistral":{"ttl_seconds":60}}}}""", Key);
        var result = await Json(tokenResponse);
        Assert.Equal(token, (string?)result["token"]);
        Assert.Contains("/v1/audio/transcriptions/realtime?model=voxtral-mini-transcribe-realtime-3-0", (string?)result["url"]);
        Assert.True(tokenResponse.Headers.CacheControl?.NoStore);
        Assert.Equal("realtime", (string?)host.Requests.Last().Body!["purpose"]);
        Assert.Equal(60, (int?)host.Requests.Last().Body!["ttl_seconds"]);

        using var voice = await host.PostAsync("/api/realtime/token/mistral/voxtral-mini-tts-3-0", "{}", Key);
        Assert.Contains("/v1/audio/speech/realtime?model=", (string?)(await Json(voice))["url"]);
        expires = DateTimeOffset.UtcNow.AddMinutes(-1);
        using var expired = await host.PostAsync(endpoint, "{}", Key);
        Assert.Equal(HttpStatusCode.BadGateway, expired.StatusCode);
        Assert.DoesNotContain(token, await expired.Content.ReadAsStringAsync());
    }

    private static async Task<JsonNode> Json(HttpResponseMessage response) => JsonNode.Parse(await GatewayTests.Success(response))!;
}
