using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Streaik.Gateway.Tests;

public sealed class ASIOneRimeStabilityAITests
{
    private static readonly (string, string) ASI = ("X-Asione-Key", "asi-fixture");
    private static readonly (string, string) Rime = ("X-Rime-Key", "rime-fixture");
    private static readonly (string, string) Stability = ("X-Stabilityai-Key", "stability-fixture");

    [Fact]
    public async Task Catalogs_include_static_models_and_public_rime_voice_discovery()
    {
        await using var host = await FixtureHost.StartAsync(_ => FixtureHost.Json("""
            {"coda":{"en":["speaker","speaker"],"fr":["speaker"]},"Mistv3":{"es":["voz"]},"arcana":{"en":["old"]}}
            """), asiOneRimeStabilityAI: true);
        using var anonymous = await host.GetAsync("/v1/models");
        Assert.Empty((await Body(anonymous))["data"]!.AsArray());
        Assert.Empty(host.Requests);

        using var response = await host.GetAsync("/v1/models", ASI, Rime, Stability);
        var models = (await Body(response))["data"]!.AsArray().ToDictionary(node => (string)node!["id"]!);
        Assert.Equal(128_000, (int)models["asione/asi1"]!["context_window"]!);
        Assert.Equal("speech", (string?)models["rime/coda/speaker"]!["type"]);
        Assert.Contains("rime/mistv3/voz", models.Keys);
        Assert.Contains("rime/mistv3", models.Keys);
        Assert.Contains("rime/coda", models.Keys);
        Assert.DoesNotContain("rime/arcana/old", models.Keys);
        Assert.Equal(9, models.Keys.Count(id => id.StartsWith("stabilityai/", StringComparison.Ordinal)));
        Assert.Equal("image", (string?)models["stabilityai/stable-image-core"]!["type"]);
        Assert.Equal("speech", (string?)models["stabilityai/stable-audio-3"]!["type"]);
        var discovery = Assert.Single(host.Requests);
        Assert.Equal("users.rime.ai", discovery.Host);
        Assert.Equal("/data/voices/all-v2.json", discovery.Path);
        Assert.False(discovery.Headers.ContainsKey("Authorization"));
    }

