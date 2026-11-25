using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace Streaik.Gateway.Tests;

public sealed class BastionTensorUnrealTests
{
    private const string Chat = "/v1/chat/completions";
    private const string Responses = "/v1/responses";
    private const string Messages = "/v1/messages";
    private const string Vercel = "/api/chat";
    private const string Future = """{"levels":[2,{"enabled":true},null]}""";
    private static readonly (string, string) Bastion = ("X-BastionGPT-Key", "bastion-key");
    private static readonly (string, string) Tensor = ("X-TensorBlock-Key", "tensor-key");
    private static readonly (string, string) Unreal = ("X-UnrealSpeech-Key", "unreal-key");

    [Fact]
    public async Task Models_are_key_scoped_and_keep_native_ids()
    {
        await using var host = await Start(request => FixtureHost.Json("""
            {"data":[{"id":"vendor/Model","name":"My model","context_length":4096,"description":"Tensor model"}]}
            """));
        using var anonymous = await host.GetAsync("/v1/models");
        Assert.Empty((await Body(anonymous))["data"]!.AsArray());
        Assert.Empty(host.Requests);
        using var response = await host.GetAsync("/v1/models", Bastion, Tensor, Unreal);
        var list = (await Body(response))["data"]!.AsArray().ToDictionary(x => x!["id"]!.GetValue<string>());
        Assert.Equal(500_000, (int?)list["bastiongpt/bastiongpt"]!["context_window"]);
        Assert.Equal("language", (string?)list["tensorblock/vendor/Model"]!["type"]);
        Assert.Equal(4096, (int?)list["tensorblock/vendor/Model"]!["context_window"]);
        Assert.Equal("speech", (string?)list["unrealspeech/Élodie"]!["type"]);
        Assert.Equal(38, list.Keys.Count(id => id.StartsWith("unrealspeech/", StringComparison.Ordinal)));
        var request = Assert.Single(host.Requests);
        Assert.Equal("/v1/models", request.Path);
        Assert.Equal("Bearer tensor-key", request.Headers["Authorization"]);
    }

    [Theory]
    [InlineData(Chat, false)]
    [InlineData(Chat, true)]
    [InlineData(Responses, false)]
    [InlineData(Messages, true)]
    [InlineData(Vercel, true)]
    public async Task Bastion_uses_key_auth_and_chat_bridge_without_sending_model(string route, bool stream)
    {
        await using var host = await Start();
        var payload = GatewayTests.Request(route, "bastiongpt/bastiongpt", stream);
        Options(payload, route, "bastiongpt");
        using var response = await host.PostAsync(route, payload.ToJsonString(), Bastion);
        var body = await GatewayTests.Success(response);
        Assert.Contains("hello", body);
        Assert.Contains("future_response", body);
        var sent = Assert.Single(host.Requests);
        Assert.Equal("api.bastiongpt.com", sent.Host);
        Assert.Equal("/v1/ChatCompletion", sent.Path);
        Assert.Equal("bastion-key", sent.Headers["Key"]);
        Assert.False(sent.Headers.ContainsKey("Authorization"));
        Assert.Null(sent.Body!["model"]);
        Assert.Equal(stream, (bool?)sent.Body["stream"]);
        AssertOptions(sent.Body);
    }

    [Theory]
    [InlineData(Chat, false, Chat, "future_response")]
    [InlineData(Chat, true, Chat, "[DONE]")]
    [InlineData(Responses, false, Responses, "future_response")]
    [InlineData(Responses, true, Responses, "response.completed")]
    [InlineData(Messages, false, Messages, "future_content")]
    [InlineData(Messages, true, Messages, "message_stop")]
    [InlineData(Vercel, true, Chat, "future_response")]
    public async Task Tensor_uses_native_text_protocols_and_chat_for_Vercel(string route, bool stream, string path, string expected)
    {
        await using var host = await Start();
        var payload = GatewayTests.Request(route, "tensorblock/vendor/model", stream);
        Options(payload, route, "tensorblock");
        using var response = await host.PostAsync(route, payload.ToJsonString(), Tensor, ("anthropic-beta", "fixture-beta"));
        var body = await GatewayTests.Success(response);
        Assert.Contains("hello", body);
        Assert.Contains(expected, body);
        var sent = Assert.Single(host.Requests);
        Assert.Equal("api.forge.tensorblock.co", sent.Host);
        Assert.Equal(path, sent.Path);
        Assert.Equal("Bearer tensor-key", sent.Headers["Authorization"]);
        if (route == Messages) Assert.Equal("fixture-beta", sent.Headers["anthropic-beta"]);
        Assert.Equal("vendor/model", (string?)sent.Body!["model"]);
        AssertOptions(sent.Body);
    }

