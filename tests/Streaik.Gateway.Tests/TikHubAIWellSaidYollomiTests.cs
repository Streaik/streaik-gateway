using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Xunit;

namespace Streaik.Gateway.Tests;

public sealed class TikHubAIWellSaidYollomiTests
{
    private static readonly (string, string) TikHubKey = ("x-tikhubai-key", "tik-fixture");
    private static readonly (string, string) WellSaidKey = ("x-wellsaid-key", "well-fixture");
    private static readonly (string, string) YollomiKey = ("x-yollomi-key", "yollomi-fixture");

    [Fact]
    public async Task Lists_native_models_with_gateway_ids_and_correct_authentication()
    {
        await using var host = await FixtureHost.StartAsync(request => request.Host switch
        {
            "api.wellsaidlabs.com" => FixtureHost.Json("""{"avatars":[{"id":42,"name":"Fixture Avatar"},{"id":"invalid"}]}"""),
            "yollomi.com" => FixtureHost.Json("""{"image":[{"modelId":"vendor/image"}],"video":[{"modelId":"movie","displayName":"Movie"}]}"""),
            _ => FixtureHost.Reply(request)
        }, tikHubAIWellSaidYollomi: true);
        using var response = await host.GetAsync("/v1/models", TikHubKey, WellSaidKey, YollomiKey);
        var models = JsonNode.Parse(await GatewayTests.Success(response))!["data"]!.AsArray()
            .ToDictionary(item => (string)item!["id"]!);
        Assert.Equal("fixture", (string?)models["tikhubai/vendor/Model"]!["owned_by"]);
        Assert.Equal("Fixture Avatar", (string?)models["wellsaid/42"]!["name"]);
        Assert.Equal("speech", (string?)models["wellsaid/42"]!["type"]);
        Assert.Equal("image", (string?)models["yollomi/vendor/image"]!["type"]);
        Assert.Equal("video", (string?)models["yollomi/movie"]!["type"]);
        var tik = Assert.Single(host.Requests, request => request.Host == "ai.tikhub.io");
        Assert.Equal("/v1/models", tik.Path);
        Assert.Equal("Bearer tik-fixture", tik.Headers["Authorization"]);
        var well = Assert.Single(host.Requests, request => request.Host == "api.wellsaidlabs.com");
        Assert.Equal("/v1/tts/avatars", well.Path);
        Assert.Equal("well-fixture", well.Headers["X-Api-Key"]);
        var yollomi = Assert.Single(host.Requests, request => request.Host == "yollomi.com");
        Assert.Equal("/api/v1/models", yollomi.Path);
        Assert.False(yollomi.Headers.ContainsKey("Authorization"));
    }

    [Theory]
    [InlineData("chat/completions", false)]
    [InlineData("chat/completions", true)]
    [InlineData("responses", false)]
    [InlineData("responses", true)]
    [InlineData("messages", false)]
    [InlineData("messages", true)]
    public async Task TikHubAI_forwards_native_protocols_streaming_and_nested_options(string protocol, bool stream)
    {
        await using var host = await FixtureHost.StartAsync(tikHubAIWellSaidYollomi: true);
        var body = new JsonObject
        {
            ["model"] = "tikhubai/vendor/Model", ["stream"] = stream,
            ["tikhubai"] = JsonNode.Parse("""{"model":"bogus","stream":false,"headers":{"bad":"x"},"vendor_config":{"mode":[true,2]}}""")
        };
        if (protocol == "responses") body["input"] = "hello";
        else body["messages"] = JsonNode.Parse("""[{"role":"user","content":"hello"}]""");
        if (protocol == "messages") body["max_tokens"] = 32;
        using var response = await host.PostAsync($"/v1/{protocol}", body.ToJsonString(), TikHubKey,
            ("anthropic-version", "2023-06-01"), ("anthropic-beta", "fixture-beta"));
        var wire = await GatewayTests.Success(response);
        Assert.Contains("hello", wire);
        Assert.Contains("tikhubai/vendor/Model", wire);
        if (!stream || protocol != "messages") Assert.Contains("future_response", wire);
        else Assert.Contains("future_content", wire);
        var call = Assert.Single(host.Requests);
        Assert.Equal("ai.tikhub.io", call.Host);
        Assert.Equal($"/v1/{protocol}", call.Path);
        Assert.Equal("Bearer tik-fixture", call.Headers["Authorization"]);
        Assert.Equal(stream, call.Stream);
        Assert.Equal("vendor/Model", (string?)call.Body!["model"]);
        Assert.True((bool?)call.Body["vendor_config"]!["mode"]![0]);
        Assert.Null(call.Body["tikhubai"]);
        Assert.Null(call.Body["headers"]);
        if (protocol == "messages")
        {
            Assert.Equal("2023-06-01", call.Headers["anthropic-version"]);
            Assert.Equal("fixture-beta", call.Headers["anthropic-beta"]);
        }
    }

