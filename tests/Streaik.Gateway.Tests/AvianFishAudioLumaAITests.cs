using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Streaik.Gateway.Tests;

public sealed class AvianFishAudioLumaAITests
{
    private static readonly (string, string) Avian = ("X-Avian-Key", "avian-fixture");
    private static readonly (string, string) Fish = ("X-Fishaudio-Key", "fish-fixture");
    private static readonly (string, string) Luma = ("X-Lumaai-Key", "luma-fixture");

    [Fact]
    public async Task Catalogs_combine_static_models_and_paginated_live_agents()
    {
        await using var host = await FixtureHost.StartAsync(request => request.Path switch
        {
            "/v1/agent/agents?include_archived=false&publication_state=live&page_size=100" => FixtureHost.Json("""
                {"agents":[{"agent_id":"first","name":"First","status":"active","publication_state":"live"},
                           {"agent_id":"inactive","status":"paused","publication_state":"live"}],"has_more":true,"next_cursor":"a+b"}
                """),
            "/v1/agent/agents?include_archived=false&publication_state=live&page_size=100&cursor=a%2Bb" => FixtureHost.Json("""
                {"agents":[{"agent_id":"first","status":"active","publication_state":"live"},
                           {"agent_id":"second","status":"active","publication_state":"live","overrides_allowed":["voice"]}],"has_more":false}
                """),
            _ => throw new InvalidOperationException(request.Path)
        }, avianFishAudioLumaAI: true);
        using var anonymous = await host.GetAsync("/v1/models");
        Assert.Empty((await Body(anonymous))["data"]!.AsArray());
        Assert.Empty(host.Requests);
        using var response = await host.GetAsync("/v1/models", Avian, Fish, Luma);
        var models = (await Body(response))["data"]!.AsArray().ToDictionary(x => (string)x!["id"]!);
        Assert.Contains(models.Keys, id => id.StartsWith("avian/", StringComparison.Ordinal));
        Assert.Equal("speech", (string?)models["fishaudio/s2.1-pro"]!["type"]);
        Assert.Equal("transcription", (string?)models["fishaudio/transcribe-1-pro"]!["type"]);
        Assert.Contains(models["fishaudio/agents/second"]!["tags"]!.AsArray(), tag => (string?)tag == "realtime");
        Assert.False(models.ContainsKey("fishaudio/agents/inactive"));
        Assert.Equal("image", (string?)models["lumaai/uni-1-max"]!["type"]);
        Assert.Equal("video", (string?)models["lumaai/ray-3.2"]!["type"]);
        Assert.Equal(2, host.Requests.Count);
        Assert.All(host.Requests, r => Assert.Equal("Bearer fish-fixture", r.Headers["Authorization"]));
    }

