using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace Streaik.Gateway.Tests;

public sealed class AIBramhaReveAddisAITests
{
    private static readonly (string, string) Bramha = ("x-aibramha-key", "bramha-fixture");
    private static readonly (string, string) Reve = ("x-reve-key", "reve-fixture");
    private static readonly (string, string) Addis = ("x-addisai-key", "addis-fixture");

    [Fact]
    public async Task Models_use_configured_credentials_and_preserve_native_ids()
    {
        await using var host = await FixtureHost.StartAsync(Reply, aiBramhaReveAddisAI: true);
        using var anonymous = await host.GetAsync("/v1/models");
        var noKey = await GatewayTests.Success(anonymous);
        Assert.DoesNotContain("aibramha/", noKey);
        Assert.DoesNotContain("reve/", noKey);
        Assert.DoesNotContain("addisai/", noKey);
        Assert.Empty(host.Requests);

        using var response = await host.GetAsync("/v1/models", Bramha, Reve, Addis);
        var models = JsonNode.Parse(await GatewayTests.Success(response))!["data"]!.AsArray()
            .ToDictionary(model => model!["id"]!.GetValue<string>());
        Assert.Equal(8192, models["aibramha/vendor/Model"]!["context_window"]!.GetValue<int>());
        Assert.Equal("Vendor", models["aibramha/vendor/Model"]!["owned_by"]!.GetValue<string>());
        Assert.Contains("reve/latest", models.Keys);
        Assert.Contains("reve/reve-v2-create@260601", models.Keys);
        Assert.Contains("addisai/addis-1-alef", models.Keys);
        Assert.Contains("addisai/stt/om", models.Keys);
        Assert.Contains("addisai/tts/am", models.Keys);
        Assert.Equal(6, models.Keys.Count(id => id.StartsWith("addisai/translate/", StringComparison.Ordinal)));
        var request = Assert.Single(host.Requests);
        Assert.Equal("/api/v1/models", request.Path);
        Assert.Equal("Bearer bramha-fixture", request.Headers["Authorization"]);
    }

    [Theory]
    [InlineData("aibramha", "chat/completions", false)]
    [InlineData("aibramha", "chat/completions", true)]
    [InlineData("aibramha", "responses", false)]
    [InlineData("aibramha", "messages", true)]
    [InlineData("aibramha", "vercel", true)]
    [InlineData("addisai", "chat/completions", false)]
    [InlineData("addisai", "responses", true)]
    [InlineData("addisai", "messages", false)]
    [InlineData("addisai", "vercel", true)]
    public async Task Chat_protocols_forward_native_options_and_return_raw_metadata(string provider, string protocol, bool stream)
    {
        await using var host = await FixtureHost.StartAsync(Reply, aiBramhaReveAddisAI: true);
        var model = provider == "aibramha" ? "vendor/Model" : "addis-1-alef";
        var options = JsonNode.Parse("""{"trace":{"values":[1,true]},"model":"wrong","headers":{"Authorization":"bad"}}""")!;
        if (provider == "addisai") options["generation_config"] = JsonNode.Parse("""{"stream":true,"future":{"nested":null}}""");
        var request = new JsonObject { ["model"] = $"{provider}/{model}" };
        if (protocol == "vercel")
        {
            request["providerMetadata"] = new JsonObject { [provider] = options };
            request["messages"] = JsonNode.Parse("""[{"id":"u1","role":"user","parts":[{"type":"text","text":"hi"}]}]""");
        }
        else
        {
            request[provider] = options;
            request["stream"] = stream;
            if (protocol == "responses") request["input"] = "hi";
            else request["messages"] = JsonNode.Parse("""[{"role":"user","content":"hi"}]""");
            if (protocol == "messages") request["max_tokens"] = 64;
        }
        using var response = await host.PostAsync(protocol == "vercel" ? "/api/chat" : $"/v1/{protocol}", request.ToJsonString(),
            provider == "addisai" ? Addis : Bramha);
        var wire = await GatewayTests.Success(response);
        Assert.Contains("hello", wire);
        Assert.Contains("future_response", wire);
        var native = Assert.Single(host.Requests);
        Assert.True(native.Body!["trace"]!["values"]![1]!.GetValue<bool>());
        Assert.Null(native.Body["headers"]);
        if (provider == "aibramha")
        {
            Assert.Equal("/api/v1/chat/completions", native.Path);
            Assert.Equal("www.aibramha.in", native.Host);
            Assert.Equal("Bearer bramha-fixture", native.Headers["Authorization"]);
            Assert.Equal(model, native.Body["model"]!.GetValue<string>());
            Assert.Equal(stream, native.Stream);
        }
        else
        {
            Assert.Equal("/api/v1/chat_generate", native.Path);
            Assert.Equal("api.addisassistant.com", native.Host);
            Assert.Equal("addis-fixture", native.Headers["x-api-key"]);
            Assert.Equal("hi", native.Body["prompt"]!.GetValue<string>());
            Assert.Equal("am", native.Body["target_language"]!.GetValue<string>());
            Assert.False(native.Body["generation_config"]!["stream"]!.GetValue<bool>());
            Assert.True(native.Body["generation_config"]!["future"]!.AsObject().ContainsKey("nested"));
        }
    }