    [Fact]
    public async Task Tensor_images_preserve_nested_diagnostics_and_download_assets_without_credentials()
    {
        await using var host = await Start(request => request.Method == "GET" ? Binary("image/webp") : FixtureHost.Json("""
            {"created":123,"data":[{"url":"https://cdn.tensorblock.co/asset?signature=secret","diagnostics":{"channels":[1,2]}}],"vendor_result":{"nested":[false,null]}}
            """));
        using var response = await host.PostAsync("/api/images", $$"""
            {"model":"tensorblock/vendor/image","prompt":"draw","aspectRatio":"3:2","seed":19,
             "providerOptions":{"tensorblock":{"future_option":{{Future}},"model":"wrong"} } }
            """, Tensor);
        var result = await Body(response);
        Assert.Contains("AQID", (string?)result["images"]![0]);
        var metadata = result["providerMetadata"]!["tensorblock"]!;
        Assert.Equal(2, (int?)metadata["data"]![0]!["diagnostics"]!["channels"]![1]);
        Assert.False((bool)metadata["vendor_result"]!["nested"]![0]!);
        Assert.DoesNotContain("signature=secret", result.ToJsonString());
        var upstream = host.Requests.ToArray();
        Assert.Equal("/v1/images/generations", upstream[0].Path);
        Assert.Equal("vendor/image", (string?)upstream[0].Body!["model"]);
        Assert.Equal("3:2", (string?)upstream[0].Body!["aspect_ratio"]);
        Assert.Equal(19, (int?)upstream[0].Body!["seed"]);
        AssertOptions(upstream[0].Body!);
        Assert.Equal("cdn.tensorblock.co", upstream[1].Host);
        Assert.False(upstream[1].Headers.ContainsKey("Authorization"));
    }

    [Theory]
    [InlineData(false, "/v1/images/generations", "image_generation.completed")]
    [InlineData(true, "/v1/images/edits", "image_edit.completed")]
    public async Task Tensor_streams_existing_image_completion_frames(bool edit, string path, string expected)
    {
        await using var host = await Start(_ => FixtureHost.Json("""
            {"created":123,"data":[{"b64_json":"AQID","mime_type":"image/png"}],"usage":{"input_tokens":2,"output_tokens":3,"total_tokens":5}}
            """));
        var payload = JsonNode.Parse("""{"model":"tensorblock/vendor/image","prompt":"draw","stream":true}""")!.AsObject();
        if (edit) payload["images"] = JsonNode.Parse("""[{"image_url":"data:image/png;base64,AQID"},{"image_url":"https://example.net/unused.png"}]""");
        using var response = await host.PostAsync(path, payload.ToJsonString(), Tensor);
        var events = await GatewayTests.Success(response);
        Assert.Contains(expected, events);
        Assert.Contains("AQID", events);
        Assert.Contains("\"total_tokens\":5", events);
        var request = Assert.Single(host.Requests);
        Assert.Equal(path, request.Path);
        Assert.Null(request.Body!["stream"]);
        if (edit)
        {
            Assert.Equal("data:image/png;base64,AQID", (string?)request.Body["image"]);
            Assert.Null(request.Body["images"]);
        }
    }

