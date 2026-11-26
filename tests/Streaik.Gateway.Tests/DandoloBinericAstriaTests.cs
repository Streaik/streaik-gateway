using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace Streaik.Gateway.Tests;

public sealed class DandoloBinericAstriaTests
{
    private static readonly (string, string) Dandolo = ("X-Dandolo-Key", "dandolo-fixture");
    private static readonly (string, string) Bineric = ("X-Bineric-Key", "bineric-fixture");
    private static readonly (string, string) Astria = ("X-Astria-Key", "astria-fixture");
    private const string Catalog = """
        {"models":{"image-model":{"tune_id":42}},"default":"image-model","video_model_families":{"motion":{"title":"Motion"}}}
        """;

    [Fact]
    public async Task Catalogs_use_native_paths_credentials_and_modalities()
    {
        await using var host = await FixtureHost.StartAsync(upstream => upstream.Path switch
        {
            "/api/v1/models" => FixtureHost.Json("""{"data":[{"id":"vendor/chat","owned_by":"vendor"}]}"""),
            "/api/v1/ai/models" => FixtureHost.Json("""{"data":[{"id":"speech-to-text","name":"Speech to Text","provider":"Speech vendor"}]}"""),
            "/cli/models" => FixtureHost.Json(Catalog),
            _ => throw new InvalidOperationException(upstream.Path)
        }, dandoloBinericAstria: true);
        using var anonymous = await host.GetAsync("/v1/models");
        Assert.Empty((await Body(anonymous))["data"]!.AsArray());
        Assert.Empty(host.Requests);
        using var models = await host.GetAsync("/v1/models", Dandolo, Bineric, Astria);
        var catalog = (await Body(models))["data"]!.AsArray().ToDictionary(item => (string)item!["id"]!);
        Assert.Equal("vendor", (string?)catalog["dandolo/vendor/chat"]!["owned_by"]);
        Assert.Equal("transcription", (string?)catalog["bineric/speech-to-text"]!["type"]);
        Assert.Equal("speech", (string?)catalog["bineric/text-to-speech"]!["type"]);
        Assert.Equal("image", (string?)catalog["astria/42"]!["type"]);
        Assert.Equal("video", (string?)catalog["astria/motion"]!["type"]);
        Assert.Equal("Bearer dandolo-fixture", host.Requests.Single(x => x.Host == "api.dandolo.ai").Headers["Authorization"]);
        AssertBinericAuth(host.Requests.Single(x => x.Host == "api.bineric.com"));
        Assert.Empty(host.Requests.Single(x => x.Host == "api.astria.ai").Headers);
    }