    [Fact]
    public async Task Reve_generation_preserves_raw_postprocessing_and_credits()
    {
        await using var host = await FixtureHost.StartAsync(Reply, aiBramhaReveAddisAI: true);
        using var response = await host.PostAsync("/api/images", """
            {"model":"reve/latest","prompt":"draw","aspectRatio":"3:2","n":2,"seed":8,
             "providerOptions":{"reve":{"version":"wrong","stream":true,"extension":{"a":[null,1]},
             "postprocessing":[{"process":"upscale","upscale_factor":2,"future":{"keep":true}}]}}}
            """, Reve);
        var json = JsonNode.Parse(await GatewayTests.Success(response))!;
        Assert.Contains("aGVsbG8=", json["images"]![0]!.GetValue<string>());
        Assert.Equal(3, json["providerMetadata"]!["reve"]!["credits_used"]!.GetValue<int>());
        Assert.Equal(0.0039999999999999m, json["providerMetadata"]!["gateway"]!["cost"]!.GetValue<decimal>());
        Assert.True(json["providerMetadata"]!["reve"]!["native"]!["nested"]![1]!.GetValue<bool>());
        Assert.Null(json["providerMetadata"]!["reve"]!["image"]);
        Assert.Equal(2, json["warnings"]!.AsArray().Count);
        var native = Assert.Single(host.Requests);
        Assert.Equal("/v2/image/create", native.Path);
        Assert.Equal("reve-fixture", native.Headers["x-api-key"]);
        Assert.Equal("reve-v2-create@260601", native.Body!["version"]!.GetValue<string>());
        Assert.Equal("3:2", native.Body["aspect_ratio"]!.GetValue<string>());
        Assert.True(native.Body["postprocessing"]![0]!["future"]!["keep"]!.GetValue<bool>());
        Assert.True(native.Body["extension"]!["a"]!.AsArray()[0] is null);
        Assert.Null(native.Body["stream"]);
    }