    [Theory]
    [InlineData("/v1/chat/completions", false)]
    [InlineData("/v1/chat/completions", true)]
    [InlineData("/v1/responses", false)]
    [InlineData("/v1/messages", true)]
    [InlineData("/api/chat", true)]
    public async Task Avian_preserves_native_options_and_metadata_through_chat_bridges(string route, bool stream)
    {
        await using var host = await FixtureHost.StartAsync(request => request.Stream
            ? FixtureHost.Sse("""
                {"id":"req","object":"chat.completion.chunk","created":1,"model":"vendor/model","choices":[{"index":0,"delta":{"role":"assistant","content":"Avian says hi"},"finish_reason":null}],"avian_native":{"values":[null,7]}}
                """, """
                {"id":"req","object":"chat.completion.chunk","created":1,"model":"vendor/model","choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":2,"total_tokens":3}}
                """, "[DONE]")
            : FixtureHost.Json("""
                {"id":"req","object":"chat.completion","created":1,"model":"vendor/model","choices":[{"index":0,"message":{"role":"assistant","content":"Avian says hi"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":2,"total_tokens":3},"avian_native":{"values":[null,7]}}
                """), avianFishAudioLumaAI: true);
        const string options = """{"nested":{"values":[true,null]},"model":"forged","stream":false,"authorization":"forged"}""";
        var turn = route switch
        {
            "/v1/responses" => "\"input\":\"hello\"",
            "/v1/messages" => "\"max_tokens\":128,\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}]",
            "/api/chat" => "\"messages\":[{\"id\":\"user-1\",\"role\":\"user\",\"parts\":[{\"type\":\"text\",\"text\":\"hello\"}]}]",
            _ => "\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}]"
        };
        var native = route == "/api/chat" ? "\"providerMetadata\":{\"avian\":" + options + "}" : "\"avian\":" + options;
        using var response = await host.PostAsync(route,
            "{\"model\":\"avian/vendor/model\",\"stream\":" + (stream ? "true" : "false") + "," + turn + "," + native + "}", Avian);
        var output = await GatewayTests.Success(response);
        Assert.Contains("Avian says hi", output);
        Assert.Contains("avian_native", output);
        var sent = Assert.Single(host.Requests);
        Assert.Equal("api.avian.io", sent.Host);
        Assert.Equal("/v1/chat/completions", sent.Path);
        Assert.Equal("Bearer avian-fixture", sent.Headers["Authorization"]);
        Assert.Equal("vendor/model", (string?)sent.Body!["model"]);
        Assert.Null(sent.Body["authorization"]);
        Assert.Equal(stream, sent.Stream);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"values":[true,null]}"""), sent.Body["nested"]));
    }

    [Fact]
    public async Task Fish_speech_maps_options_without_losing_binary_audio_or_headers()
    {
        await using var host = await FixtureHost.StartAsync(_ =>
        {
            var reply = Binary("audio/mpeg");
            reply.Headers.Add("X-Request-Id", "fish-request");
            reply.Headers.Add("Set-Cookie", "sensitive=1");
            return reply;
        }, avianFishAudioLumaAI: true);
        using var response = await host.PostAsync("/api/speech", """
            {"model":"fishaudio/s2.1-pro","text":"Hello","voice":"voice-id","speed":1.2,"language":"en",
             "providerOptions":{"fishaudio":{"prosody":{"pitch":3},"pronunciation_dictionary":{"word":[1,null]},"model":"wrong"}}}
            """, Fish);
        var result = await Body(response);
        Assert.Equal("AQID", (string?)result["audio"]!["base64"]);
        var headers = result["providerMetadata"]!["fishaudio"]!["headers"]!;
        Assert.Equal("fish-request", (string?)headers["x-request-id"]);
        Assert.Null(headers["set-cookie"]);
        var sent = Assert.Single(host.Requests);
        Assert.Equal("/v1/tts", sent.Path);
        Assert.Equal("Bearer fish-fixture", sent.Headers["Authorization"]);
        Assert.Equal("s2.1-pro", sent.Headers["model"]);
        Assert.Equal("voice-id", (string?)sent.Body!["reference_id"]);
        Assert.Equal("en", (string?)sent.Body["language"]);
        Assert.Equal(1.2, (double)sent.Body["prosody"]!["speed"]!);
        Assert.Equal(3, (int)sent.Body["prosody"]!["pitch"]!);
        Assert.Equal(1, (int)sent.Body["pronunciation_dictionary"]!["word"]![0]!);
        Assert.Null(sent.Body["model"]);
    }

    [Fact]
    public async Task Fish_streams_native_alignment_and_reassembles_text_without_spaces()
    {
        await using var host = await FixtureHost.StartAsync(_ => FixtureHost.Sse(
            """{"audio_base64":"AQID","chunk_seq":0,"alignment":{"words":["old"]},"vendor":{"x":[true,null]}}""",
            """{"audio_base64":"","chunk_seq":0,"alignment":{"words":["hello"]}}""", "[DONE]"), avianFishAudioLumaAI: true);
        using var response = await host.PostAsync("/api/speech/stream", """
            {"model":"fishaudio/drama-3","text":[{"type":"text","text":"hel"},{"type":"text","text":"lo"}],
             "outputFormat":{"type":"pcm","rate":24000},"includeRawChunks":true,"providerOptions":{"fishaudio":{"reference_id":"ref"}}}
            """, Fish);
        var output = await GatewayTests.Success(response);
        Assert.Contains("\"type\":\"raw\"", output);
        Assert.Contains("\"vendor\":{\"x\":[true,null]}", output);
        Assert.Contains("\"audio\":\"AQID\"", output);
        Assert.Contains("\"alignments\":[{\"chunk_seq\":0,\"alignment\":{\"words\":[\"hello\"]}}]", output);
        var sent = Assert.Single(host.Requests);
        Assert.Equal("/v1/tts/stream/with-timestamp", sent.Path);
        Assert.Equal("hello", (string?)sent.Body!["text"]);
        Assert.Equal(24000, (int)sent.Body["sample_rate"]!);
        Assert.Equal("ref", (string?)sent.Body["reference_id"]);
    }

    [Fact]
    public async Task Fish_audio_chat_stream_reuses_speech_and_preserves_native_metadata()
    {
        await using var host = await FixtureHost.StartAsync(_ => FixtureHost.Sse(
            """{"audio_base64":"AQID","chunk_seq":0,"native":{"v":2}}""", "[DONE]"), avianFishAudioLumaAI: true);
        using var chat = await host.PostAsync("/v1/chat/completions", """
            {"model":"fishaudio/s2-pro","stream":true,"messages":[{"role":"user","content":"Hello"}],
             "audio":{"voice":"ref-chat","format":"mp3"},"fishaudio":{"temperature":0.7}}
            """, Fish);
        var output = await GatewayTests.Success(chat);
        Assert.Contains("\"data\":\"AQID\"", output);
        Assert.Contains("\"native\":{\"v\":2}", output);
        Assert.Contains("[DONE]", output);
        var sent = Assert.Single(host.Requests);
        Assert.Equal("/v1/tts/stream/with-timestamp", sent.Path);
        Assert.Equal("ref-chat", (string?)sent.Body!["reference_id"]);
        Assert.Equal(0.7, (double)sent.Body["temperature"]!);
    }

    [Fact]
    public async Task Fish_transcription_keeps_native_json_and_converts_asr_alias()
    {
        await using var host = await FixtureHost.StartAsync(_ => FixtureHost.Json("""
            {"text":"hello","language":"en","duration":0.5,"segments":[{"start":0.1,"end":0.4,"text":"hello","speaker_id":"1"}],"native":{"v":[2,null]}}
            """), avianFishAudioLumaAI: true);
        using var response = await host.PostAsync("/api/transcriptions", """
            {"model":"fishaudio/asr","audio":"AQID","mediaType":"audio/wav","providerOptions":{"fishaudio":{"language":"en","ignore_timestamps":false}}}
            """, Fish);
        var result = await Body(response);
        Assert.Equal("hello", (string?)result["text"]);
        Assert.Equal(0.4, (double)result["segments"]![0]!["endSecond"]!);
        Assert.Equal("1", (string?)result["providerMetadata"]!["fishaudio"]!["segments"]![0]!["speaker_id"]);
        var sent = Assert.Single(host.Requests);
        Assert.Equal("/v1/asr", sent.Path);
        Assert.Equal("transcribe-1-pro", sent.Headers["model"]);
        Assert.Contains("name=audio", sent.RawBody);
        Assert.Contains("name=ignore_timestamps", sent.RawBody);
    }

    [Fact]
    public async Task Fish_realtime_and_livekit_keep_join_data_and_reject_model_overrides()
    {
        await using var host = await FixtureHost.StartAsync(_ => FixtureHost.Json("""
            {"transport":"livekit","session_id":"session-1","livekit_url":"wss://example.livekit.cloud","token":"ephemeral",
             "expires_at":"2090-01-01T00:00:00Z","max_duration_seconds":60,"native":{"enabled":true}}
            """), avianFishAudioLumaAI: true);
        using var token = await host.PostAsync("/api/realtime/token/fishaudio/agents/ann", """
            {"sessionConfig":{"instructions":"Hello","providerOptions":{"fishaudio":{"metadata":{"test":[true,null]}}}}}
            """, Fish);
        Assert.Equal("ephemeral", (string?)(await Body(token))["token"]);
        using var live = await host.PostAsync("/v1/live/sessions", """
            {"session":{"model":"fishaudio/agents/ann","audio":{"output":{"voice":"voice-1"}}},"transport":{"type":"livekit"}}
            """, Fish);
        var result = await Body(live);
        Assert.Equal("session-1", (string?)result["session"]!["id"]);
        Assert.True((bool)result["transport"]!["native"]!["enabled"]!);
        Assert.Equal("ephemeral", (string?)result["transport"]!["token"]);
        Assert.All(host.Requests, r =>
        {
            Assert.Equal("/v1/agent/sessions", r.Path);
            Assert.Equal("ann", (string?)r.Body!["agent_id"]);
        });
        Assert.Equal("Hello", (string?)host.Requests.First().Body!["overrides"]!["system_prompt"]);
        Assert.Equal("voice-1", (string?)host.Requests.Last().Body!["overrides"]!["voice_id"]);
        using var mismatch = await host.PostAsync("/api/realtime/token/fishaudio/agents/ann", """
            {"sessionConfig":{"providerOptions":{"fishaudio":{"agent_id":"other"}}}}
            """, Fish);
        Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode);
        Assert.Equal(2, host.Requests.Count);
    }

    [Fact]
    public async Task Luma_images_poll_and_keep_raw_output_metadata_without_authorizing_delivery()
    {
        await using var host = await FixtureHost.StartAsync(r => r.Path switch
        {
            "/v1/generations" => FixtureHost.Json("""{"id":"image-1","state":"queued"}"""),
            "/v1/generations/image-1" => FixtureHost.Json("""
                {"id":"image-1","state":"completed","output":[{"type":"image","url":"https://storage.cdn-luma.com/p.png?sig=presigned"}],"native":{"values":[true,null]}}
                """),
            "/p.png?sig=presigned" => Binary("image/png"),
            _ => throw new InvalidOperationException(r.Path)
        }, avianFishAudioLumaAI: true);
        using var response = await host.PostAsync("/api/images", """
            {"model":"lumaai/uni-1","prompt":"Tree","aspectRatio":"4:3","providerOptions":{"lumaai":{"prompt":"forged","type":"video","native":{"parts":[1,null]}}}}
            """, Luma);
        var result = await Body(response);
        Assert.Equal("data:image/png;base64,AQID", (string?)result["images"]![0]);
        Assert.True((bool)result["providerMetadata"]!["lumaai"]!["native"]!["values"]![0]!);
        var sent = host.Requests.First();
        Assert.Equal("agents.lumalabs.ai", sent.Host);
        Assert.Equal("Bearer luma-fixture", sent.Headers["Authorization"]);
        Assert.Equal("Tree", (string?)sent.Body!["prompt"]);
        Assert.Equal("image", (string?)sent.Body["type"]);
        Assert.Equal("4:3", (string?)sent.Body["aspect_ratio"]);
        Assert.Equal(1, (int)sent.Body["native"]!["parts"]![0]!);
        Assert.Empty(host.Requests.Last().Headers);

        using var edit = await host.PostAsync("/v1/images/edits", """
            {"model":"lumaai/uni-1-max","prompt":"Edit","stream":true,"images":[{"image_url":"data:image/png;base64,AA=="},{"image_url":"data:image/jpeg;base64,AQ=="}]}
            """, Luma);
        Assert.Contains("image_edit.completed", await GatewayTests.Success(edit));
        var editBody = host.Requests.Last(r => r.Method == "POST").Body!;
        Assert.Equal("image_edit", (string?)editBody["type"]);
        Assert.Equal("AA==", (string?)editBody["source"]!["data"]);
        Assert.Equal("image/jpeg", (string?)editBody["image_ref"]![0]!["media_type"]);
    }

    [Fact]
    public async Task Luma_video_reports_pending_completed_and_failed_states_with_raw_metadata()
    {
        var polls = 0;
        await using var host = await FixtureHost.StartAsync(r => r.Path switch
        {
            "/v1/generations" => FixtureHost.Json("""{"id":"video-1","state":"queued","estimate":{"tokens":100}}"""),
            "/v1/generations/video-1" => Interlocked.Increment(ref polls) switch
            {
                1 => FixtureHost.Json("""{"state":"processing","progress":0.5}"""),
                2 => FixtureHost.Json("""{"state":"completed","output":[{"type":"video","url":"https://storage.cdn-luma.com/v.mp4"}],"native":[1,null]}"""),
                _ => FixtureHost.Json("""{"state":"failed","failure_reason":"Bad frame"}""")
            },
            "/v.mp4" => Binary("video/mp4"),
            _ => throw new InvalidOperationException(r.Path)
        }, configure: services => services.AddStreaikReferences(), avianFishAudioLumaAI: true);
        using var start = await host.PostAsync("/api/videos", """
            {"model":"lumaai/ray-3.2","prompt":"Animate","duration":5,"resolution":"720p","image":{"type":"file","mediaType":"image/png","data":"AA=="},
             "providerOptions":{"lumaai":{"loop":true,"video":{"custom":{"values":[2,null]},"duration":"forged"}}}}
            """, Luma);
        var started = await Body(start);
        Assert.Equal(100, (int)started["providerMetadata"]!["lumaai"]!["estimate"]!["tokens"]!);
        var sent = host.Requests.First().Body!["video"]!;
        Assert.True((bool)sent["loop"]!);
        Assert.Equal("5s", (string?)sent["duration"]);
        Assert.Equal("AA==", (string?)sent["start_frame"]!["data"]);
        Assert.Equal(2, (int)sent["custom"]!["values"]![0]!);
        var path = "/api/videos/" + (string)started["operation"]!;
        using var pending = await host.GetAsync(path, Luma);
        Assert.Equal(0.5, (double)(await Body(pending))["providerMetadata"]!["lumaai"]!["progress"]!);
        using var complete = await host.GetAsync(path, Luma);
        var completed = await Body(complete);
        Assert.Equal("completed", (string?)completed["status"]);
        Assert.Equal("AQID", (string?)completed["videos"]![0]!["data"]);
        Assert.Empty(host.Requests.Last().Headers);
        using var failed = await host.GetAsync(path, Luma);
        var error = await Body(failed);
        Assert.Equal("error", (string?)error["status"]);
        Assert.Equal("Bad frame", (string?)error["providerMetadata"]!["lumaai"]!["failure_reason"]);
    }

    [Fact]
    public async Task Luma_video_uses_the_existing_responses_media_bridge()
    {
        await using var host = await FixtureHost.StartAsync(r => r.Path switch
        {
            "/v1/generations" => FixtureHost.Json("""{"state":"queued","id":"v"}"""),
            "/v1/generations/v" => FixtureHost.Json("""{"state":"completed","output":[{"type":"video","url":"https://storage.cdn-luma.com/move.mp4"}],"native":{"n":7}}"""),
            "/move.mp4" => Binary("video/mp4"),
            _ => throw new InvalidOperationException(r.Path)
        }, configure: services => services.AddStreaikReferences(), avianFishAudioLumaAI: true);
        using var response = await host.PostAsync("/v1/responses", """
            {"model":"lumaai/ray-3.2","stream":true,"input":"Make it move","lumaai":{"loop":true}}
            """, Luma);
        var output = await GatewayTests.Success(response);
        Assert.Contains("generate_video", output);
        Assert.Contains("response.completed", output);
        Assert.Contains("\"native\":{\"n\":7}", output);
        var sent = host.Requests.First();
        Assert.Equal("Make it move", (string?)sent.Body!["prompt"]);
        Assert.True((bool)sent.Body["video"]!["loop"]!);
    }

    [Fact]
    public async Task Luma_rejects_untrusted_delivery_without_fetching_and_honors_cancellation()
    {
        var poll = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = await FixtureHost.StartAsync(r => r.Path switch
        {
            "/v1/generations" when (string?)r.Body?["prompt"] == "Cancel" => FixtureHost.Json("""{"id":"cancel-job","state":"queued"}"""),
            "/v1/generations" => FixtureHost.Json("""{"id":"untrusted-job","state":"queued"}"""),
            "/v1/generations/cancel-job" => SignalPending(),
            "/v1/generations/untrusted-job" => FixtureHost.Json("""{"state":"completed","output":[{"url":"https://untrusted.example/photo.png"}]}"""),
            _ => throw new InvalidOperationException(r.Path)
        }, avianFishAudioLumaAI: true);

        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/images/generations")
        {
            Content = new StringContent("""{"model":"lumaai/uni-1","prompt":"Cancel"}""", Encoding.UTF8, "application/json")
        };
        request.Headers.Add(Luma.Item1, Luma.Item2);
        var canceled = host.Client.SendAsync(request, ct.Token);
        await poll.Task.WaitAsync(ct.Token);
        await ct.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
        using var untrusted = await host.PostAsync("/v1/images/generations", """{"model":"lumaai/uni-1","prompt":"Untrusted"}""", Luma);
        Assert.Equal(HttpStatusCode.BadGateway, untrusted.StatusCode);
        Assert.All(host.Requests, r => Assert.Equal("agents.lumalabs.ai", r.Host));

        HttpResponseMessage SignalPending()
        {
            poll.TrySetResult();
            return FixtureHost.Json("""{"state":"processing"}""");
        }
    }

    private static async Task<JsonNode> Body(HttpResponseMessage response) => JsonNode.Parse(await GatewayTests.Success(response))!;
    private static HttpResponseMessage Binary(string type) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent([1, 2, 3]) { Headers = { ContentType = new MediaTypeHeaderValue(type) } }
    };
}