    [Fact]
    public async Task Tensor_accepts_operator_configured_delivery_hosts_for_buffered_image_batches()
    {
        const string path = "/v1/images/generations";
        const string payload = """{"model":"tensorblock/vendor/image","prompt":"draw","n":2}""";
        await using var host = await FixtureHost.StartAsync(request => request.Method == "GET" ? Binary("image/png") : FixtureHost.Json("""
            {"data":[{"url":"https://images.vendor.example/draw.png"},{"b64_json":"AQID"}]}
            """), configureHost: builder => builder.Configuration["Providers:tensorblock:AssetHosts:0"] = "images.vendor.example", bastionTensorUnreal: true);
        using var response = await host.PostAsync(path, payload, Tensor);
        Assert.Equal(2, (await Body(response))["data"]!.AsArray().Count);
        Assert.Equal("images.vendor.example", host.Requests.Last().Host);
        Assert.False(host.Requests.Last().Headers.ContainsKey("Authorization"));

        using var stream = await host.PostAsync(path, payload.Insert(payload.Length - 1, ",\"stream\":true"), Tensor);
        Assert.Equal(HttpStatusCode.BadRequest, stream.StatusCode);
        Assert.Equal(2, host.Requests.Count);
    }

    [Theory]
    [InlineData(1000, "/stream")]
    [InlineData(1001, "/speech")]
    [InlineData(3000, "/speech")]
    [InlineData(3001, "/synthesisTasks")]
    public async Task Unreal_synthesis_preserves_options_and_job_metadata(int length, string endpoint)
    {
        await using var host = await Start(request => request.Path switch
        {
            "/stream" => Binary("audio/mpeg"),
            "/speech" => FixtureHost.Json("""
                {"OutputUri":"https://unreal-expire-in-90-days.s3-us-west-2.amazonaws.com/audio.mp3?signature=secret",
                 "TimestampsUri":"https://unreal-expire-in-90-days.s3-us-west-2.amazonaws.com/words.json","Timestamps":[{"word":"hello","start":0.1}]}
                """),
            "/synthesisTasks" => FixtureHost.Json("""{"SynthesisTask":{"TaskId":"job 1"},"VendorOptions":{"queued":true}}"""),
            "/synthesisTasks/job%201" => FixtureHost.Json("""
                {"SynthesisTask":{"TaskStatus":"completed","OutputUri":["https://assets.unrealspeech.com/audio.mp3?signature=secret"]},"diagnostics":{"values":[1,2]}}
                """),
            "/audio.mp3?signature=secret" => Binary("audio/mpeg"),
            _ => throw new InvalidOperationException(request.Path)
        });
        using var response = await host.PostAsync("/api/speech", $$"""
            {"model":"unrealspeech/Autumn","text":"{{new string('x', length)}}","outputFormat":"wav",
             "providerOptions":{"unrealspeech":{"future_option":{{Future}},"VoiceId":"wrong","timestampType":"word","speed":0.2,"pitch":1.1} } }
            """, Unreal);
        var body = await Body(response);
        Assert.Equal("AQID", (string?)body["audio"]!["base64"]);
        Assert.Equal("mp3", (string?)body["audio"]!["format"]);
        var meta = body["providerMetadata"]!["unrealspeech"]!;
        Assert.Equal(length, (int?)meta["charCount"]);
        Assert.DoesNotContain("signature=secret", body.ToJsonString());
        var requests = host.Requests.ToArray();
        Assert.Equal(endpoint, requests[0].Path);
        Assert.Equal("Autumn", (string?)requests[0].Body!["VoiceId"]);
        Assert.Equal("Bearer unreal-key", requests[0].Headers["Authorization"]);
        AssertOptions(requests[0].Body!);
        if (endpoint == "/stream") Assert.Single(requests);
        else
        {
            Assert.Equal("word", (string?)requests[0].Body!["TimestampType"]);
            Assert.Equal("uri", (string?)requests[0].Body!["OutputFormat"]);
            Assert.False(requests[^1].Headers.ContainsKey("Authorization"));
            if (endpoint == "/speech")
            {
                Assert.Equal("unreal-expire-in-90-days.s3-us-west-2.amazonaws.com", requests[^1].Host);
                Assert.Equal("hello", (string?)meta["result"]!["response"]!["Timestamps"]![0]!["word"]);
                Assert.EndsWith("/words.json", (string?)meta["result"]!["response"]!["TimestampsUri"]);
            }
            else
            {
                Assert.Equal("Bearer unreal-key", requests[1].Headers["Authorization"]);
                Assert.True((bool?)meta["result"]!["create"]!["VendorOptions"]!["queued"]);
                Assert.Equal(2, (int?)meta["result"]!["final"]!["diagnostics"]!["values"]![1]);
            }
        }
    }