    [Fact]
    public async Task Reve_edit_accepts_inline_and_remote_references_and_emits_one_completion()
    {
        await using var host = await FixtureHost.StartAsync(Reply, aiBramhaReveAddisAI: true);
        using var response = await host.PostAsync("/v1/images/edits", """
            {"model":"reve/reve-v2-create@20260601","prompt":"edit","stream":true,
             "images":[{"image_url":"data:image/png;base64,YQ=="},{"image_url":"https://example.invalid/reference.png"}],
             "reve":{"references":[{"data":"wrong"}],"native":{"keep":true}}}
            """, Reve);
        var wire = await GatewayTests.Success(response);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        Assert.Contains("event: image_edit.completed", wire);
        Assert.Contains("credits_used", wire);
        Assert.DoesNotContain("partial_image", wire);
        var native = Assert.Single(host.Requests);
        Assert.Equal("reve-v2-create@260601", native.Body!["version"]!.GetValue<string>());
        Assert.Equal(new[] { "YQ==", "https://example.invalid/reference.png" },
            native.Body["references"]!.AsArray().Select(reference => reference!["data"]!.GetValue<string>()));
        Assert.True(native.Body["native"]!["keep"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Reve_edit_maps_multipart_uploads_to_native_references()
    {
        await using var host = await FixtureHost.StartAsync(Reply, aiBramhaReveAddisAI: true);
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("reve/latest"), "model");
        form.Add(new StringContent("edit"), "prompt");
        form.Add(new ByteArrayContent([1, 2, 3]) { Headers = { ContentType = new MediaTypeHeaderValue("image/png") } }, "image", "input.png");
        using var response = await host.PostFormAsync("/v1/images/edits", form, Reve);
        var json = JsonNode.Parse(await GatewayTests.Success(response))!;
        Assert.Equal("aGVsbG8=", json["data"]![0]!["b64_json"]!.GetValue<string>());
        Assert.Equal("AQID", Assert.Single(host.Requests).Body!["references"]![0]!["data"]!.GetValue<string>());
    }

    [Fact]
    public async Task AddisAI_translation_uses_model_pair_and_preserves_each_raw_result_in_order()
    {
        await using var host = await FixtureHost.StartAsync(Reply, aiBramhaReveAddisAI: true);
        using var response = await host.PostAsync("/v1/responses", """
            {"model":"addisai/translate/en/om","input":[{"role":"user","content":"one"},{"role":"assistant","content":"skip"},
             {"role":"user","content":"two"}],"addisai":{"target_language":"bad","extension":{"values":[1,true]}}}
            """, Addis);
        var json = JsonNode.Parse(await GatewayTests.Success(response))!;
        Assert.Equal("translated one\ntranslated two", json["output"]![0]!["content"]![0]!["text"]!.GetValue<string>());
        Assert.Equal("one", json["metadata"]!["addisai"]!["responses"]![0]!["raw_text"]!.GetValue<string>());
        Assert.Equal("two", json["metadata"]!["addisai"]!["responses"]![1]!["raw_text"]!.GetValue<string>());
        Assert.Equal(new[] { "one", "two" }, host.Requests.Select(request => request.Body!["text"]!.GetValue<string>()));
        Assert.All(host.Requests, request =>
        {
            Assert.Equal("/api/v1/translate", request.Path);
            Assert.Equal("en", request.Body!["source_language"]!.GetValue<string>());
            Assert.Equal("om", request.Body["target_language"]!.GetValue<string>());
            Assert.True(request.Body["extension"]!["values"]![1]!.GetValue<bool>());
        });
    }

    [Fact]
    public async Task AddisAI_speech_preserves_metadata_and_frames_buffered_WAV()
    {
        await using var host = await FixtureHost.StartAsync(Reply, aiBramhaReveAddisAI: true);
        using var response = await host.PostAsync("/api/speech", """
            {"model":"addisai/tts/am","text":"hi","outputFormat":"mp3",
             "providerOptions":{"addisai":{"language":"om","stream":true,"opaque":{"value":null}}}}
            """, Addis);
        var json = JsonNode.Parse(await GatewayTests.Success(response))!;
        Assert.Equal("wav", json["audio"]!["format"]!.GetValue<string>());
        Assert.Equal("aGVsbG8=", json["audio"]!["base64"]!.GetValue<string>());
        Assert.True(json["providerMetadata"]!["addisai"]!["response"]!["extra"]!["flag"]!.GetValue<bool>());
        Assert.Null(json["providerMetadata"]!["addisai"]!["response"]!["audio"]);
        using var stream = await host.PostAsync("/v1/audio/speech", """
            {"model":"addisai/tts/om","input":"hi","voice":"alloy","stream_format":"sse"}
            """, Addis);
        var wire = await GatewayTests.Success(stream);
        Assert.Contains("speech.audio.delta", wire);
        Assert.Contains("speech.audio.done", wire);
        Assert.Contains("aGVsbG8=", wire);
        var requests = host.Requests.ToArray();
        Assert.Equal(2, requests.Length);
        Assert.Equal("am", requests[0].Body!["language"]!.GetValue<string>());
        Assert.True(requests[0].Body!["opaque"]!.AsObject().ContainsKey("value"));
        Assert.Equal("om", requests[1].Body!["language"]!.GetValue<string>());
        Assert.All(requests, request =>
        {
            Assert.Equal("/api/v1/audio", request.Path);
            Assert.False(request.Body!["stream"]!.GetValue<bool>());
        });
    }

    [Fact]
    public async Task AddisAI_transcription_posts_audio_and_keeps_native_metadata_on_Vercel()
    {
        await using var host = await FixtureHost.StartAsync(Reply, aiBramhaReveAddisAI: true);
        using var response = await host.PostAsync("/api/transcriptions", """
            {"model":"addisai/stt/om","audio":"aGVsbG8=","mediaType":"audio/wav",
             "providerOptions":{"addisai":{"language_code":"am","future":{"values":[null,1]}}}}
            """, Addis);
        var json = JsonNode.Parse(await GatewayTests.Success(response))!;
        Assert.Equal("heard", json["text"]!.GetValue<string>());
        Assert.Equal("om", json["language"]!.GetValue<string>());
        Assert.Equal(0.8, json["providerMetadata"]!["addisai"]!["confidence"]!.GetValue<double>());
        Assert.True(json["providerMetadata"]!["addisai"]!["response"]!["data"]!["extra"]!.GetValue<bool>());
        var native = Assert.Single(host.Requests);
        Assert.Equal("/api/v2/stt", native.Path);
        Assert.Equal("addis-fixture", native.Headers["x-api-key"]);
        Assert.Contains("name=audio;", native.RawBody);
        Assert.Contains("hello", native.RawBody);
        Assert.Contains("name=request_data", native.RawBody);
        Assert.Contains("\"language_code\":\"om\"", native.RawBody);
        Assert.Contains("\"future\":{\"values\":[null,1]}", native.RawBody);
    }

    [Fact]
    public async Task AddisAI_OpenAI_transcription_can_frame_buffered_text_without_changing_the_wire()
    {
        await using var host = await FixtureHost.StartAsync(Reply, aiBramhaReveAddisAI: true);
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("addisai/stt/am"), "model");
        form.Add(new StringContent("true"), "stream");
        form.Add(new ByteArrayContent([1, 2, 3]) { Headers = { ContentType = new MediaTypeHeaderValue("audio/wav") } }, "file", "input.wav");
        using var response = await host.PostFormAsync("/v1/audio/transcriptions", form, Addis);
        var wire = await GatewayTests.Success(response);
        Assert.Contains("transcript.text.delta", wire);
        Assert.Contains("transcript.text.done", wire);
        Assert.Contains("heard", wire);
        Assert.DoesNotContain("providerMetadata", wire);
        Assert.Equal("/api/v2/stt", Assert.Single(host.Requests).Path);
    }

    [Fact]
    public async Task AddisAI_transcription_model_routes_Responses_audio_to_the_STT_endpoint()
    {
        await using var host = await FixtureHost.StartAsync(Reply, aiBramhaReveAddisAI: true);
        using var response = await host.PostAsync("/v1/responses", """
            {"model":"addisai/stt/am","input":[{"role":"user","content":[
             {"type":"input_file","filename":"audio.wav","file_data":"data:audio/wav;base64,aGVsbG8="}]}],
             "addisai":{"future":{"enabled":true}}}
            """, Addis);
        var json = JsonNode.Parse(await GatewayTests.Success(response))!;
        Assert.Equal("heard", json["output"]![0]!["content"]![0]!["text"]!.GetValue<string>());
        Assert.Equal(0.8, json["metadata"]!["addisai"]!["confidence"]!.GetValue<double>());
        var request = Assert.Single(host.Requests);
        Assert.Equal("/api/v2/stt", request.Path);
        Assert.Contains("\"future\":{\"enabled\":true}", request.RawBody);
    }

    [Fact]
    public async Task Invalid_native_options_do_not_send_requests_and_bad_upstream_results_fail()
    {
        await using var host = await FixtureHost.StartAsync(_ => FixtureHost.Json("""{"no_image":true}"""), aiBramhaReveAddisAI: true);
        using var invalid = await host.PostAsync("/v1/images/generations", """
            {"model":"reve/latest","prompt":"draw","reve":{"postprocessing":"wrong"}}
            """, Reve);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var invalidPair = await host.PostAsync("/v1/chat/completions", """
            {"model":"addisai/translate/en/en","messages":[{"role":"user","content":"hi"}]}
            """, Addis);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalidPair.StatusCode);
        Assert.Empty(host.Requests);
        using var malformed = await host.PostAsync("/v1/images/generations", """
            {"model":"reve/latest","prompt":"draw"}
            """, Reve);
        Assert.Equal(HttpStatusCode.BadGateway, malformed.StatusCode);
    }