    [Theory]
    [InlineData("/v1/chat/completions", false)]
    [InlineData("/v1/chat/completions", true)]
    [InlineData("/v1/responses", false)]
    [InlineData("/v1/messages", true)]
    [InlineData("/api/chat", true)]
    public async Task ASIOne_uses_one_native_chat_endpoint_and_round_trips_extensions(string route, bool stream)
    {
        await using var host = await FixtureHost.StartAsync(FixtureHost.Reply, asiOneRimeStabilityAI: true);
        const string native = """{"custom":{"values":[true,null]},"model":"wrong","stream":false,"authorization":"wrong"}""";
        var input = route switch
        {
            "/v1/responses" => "\"input\":\"hello\"",
            "/v1/messages" => "\"max_tokens\":64,\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}]",
            "/api/chat" => "\"messages\":[{\"id\":\"u1\",\"role\":\"user\",\"parts\":[{\"type\":\"text\",\"text\":\"hello\"}]}]",
            _ => "\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}]"
        };
        var options = route == "/api/chat" ? "\"providerMetadata\":{\"asione\":" + native + "}" : "\"asione\":" + native;
        using var response = await host.PostAsync(route,
            "{\"model\":\"asione/asi1\",\"stream\":" + (stream ? "true" : "false") + "," + input + "," + options + "}", ASI);
        var output = await GatewayTests.Success(response);
        Assert.Contains("hello", output);
        Assert.Contains("future_response", output);
        var request = Assert.Single(host.Requests);
        Assert.Equal("api.asi1.ai", request.Host);
        Assert.Equal("/v1/chat/completions", request.Path);
        Assert.Equal("Bearer asi-fixture", request.Headers["Authorization"]);
        Assert.Equal(stream, request.Stream);
        Assert.Equal("asi1", (string?)request.Body!["model"]);
        Assert.Null(request.Body["authorization"]);
        Assert.Null(request.Body["asione"]);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"values":[true,null]}"""), request.Body["custom"]));
    }

    [Fact]
    public async Task Rime_coda_preserves_native_json_and_prioritizes_routed_voice_and_typed_language()
    {
        await using var host = await FixtureHost.StartAsync(_ => Binary("audio/webm;codecs=opus"), asiOneRimeStabilityAI: true);
        using var response = await host.PostAsync("/api/speech", """
            {"model":"rime/coda/speaker","text":"Speak this","voice":"other","language":"eng","outputFormat":"opus",
             "providerOptions":{"rime":{"language":"fr","modelId":"bad","text":"bad","authorization":"bad","timeScaleFactor":0.8,
                                        "customLexicon":[{"spelling":"AI","pronunciation":"ay eye"}],"vendor":{"values":[true,null]}}}}
            """, Rime);
        var output = await Body(response);
        Assert.Equal("AQIDBA==", (string?)output["audio"]!["base64"]);
        Assert.Equal("webm", (string?)output["audio"]!["format"]);
        Assert.Equal("speaker", (string?)output["providerMetadata"]!["rime"]!["voice"]);
        var request = Assert.Single(host.Requests);
        Assert.Equal("coda.api.rime.ai", request.Host);
        Assert.Equal("/", request.Path);
        Assert.Equal("Bearer rime-fixture", request.Headers["Authorization"]);
        Assert.Equal("coda", (string?)request.Body!["modelId"]);
        Assert.Equal("en", (string?)request.Body["lang"]);
        Assert.Equal("speaker", (string?)request.Body["speaker"]);
        Assert.Equal("Speak this", (string?)request.Body["text"]);
        Assert.Equal("AI", (string?)request.Body["customLexicon"]![0]!["spelling"]);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"values":[true,null]}"""), request.Body["vendor"]));
        Assert.Null(request.Body["authorization"]);
    }

    [Fact]
    public async Task Rime_mist_streams_aligned_pcm_with_reported_rate_and_rejects_mixed_voice_scripts()
    {
        await using var host = await FixtureHost.StartAsync(_ => Binary("audio/L16;rate=22050", split: true), asiOneRimeStabilityAI: true);
        using var response = await host.PostAsync("/api/speech/stream", """
            {"model":"rime/mistv3","voice":"speaker","text":[{"type":"text","text":"Hel"},{"type":"text","text":"lo"}],
             "outputFormat":{"type":"pcm","rate":16000},"includeRawChunks":true,
             "providerOptions":{"rime":{"samplingRate":24000,"pauseBetweenBrackets":true,"phonemizeBetweenBrackets":true}}}
            """, Rime);
        var frames = (await GatewayTests.Success(response)).Split('\n').Where(line => line.StartsWith("data: ", StringComparison.Ordinal))
            .Select(line => JsonNode.Parse(line[6..])!).ToArray();
        Assert.Equal(["stream-start", "response-metadata", "raw", "audio", "raw", "audio", "finish"], frames.Select(frame => (string?)frame["type"]));
        Assert.Equal("AQI=", (string?)frames[3]["audio"]);
        Assert.Equal("AwQ=", (string?)frames[5]["audio"]);
        Assert.Equal("mock-upstream", (string?)frames[1]["headers"]!["x-request-id"]);
        Assert.Equal(22050, (int)frames[^1]["providerMetadata"]!["rime"]!["samplingRate"]!);
        var request = Assert.Single(host.Requests);
        Assert.Equal("users.rime.ai", request.Host);
        Assert.Equal("/v1/rime-tts", request.Path);
        Assert.Equal("Hello", (string?)request.Body!["text"]);
        Assert.Equal(16000, (int)request.Body["samplingRate"]!);
        Assert.True((bool)request.Body["pauseBetweenBrackets"]!);

        using var mixed = await host.PostAsync("/api/speech/stream", """
            {"model":"rime/mistv3","text":[{"type":"text","text":"a","voice":"one"},{"type":"text","text":"b","voice":"two"}]}
            """, Rime);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, mixed.StatusCode);
        Assert.Single(host.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rime_openai_audio_supports_binary_and_sse(bool stream)
    {
        await using var host = await FixtureHost.StartAsync(_ => Binary("audio/wav"), asiOneRimeStabilityAI: true);
        using var response = await host.PostAsync("/v1/audio/speech", $$"""
            {"model":"rime/mistv3","input":"Hello","response_format":"wav","voice":"speaker","stream_format":"{{(stream ? "sse" : "audio")}}"}
            """, Rime);
        if (stream)
        {
            var output = await GatewayTests.Success(response);
            Assert.Contains("speech.audio.delta", output);
            Assert.Contains("AQIDBA==", output);
            Assert.Contains("speech.audio.done", output);
        }
        else
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("audio/wav", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, await response.Content.ReadAsByteArrayAsync());
        }
        var request = Assert.Single(host.Requests);
        Assert.Equal("speaker", (string?)request.Body!["speaker"]);
        Assert.Equal("audio/wav", request.Headers["Accept"]);
        Assert.Null(request.Body["stream_format"]);
    }

    [Theory]
    [InlineData("stable-image-core", "core")]
    [InlineData("stable-image-ultra", "ultra")]
    [InlineData("sd3.5-large-turbo", "sd3")]
    public async Task Stability_images_use_native_multipart_and_preserve_metadata(string model, string endpoint)
    {
        await using var host = await FixtureHost.StartAsync(_ => Binary("image/webp"), asiOneRimeStabilityAI: true);
        using var response = await host.PostAsync("/api/images", $$$$$"""
            {"model":"stabilityai/{{{{{model}}}}}","prompt":"A tree","n":2,"size":"1920x1080","seed":17,
             "providerOptions":{"stabilityai":{"output_format":"webp","mode":"image-to-image","model":"wrong","authorization":"wrong",
               "aspect_ratio":"1:1","negative_prompt":"fog","vendor":{"values":[true,null]}}}}
            """, Stability);
        var result = await Body(response);
        Assert.Equal("data:image/webp;base64,AQIDBA==", (string?)Assert.Single(result["images"]!.AsArray()));
        Assert.Equal("42", (string?)result["providerMetadata"]!["stabilityai"]!["seed"]);
        Assert.Equal("n", (string?)result["warnings"]![0]!["feature"]);
        var request = Assert.Single(host.Requests);
        Assert.Equal("api.stability.ai", request.Host);
        Assert.Equal($"/v2beta/stable-image/generate/{endpoint}", request.Path);
        Assert.Equal("Bearer stability-fixture", request.Headers["Authorization"]);
        var fields = FormFields(request);
        Assert.Equal("A tree", fields["prompt"]);
        Assert.Equal("16:9", fields["aspect_ratio"]);
        Assert.Equal("webp", fields["output_format"]);
        Assert.Equal("17", fields["seed"]);
        Assert.Equal("fog", fields["negative_prompt"]);
        Assert.Equal("""{"values":[true,null]}""", fields["vendor"]);
        Assert.False(fields.ContainsKey("authorization"));
        Assert.Equal(endpoint == "sd3" ? model : null, fields.GetValueOrDefault("model"));
        Assert.Equal(endpoint == "sd3" ? "text-to-image" : null, fields.GetValueOrDefault("mode"));
    }

    [Fact]
    public async Task Stability_media_streams_only_completed_outputs_and_routes_audio_and_image_separately()
    {
        await using var host = await FixtureHost.StartAsync(request => Binary(request.Path.Contains("/audio/", StringComparison.Ordinal)
            ? "audio/wav" : "image/png"), asiOneRimeStabilityAI: true);
        using var images = await host.PostAsync("/v1/images/generations", """
            {"model":"stabilityai/stable-image-ultra","prompt":"A tree","stream":true}
            """, Stability);
        var imageFrames = await GatewayTests.Success(images);
        Assert.Contains("image_generation.completed", imageFrames);
        Assert.DoesNotContain("image_generation.partial_image", imageFrames);
        using var voice = await host.PostAsync("/v1/audio/speech", """
            {"model":"stabilityai/stable-audio-2.5","input":"Birdsong","response_format":"wav","stream_format":"sse",
             "stabilityai":{"model":"wrong","prompt":"wrong","duration":2.5,"cfg_scale":7}}
            """, Stability);
        var audioFrames = await GatewayTests.Success(voice);
        Assert.Contains("speech.audio.delta", audioFrames);
        Assert.Contains("AQIDBA==", audioFrames);
        Assert.Contains("speech.audio.done", audioFrames);
        var audioRequest = host.Requests.Last();
        Assert.Equal("/v2beta/audio/stable-audio-2/text-to-audio", audioRequest.Path);
        var fields = FormFields(audioRequest);
        Assert.Equal("stable-audio-2.5", fields["model"]);
        Assert.Equal("Birdsong", fields["prompt"]);
        Assert.Equal("2.5", fields["duration"]);
        Assert.Equal("7", fields["cfg_scale"]);

        using var chat = await host.PostAsync("/v1/chat/completions", """
            {"model":"stabilityai/stable-audio-3","messages":[{"role":"user","content":"A piano"}],"stabilityai":{"duration":1}}
            """, Stability);
        Assert.Contains("AQIDBA==", await GatewayTests.Success(chat));
        Assert.Equal("1", FormFields(host.Requests.Last())["duration"]);

        using var imageChat = await host.PostAsync("/v1/chat/completions", """
            {"model":"stabilityai/stable-image-core","messages":[{"role":"user","content":"A tree"}]}
            """, Stability);
        Assert.Contains("AQIDBA==", await GatewayTests.Success(imageChat));
        Assert.Equal("/v2beta/stable-image/generate/core", host.Requests.Last().Path);

        using var edit = await host.PostAsync("/v1/images/edits", """
            {"model":"stabilityai/stable-image-core","prompt":"A tree","images":[{"image_url":"data:image/png;base64,AQIDBA=="}]}
            """, Stability);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, edit.StatusCode);
        Assert.Equal(4, host.Requests.Count);
    }

    private static async Task<JsonNode> Body(HttpResponseMessage response) => JsonNode.Parse(await GatewayTests.Success(response))!;

    private static Dictionary<string, string> FormFields(FixtureHost.UpstreamRequest request)
        => Regex.Matches(request.RawBody!, "(?im)^Content-Disposition: form-data; name=\"?(?<name>[a-z0-9_.-]+)\"?\\r\\n(?:[^\\r\\n]+\\r\\n)*\\r\\n(?<value>[^\\r\\n]*)")
            .ToDictionary(match => match.Groups["name"].Value, match => match.Groups["value"].Value);

    private static HttpResponseMessage Binary(string type, bool split = false)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        { Content = split ? new StreamContent(new SplitAudio()) : new ByteArrayContent([1, 2, 3, 4]) };
        response.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(type);
        response.Headers.Add("seed", "42");
        response.Headers.Add("x-request-id", "mock-upstream");
        return response;
    }

    private sealed class SplitAudio() : MemoryStream([1, 2, 3, 4])
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => base.ReadAsync(buffer[..Math.Min(3, buffer.Length)], cancellationToken);
    }
}