    [Theory]
    [InlineData(Chat, false, "\"audio\"")]
    [InlineData(Chat, true, "[DONE]")]
    [InlineData(Responses, true, "response.completed")]
    [InlineData(Messages, false, "\"document\"")]
    [InlineData(Vercel, true, "\"file\"")]
    public async Task Unreal_speech_uses_existing_chat_adapters(string route, bool stream, string expected)
    {
        await using var host = await Start(_ => Binary("audio/mpeg"));
        var payload = GatewayTests.Request(route, "unrealspeech/Autumn", stream);
        Options(payload, route, "unrealspeech");
        using var response = await host.PostAsync(route, payload.ToJsonString(), Unreal);
        var body = await GatewayTests.Success(response);
        Assert.Contains(expected, body);
        Assert.Contains("AQID", body);
        Assert.Contains("charCount", body);
        var upstream = Assert.Single(host.Requests);
        Assert.Equal("/stream", upstream.Path);
        AssertOptions(upstream.Body!);
    }

    [Fact]
    public async Task Unreal_streams_native_audio_and_SDK_events_and_rejects_invalid_options()
    {
        await using var host = await Start(_ => Binary("audio/mpeg"));
        using var native = await host.PostAsync("/v1/audio/speech", """{"model":"unrealspeech/Autumn","input":"hello","stream_format":"sse"}""", Unreal);
        Assert.Contains("speech.audio.done", await GatewayTests.Success(native));
        using var sdk = await host.PostAsync("/api/speech/stream", """{"model":"unrealspeech/Autumn","text":[{"text":"hello"}]}""", Unreal);
        var events = await GatewayTests.Success(sdk);
        Assert.Contains("AQID", events);
        Assert.Contains("charCount", events);
        using var invalid = await host.PostAsync("/api/speech", """
            {"model":"unrealspeech/Autumn","text":"hello","providerOptions":{"unrealspeech":{"pitch":2}}}
            """, Unreal);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(2, host.Requests.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Media_rejects_untrusted_delivery_hosts_without_fetching(bool speech)
    {
        await using var host = await Start(_ => FixtureHost.Json(speech
            ? """{"OutputUri":"https://127.0.0.1/private"}"""
            : """{"data":[{"url":"https://127.0.0.1/private"}]}"""));
        using var response = speech
            ? await host.PostAsync("/api/speech", $$"""{"model":"unrealspeech/Autumn","text":"{{new string('x', 1001)}}"}""", Unreal)
            : await host.PostAsync("/api/images", """{"model":"tensorblock/vendor/image","prompt":"draw"}""", Tensor);
        Assert.False(response.IsSuccessStatusCode);
        Assert.Single(host.Requests);
    }

    private static Task<FixtureHost> Start(Func<FixtureHost.UpstreamRequest, HttpResponseMessage>? reply = null)
        => FixtureHost.StartAsync(reply ?? (request => FixtureHost.Reply(request.Path == "/v1/ChatCompletion" ? request with { Path = Chat } : request)),
            bastionTensorUnreal: true);

    private static void Options(JsonObject payload, string route, string provider)
    {
        var options = JsonNode.Parse($$"""{"MODEL":"wrong","future_option":{{Future}}}""");
        if (route == Vercel) payload["providerMetadata"] = new JsonObject { [provider] = options };
        else payload[provider] = options;
    }

    private static void AssertOptions(JsonNode body)
    {
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Future), body["future_option"]));
        Assert.Null(body["MODEL"]);
    }

    private static HttpResponseMessage Binary(string mime)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(mime);
        return response;
    }

    private static async Task<JsonNode> Body(HttpResponseMessage response) => JsonNode.Parse(await GatewayTests.Success(response))!;
}