    private static HttpResponseMessage Reply(FixtureHost.UpstreamRequest request) => request.Path switch
    {
        "/api/v1/models" => FixtureHost.Json("""
            {"models":[{"id":"vendor/Model","name":"Fixture","description":"native description","category":"language",
              "provider":"Vendor","context_length":8192}]}
            """),
        "/api/v1/chat/completions" => FixtureHost.Reply(request with { Path = "/v1/chat/completions" }),
        "/api/v1/chat_generate" => FixtureHost.Json("""
            {"response_text":"hello","modelVersion":"test","usage_metadata":{"prompt_token_count":1,"candidates_token_count":2,
             "total_token_count":3},"future_response":{"flags":[true,null]}}
            """),
        "/v2/image/create" => FixtureHost.Json("""{"image":"aGVsbG8=","credits_used":3,"native":{"nested":[null,true]}}"""),
        "/api/v1/translate" => FixtureHost.Json(new JsonObject
        {
            ["data"] = new JsonObject { ["translation"] = $"translated {request.Body!["text"]!.GetValue<string>()}" },
            ["raw_text"] = request.Body!["text"]!.DeepClone()
        }.ToJsonString()),
        "/api/v1/audio" => FixtureHost.Json("""{"audio":"aGVsbG8=","extra":{"flag":true}}"""),
        "/api/v2/stt" => FixtureHost.Json("""{"data":{"transcription":"heard","extra":true},"confidence":0.8}"""),
        _ => FixtureHost.Reply(request)
    };
}