    [Fact]
    public async Task TikHubAI_Vercel_chat_uses_native_Responses()
    {
        await using var host = await FixtureHost.StartAsync(tikHubAIWellSaidYollomi: true);
        using var response = await host.PostAsync("/api/chat", """
            {"model":"tikhubai/vendor/Model","messages":[{"id":"u1","role":"user","parts":[{"type":"text","text":"Hi"}]}],
             "providerMetadata":{"tikhubai":{"vendor_config":{"enabled":true}}}}
            """, TikHubKey);
        Assert.Contains("hello", await GatewayTests.Success(response));
        var call = Assert.Single(host.Requests);
        Assert.Equal("/v1/responses", call.Path);
        Assert.True(call.Stream);
        Assert.True((bool?)call.Body!["vendor_config"]!["enabled"]);
    }

    [Fact]
    public async Task WellSaid_synthesizes_MP3_and_streams_existing_speech_contracts()
    {
        await using var host = await FixtureHost.StartAsync(_ =>
        {
            var audio = Bytes("audio/mpeg");
            audio.Headers.Add("x-request-id", "speech-1");
            audio.Headers.TryAddWithoutValidation("Set-Cookie", "private=fixture");
            return audio;
        }, tikHubAIWellSaidYollomi: true);
        using var native = await host.PostAsync("/v1/audio/speech", """
            {"model":"wellsaid/42","input":"say this","providerOptions":{"wellsaid":{"speaker_id":11,"text":"bad","style":{"pace":[1,2]}}}}
            """, WellSaidKey);
        Assert.Equal(HttpStatusCode.OK, native.StatusCode);
        Assert.Equal("audio/mpeg", native.Content.Headers.ContentType?.MediaType);
        Assert.Equal(new byte[] { 1, 2, 3 }, await native.Content.ReadAsByteArrayAsync());

        using var nativeSse = await host.PostAsync("/v1/audio/speech", """
            {"model":"wellsaid/42","input":"say this","stream_format":"sse"}
            """, WellSaidKey);
        var events = await GatewayTests.Success(nativeSse);
        Assert.Contains("speech.audio.delta", events);
        Assert.Contains("AQID", events);
        Assert.Contains("speech.audio.done", events);

        using var speech = await host.PostAsync("/api/speech", """
            {"model":"wellsaid/42","text":"say this","providerOptions":{"wellsaid":{"style":{"pace":[1,2]}}}}
            """, WellSaidKey);
        var speechWire = await GatewayTests.Success(speech);
        var vercel = JsonNode.Parse(speechWire)!;
        Assert.Equal("mp3", (string?)vercel["audio"]!["format"]);
        Assert.Equal("AQID", (string?)vercel["audio"]!["base64"]);
        var responseHeaders = vercel["providerMetadata"]!["wellsaid"]!["headers"]!.AsObject();
        var requestId = Assert.Single(responseHeaders, header => header.Key.Equals("x-request-id", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("speech-1", (string?)requestId.Value);
        Assert.DoesNotContain("private=fixture", speechWire);

        using var responses = await host.PostAsync("/v1/responses", """
            {"model":"wellsaid/42","input":"say this","stream":true}
            """, WellSaidKey);
        Assert.Contains("AQID", await GatewayTests.Success(responses));

        var calls = host.Requests.ToArray();
        Assert.Equal(4, calls.Length);
        Assert.All(calls, call =>
        {
            Assert.Equal("api.wellsaidlabs.com", call.Host);
            Assert.Equal("/v1/tts/stream", call.Path);
            Assert.Equal("well-fixture", call.Headers["X-Api-Key"]);
            Assert.Equal("audio/mpeg", call.Headers["Accept"]);
            Assert.Equal(42, (int)call.Body!["speaker_id"]!);
            Assert.Equal("say this", (string?)call.Body["text"]);
            Assert.Null(call.Body["model"]);
        });
        Assert.Equal(2, (int)calls[0].Body!["style"]!["pace"]![1]!);
        Assert.Equal(2, (int)calls[2].Body!["style"]!["pace"]![1]!);
    }

    [Fact]
    public async Task WellSaid_rejects_unsupported_format_before_contacting_upstream()
    {
        await using var host = await FixtureHost.StartAsync(tikHubAIWellSaidYollomi: true);
        using var response = await host.PostAsync("/v1/audio/speech", """
            {"model":"wellsaid/42","input":"test","response_format":"wav"}
            """, WellSaidKey);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(host.Requests);
    }

    [Fact]
    public async Task Yollomi_generates_through_Vercel_with_raw_nested_metadata_and_credential_free_download()
    {
        await using var host = await FixtureHost.StartAsync(request => request.Method == "GET" ? Asset() : FixtureHost.Json("""
            {"images":["https://assets.yollomi.com/result.png?signature=fixture"],"requestId":"request-1","diagnostics":{"model":[{"credits":2}]}}
            """), tikHubAIWellSaidYollomi: true);
        using var response = await host.PostAsync("/api/images", """
            {"model":"yollomi/vendor/image","prompt":"Clouds","aspectRatio":"3:2","seed":7,"n":1,
             "providerOptions":{"yollomi":{"type":"video","modelId":"replaced","style":{"palette":["blue"]}}}}
            """, YollomiKey);
        var wire = await GatewayTests.Success(response);
        var result = JsonNode.Parse(wire)!;
        Assert.Equal("data:image/png;base64,AQID", (string?)result["images"]![0]);
        var metadata = result["providerMetadata"]!["yollomi"]!;
        Assert.Equal("request-1", (string?)metadata["requestId"]);
        Assert.Equal(2, (int)metadata["diagnostics"]!["model"]![0]!["credits"]!);
        Assert.DoesNotContain("signature=fixture", wire);
        var call = Assert.Single(host.Requests, request => request.Method == "POST");
        Assert.Equal("/api/v1/generate", call.Path);
        Assert.Equal("Bearer yollomi-fixture", call.Headers["Authorization"]);
        Assert.Equal("image", (string?)call.Body!["type"]);
        Assert.Equal("vendor/image", (string?)call.Body["modelId"]);
        Assert.Equal("3:2", (string?)call.Body["aspectRatio"]);
        Assert.Equal(7, (int)call.Body["seed"]!);
        Assert.Equal(1, (int)call.Body["numOutputs"]!);
        Assert.Equal("blue", (string?)call.Body["style"]!["palette"]![0]);
        var download = Assert.Single(host.Requests, request => request.Method == "GET");
        Assert.Equal("assets.yollomi.com", download.Host);
        Assert.False(download.Headers.ContainsKey("Authorization"));
        Assert.False(download.Headers.ContainsKey("x-yollomi-key"));
    }

    [Fact]
    public async Task Yollomi_preserves_multiple_edit_inputs_and_mask_with_existing_completion_event()
    {
        await using var host = await FixtureHost.StartAsync(_ => FixtureHost.Json("""{"image":"data:image/webp;base64,AQID"}"""),
            tikHubAIWellSaidYollomi: true);
        using var response = await host.PostAsync("/v1/images/edits", """
            {"model":"yollomi/vendor/image","prompt":"Clouds","stream":true,"images":[{"image_url":"https://example.test/input.png"},
             {"image_url":"Ag=="}],"mask":{"image_url":"data:image/png;base64,Aw=="}}
            """, YollomiKey);
        var events = await GatewayTests.Success(response);
        Assert.Contains("image_edit.completed", events);
        Assert.Contains("AQID", events);
        Assert.Contains("webp", events);
        var body = Assert.Single(host.Requests).Body!;
        Assert.Equal("https://example.test/input.png", (string?)body["imageUrl"]);
        Assert.Equal("https://example.test/input.png", (string?)body["image"]);
        Assert.Equal("data:image/png;base64,Ag==", (string?)body["images"]![1]);
        Assert.Equal("data:image/png;base64,Aw==", (string?)body["mask"]);
        Assert.Null(body["stream"]);
    }

    [Fact]
    public async Task Yollomi_forwards_multipart_inputs_and_standard_image_options()
    {
        await using var host = await FixtureHost.StartAsync(_ => FixtureHost.Json("""{"images":["AQID"],"trace":{"id":"edit-1"}}"""),
            tikHubAIWellSaidYollomi: true);
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("yollomi/vendor/image"), "model");
        form.Add(new StringContent("Clouds"), "prompt");
        form.Add(new StringContent("high"), "quality");
        form.Add(new StringContent("transparent"), "background");
        using var image = new ByteArrayContent([4, 5]);
        image.Headers.ContentType = new("image/png");
        form.Add(image, "image[]", "source.png");
        using var response = await host.PostFormAsync("/v1/images/edits", form, YollomiKey);
        var result = JsonNode.Parse(await GatewayTests.Success(response))!;
        Assert.Equal("AQID", (string?)result["data"]![0]!["b64_json"]);
        Assert.Equal("edit-1", (string?)result["providerMetadata"]!["yollomi"]!["trace"]!["id"]);
        Assert.Equal("high", (string?)result["quality"]);
        var body = Assert.Single(host.Requests).Body!;
        Assert.Equal("data:image/png;base64,BAU=", (string?)body["imageUrl"]);
        Assert.Equal("high", (string?)body["quality"]);
        Assert.Equal("transparent", (string?)body["background"]);
    }

    [Fact]
    public async Task Yollomi_rejects_untrusted_asset_hosts_without_fetching()
    {
        await using var host = await FixtureHost.StartAsync(_ => FixtureHost.Json("""{"image":"https://unconfigured.example/result.png"}"""),
            tikHubAIWellSaidYollomi: true);
        using var response = await host.PostAsync("/v1/images/generations", """{"model":"yollomi/vendor/image","prompt":"Clouds"}""", YollomiKey);
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("POST", Assert.Single(host.Requests).Method);
    }

    private static HttpResponseMessage Asset() => Bytes("image/png");

    private static HttpResponseMessage Bytes(string mediaType)
    {
        var content = new ByteArrayContent([1, 2, 3]);
        content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        return new(HttpStatusCode.OK) { Content = content };
    }
}