    [Theory]
    [InlineData("/v1/chat/completions", false)]
    [InlineData("/v1/chat/completions", true)]
    [InlineData("/v1/responses", false)]
    [InlineData("/v1/messages", true)]
    [InlineData("/api/chat", true)]
    public async Task Dandolo_bridges_text_without_losing_native_options(string route, bool stream)
    {
        await using var host = await FixtureHost.StartAsync(ChatReply, dandoloBinericAstria: true);
        const string native = """{"custom":{"values":[true,null]},"model":"forged","stream":false,"authorization":"forged"}""";
        var turn = route switch
        {
            "/v1/responses" => "\"input\":\"hello\"",
            "/v1/messages" => "\"max_tokens\":256,\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}]",
            "/api/chat" => "\"messages\":[{\"id\":\"user-1\",\"role\":\"user\",\"parts\":[{\"type\":\"text\",\"text\":\"hello\"}]}]",
            _ => "\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}]"
        };
        var options = route == "/api/chat" ? "\"providerMetadata\":{\"dandolo\":" + native + "}" : "\"dandolo\":" + native;
        using var response = await host.PostAsync(route,
            "{\"model\":\"dandolo/vendor/chat\",\"stream\":" + (stream ? "true" : "false") + "," + turn + "," + options + "}", Dandolo);
        var output = await GatewayTests.Success(response);
        Assert.Contains("hello from chat", output);
        Assert.Contains("upstream_native", output);
        var request = Assert.Single(host.Requests);
        Assert.Equal("api.dandolo.ai", request.Host);
        Assert.Equal("/api/v1/chat/completions", request.Path);
        Assert.Equal("Bearer dandolo-fixture", request.Headers["Authorization"]);
        Assert.Equal("vendor/chat", (string?)request.Body!["model"]);
        Assert.Equal(stream, request.Stream);
        Assert.Null(request.Body["authorization"]);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"values":[true,null]}"""), request.Body["custom"]));
    }

    [Fact]
    public async Task Bineric_chat_uses_the_same_native_key_and_preserves_unknown_fields()
    {
        await using var host = await FixtureHost.StartAsync(ChatReply, dandoloBinericAstria: true);
        using var response = await host.PostAsync("/v1/chat/completions", """
            {"model":"bineric/llm","stream":true,"messages":[{"role":"user","content":"hello"}],"bineric":{"extra":{"enable":true}}}
            """, Bineric);
        var output = await GatewayTests.Success(response);
        Assert.Contains("upstream_native", output);
        Assert.Contains("[DONE]", output);
        var request = Assert.Single(host.Requests);
        Assert.Equal("/api/v1/ai/chat/completions", request.Path);
        AssertBinericAuth(request);
        Assert.True((bool)request.Body!["extra"]!["enable"]!);
    }

    [Fact]
    public async Task Bineric_speech_keeps_native_settings_audio_and_metadata()
    {
        await using var host = await FixtureHost.StartAsync(_ => FixtureHost.Json("""
            {"choices":[{"message":{"content":{"audioContent":"data:audio/mpeg;base64,AQID","metadata":{"format":"mp3","extra":{"values":[1,null]}}}}}],"trace":{"id":7}}
            """), dandoloBinericAstria: true);
        using var speech = await host.PostAsync("/api/speech", """
            {"model":"bineric/text-to-speech","text":"Say this","voice":"typed","language":"en","speed":1.2,"outputFormat":"wav",
             "providerOptions":{"bineric":{"voice":"raw","pitch":"+5Hz","messages":[],"stream":true,"vendor":{"array":[null,true]}}}}
            """, Bineric);
        var data = await Body(speech);
        Assert.Equal("AQID", (string?)data["audio"]!["base64"]);
        Assert.Equal("mp3", (string?)data["audio"]!["format"]);
        var native = data["providerMetadata"]!["bineric"]!;
        Assert.Equal(7, (int)native["trace"]!["id"]!);
        var content = native["choices"]![0]!["message"]!["content"]!;
        Assert.Null(content["audioContent"]);
        Assert.Equal(1, (int)content["metadata"]!["extra"]!["values"]![0]!);
        var req = Assert.Single(host.Requests);
        AssertBinericAuth(req);
        Assert.Equal("Say this", (string?)req.Body!["messages"]![0]!["content"]);
        Assert.Equal("typed", (string?)req.Body["options"]!["voice"]);
        Assert.Equal("en", (string?)req.Body["options"]!["language"]);
        Assert.Equal(1.2, (double)req.Body["options"]!["rate"]!, precision: 5);
        Assert.Equal("+5Hz", (string?)req.Body["options"]!["pitch"]);
        Assert.True((bool)req.Body["vendor"]!["array"]![1]!);
        Assert.Null(req.Body["stream"]);

        using var sse = await host.PostAsync("/v1/audio/speech", """
            {"model":"bineric/text-to-speech","input":"Second","voice":"native","stream_format":"sse"}
            """, Bineric);
        var stream = await GatewayTests.Success(sse);
        Assert.Equal("text/event-stream", sse.Content.Headers.ContentType?.MediaType);
        Assert.Contains("speech.audio.delta", stream);
        Assert.Contains("AQID", stream);
        Assert.Contains("speech.audio.done", stream);
        Assert.False(host.Requests.Last().Stream);
    }

    [Fact]
    public async Task Bineric_transcription_sends_an_attachment_and_maps_phrases_including_buffered_sse()
    {
        await using var host = await FixtureHost.StartAsync(_ => FixtureHost.Json("""
            {"choices":[{"message":{}}],"_bineric":{"phrases":[{"text":"transcript","startTime":"PT1.5S","duration":"PT2S","scores":[0.9]}],"extra":{"region":"test"}}}
            """), dandoloBinericAstria: true);
        using var transcription = await host.PostAsync("/api/transcriptions", """
            {"model":"bineric/speech-to-text","audio":"AQID","mediaType":"audio/wav","providerOptions":{"bineric":{"language":"en","model":"invalid","messages":[],"raw":{"numbers":[1,null]}}}}
            """, Bineric);
        var data = await Body(transcription);
        Assert.Equal("transcript", (string?)data["text"]);
        Assert.Equal(1.5, (double)data["segments"]![0]!["startSecond"]!);
        Assert.Equal(3.5, (double)data["segments"]![0]!["endSecond"]!);
        Assert.Equal("test", (string?)data["providerMetadata"]!["bineric"]!["_bineric"]!["extra"]!["region"]);
        var request = Assert.Single(host.Requests);
        AssertBinericAuth(request);
        Assert.Equal("speech-to-text", (string?)request.Body!["model"]);
        Assert.Equal("data:audio/wav;base64,AQID", (string?)request.Body["messages"]![0]!["attachments"]![0]);
        Assert.Equal("en", (string?)request.Body["language"]);
        Assert.Equal(1, (int)request.Body["raw"]!["numbers"]![0]!);

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("bineric/speech-to-text"), "model");
        form.Add(new StringContent("true"), "stream");
        var file = new ByteArrayContent([1, 2, 3]);
        file.Headers.ContentType = new("audio/wav");
        form.Add(file, "file", "speech.wav");
        using var response = await host.PostFormAsync("/v1/audio/transcriptions", form, Bineric);
        var sse = await GatewayTests.Success(response);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("transcript.text.delta", sse);
        Assert.Contains("transcript.text.done", sse);
        Assert.False(host.Requests.Last().Stream);
    }

    [Fact]
    public async Task Astria_images_poll_and_preserve_nested_multipart_options_and_response_metadata()
    {
        await using var host = await FixtureHost.StartAsync(request => request.Path switch
        {
            "/cli/models" => FixtureHost.Json(Catalog),
            "/tunes/42/prompts" => FixtureHost.Json("""[{"id":71}]"""),
            "/tunes/42/prompts/71" => FixtureHost.Json("""{"id":71,"images":["https://assets.astria.ai/p.png"],"cost_mc":1200,"native":{"array":[null,2]}}"""),
            "/p.png" => Binary("image/png"),
            _ => throw new InvalidOperationException(request.Path)
        }, dandoloBinericAstria: true);
        using var generate = await host.PostAsync("/api/images", """
            {"model":"astria/image-model","prompt":"A tree","n":1,"seed":16,"size":"640x480","aspectRatio":"4:3",
             "providerOptions":{"astria":{"text":"forged","fx":{"items":[true,"blue"]}},"other":{"do_not_forward":true}}}
            """, Astria);
        var data = await Body(generate);
        Assert.Equal("data:image/png;base64,AQID", (string?)data["images"]![0]);
        var metadata = data["providerMetadata"]!["astria"]!;
        Assert.Equal(0.012m, (decimal)metadata["cost"]!);
        Assert.Equal(2, (int)metadata["native"]!["array"]![1]!);
        var submit = host.Requests.Single(request => request.Method == "POST");
        Assert.Equal("Bearer astria-fixture", submit.Headers["Authorization"]);
        Assert.Contains("prompt[text]", submit.RawBody);
        Assert.Contains("A tree", submit.RawBody);
        Assert.DoesNotContain("forged", submit.RawBody);
        Assert.DoesNotContain("do_not_forward", submit.RawBody);
        Assert.Contains("prompt[seed]", submit.RawBody);
        Assert.Contains("prompt[aspect_ratio]", submit.RawBody);
        Assert.Contains("prompt[fx][items][]", submit.RawBody);
        Assert.Contains("prompt[w]", submit.RawBody);
        Assert.Empty(host.Requests.Last().Headers);

        using var edit = await host.PostAsync("/v1/images/edits", """
            {"model":"astria/42","prompt":"Edit","images":[{"image_url":"https://example.net/input.png"}],
             "mask":{"image_url":"data:image/png;base64,AA=="},"astria":{"input_image_url":"https://example.net/override.png"},"stream":true}
            """, Astria);
        Assert.Contains("image_edit.completed", await GatewayTests.Success(edit));
        var editBody = host.Requests.Last(request => request.Method == "POST").RawBody;
        Assert.Contains("prompt[input_image_url]", editBody);
        Assert.Contains("https://example.net/input.png", editBody);
        Assert.DoesNotContain("override", editBody);
        Assert.Contains("prompt[mask_image]", editBody);
    }

    [Fact]
    public async Task Astria_video_operation_handles_image_stage_completion_and_error()
    {
        var poll = 0;
        await using var host = await FixtureHost.StartAsync(request => request.Path switch
        {
            "/cli/models" => FixtureHost.Json(Catalog),
            "/tunes/42/prompts" => FixtureHost.Json("""{"id":"93","quote":{"units":[1,2]}}"""),
            "/tunes/42/prompts/93" => Interlocked.Increment(ref poll) switch
            {
                1 => FixtureHost.Json("""{"id":93,"images":["https://assets.astria.ai/preview.png"]}"""),
                2 => FixtureHost.Json("""{"id":93,"images":["https://assets.astria.ai/video.mp4"],"native":{"plan":"small"}}"""),
                _ => FixtureHost.Json("""{"id":93,"user_error":"Bad frame"}""")
            },
            "/preview.png" => Binary("image/png"),
            "/video.mp4" => Binary("video/mp4"),
            _ => throw new InvalidOperationException(request.Path)
        }, configure: services => services.AddStreaikReferences(), dandoloBinericAstria: true);
        using var start = await host.PostAsync("/api/videos", """
            {"model":"astria/motion","prompt":"Animate","duration":5,"generateAudio":false,"fps":30,
             "image":{"type":"url","url":"https://example.net/first.png"},
             "frameImages":[{"frameType":"last_frame","image":{"type":"file","mediaType":"image/png","data":"AA=="}}],
             "inputReferences":[{"type":"url","url":"https://example.net/reference.mp4","mediaType":"video/mp4"},
                                {"type":"url","url":"https://example.net/reference.png","mediaType":"image/png"}],
             "providerOptions":{"astria":{"native":{"modes":["one","two"]},"video_model":"forged"}}}
            """, Astria);
        var started = await Body(start);
        var operation = (string)started["operation"]!;
        Assert.StartsWith("astria/", operation);
        Assert.DoesNotContain("42:93", operation);
        Assert.Equal("fps", (string?)started["warnings"]![0]!["feature"]);
        Assert.Equal(2, (int)started["providerMetadata"]!["astria"]!["quote"]!["units"]![1]!);
        var submit = host.Requests.Single(request => request.Method == "POST");
        Assert.Contains("prompt[video_first_frame_url]", submit.RawBody);
        Assert.Contains("prompt[video_last_frame]", submit.RawBody);
        Assert.Contains("prompt[input_video_url]", submit.RawBody);
        Assert.Contains("prompt[image_reference_urls][]", submit.RawBody);
        Assert.Contains("prompt[native][modes][]", submit.RawBody);
        Assert.DoesNotContain("forged", submit.RawBody);
        using var pending = await host.GetAsync("/api/videos/" + operation, Astria);
        Assert.Equal("pending", (string?)(await Body(pending))["status"]);
        using var complete = await host.GetAsync("/api/videos/" + operation, Astria);
        var video = await Body(complete);
        Assert.Equal("completed", (string?)video["status"]);
        Assert.Equal("video/mp4", (string?)video["videos"]![0]!["mediaType"]);
        Assert.Equal("AQID", (string?)video["videos"]![0]!["data"]);
        Assert.Equal("small", (string?)video["providerMetadata"]!["astria"]!["native"]!["plan"]);
        Assert.Empty(host.Requests.Last().Headers);
        using var error = await host.GetAsync("/api/videos/" + operation, Astria);
        Assert.Equal("Bad frame", (string?)(await Body(error))["error"]);
    }

    [Theory]
    [InlineData("/api/chat", "\"messages\":[{\"id\":\"user-1\",\"role\":\"user\",\"parts\":[{\"type\":\"text\",\"text\":\"Move\"}]}]", "tool-output-available")]
    [InlineData("/v1/chat/completions", "\"messages\":[{\"role\":\"user\",\"content\":\"Move\"}]", "provider_executed")]
    [InlineData("/v1/responses", "\"input\":\"Move\"", "response.completed")]
    public async Task Astria_video_chat_aliases_stream_the_existing_tool_result(string route, string input, string terminal)
    {
        await using var host = await FixtureHost.StartAsync(request => request.Path switch
        {
            "/cli/models" => FixtureHost.Json(Catalog),
            "/tunes/42/prompts" => FixtureHost.Json("""{"id":93}"""),
            "/tunes/42/prompts/93" => FixtureHost.Json("""{"id":93,"images":["https://assets.astria.ai/video.mp4"],"native":{"kept":true}}"""),
            "/video.mp4" => Binary("video/mp4"),
            _ => throw new InvalidOperationException(request.Path)
        }, configure: services => services.AddStreaikReferences(), dandoloBinericAstria: true);
        var options = route == "/api/chat" ? "\"providerMetadata\":{\"astria\":{\"custom\":[1,2]}}" : "\"astria\":{\"custom\":[1,2]}";
        using var response = await host.PostAsync(route, "{\"model\":\"astria/motion\",\"stream\":true," + input + "," + options + "}", Astria);
        var output = await GatewayTests.Success(response);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains(terminal, output);
        Assert.Contains("generate_video", output);
        Assert.Contains("AQID", output);
        Assert.Contains("kept", output);
        Assert.Contains("prompt[custom][]", host.Requests.Single(request => request.Method == "POST").RawBody);
    }

    [Fact]
    public async Task Astria_rejects_an_untrusted_delivery_host_without_contacting_it()
    {
        await using var host = await FixtureHost.StartAsync(_ => FixtureHost.Json("""
            {"id":71,"images":["https://untrusted.example/p.png"]}
            """), dandoloBinericAstria: true);
        using var response = await host.PostAsync("/v1/images/generations", """{"model":"astria/42","prompt":"Draw"}""", Astria);
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("api.astria.ai", Assert.Single(host.Requests).Host);
    }

    private static HttpResponseMessage ChatReply(FixtureHost.UpstreamRequest request) => request.Stream
        ? FixtureHost.Sse("""
            {"id":"chat_1","object":"chat.completion.chunk","created":1,"model":"native","choices":[{"index":0,"delta":{"role":"assistant","content":"hello from chat"},"finish_reason":"stop"}],"upstream_native":{"future":[1,null]}}
            """, "[DONE]")
        : FixtureHost.Json("""
            {"id":"chat_1","object":"chat.completion","created":1,"model":"native","choices":[{"index":0,"message":{"role":"assistant","content":"hello from chat"},"finish_reason":"stop"}],"upstream_native":{"future":[1,null]}}
            """);

    private static void AssertBinericAuth(FixtureHost.UpstreamRequest req)
    {
        Assert.Equal("bineric-fixture", req.Headers["api-key"]);
        Assert.False(req.Headers.ContainsKey("Authorization"));
        Assert.False(req.Headers.ContainsKey(Bineric.Item1));
    }

    private static HttpResponseMessage Binary(string mime)
    {
        var content = new ByteArrayContent([1, 2, 3]);
        content.Headers.ContentType = new MediaTypeHeaderValue(mime);
        return new(HttpStatusCode.OK) { Content = content };
    }

    private static async Task<JsonNode> Body(HttpResponseMessage response) => JsonNode.Parse(await GatewayTests.Success(response))!;
}
