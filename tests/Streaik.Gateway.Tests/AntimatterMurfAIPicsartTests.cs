using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Streaik.Gateway.Tests;

public sealed class AntimatterMurfAIPicsartTests
{
    private static readonly (string, string) Antimatter = ("x-antimatter-key", "antimatter-fixture");
    private static readonly (string, string) Murf = ("x-murfai-key", "murf-fixture");
    private static readonly (string, string) Picsart = ("x-picsart-key", "picsart-fixture");

    [Fact]
    public async Task Catalog_preserves_native_ids_prices_and_model_families()
    {
        await using var host = await FixtureHost.StartAsync(Reply, antimatterMurfAIPicsart: true);
        using var anonymous = await host.GetAsync("/v1/models");
        var noKey = await GatewayTests.Success(anonymous);
        Assert.DoesNotContain("antimatter/", noKey);
        Assert.DoesNotContain("murfai/", noKey);
        Assert.DoesNotContain("picsart/", noKey);
        Assert.Empty(host.Requests);

        using var response = await host.GetAsync("/v1/models", Antimatter, Murf, Picsart);
        var models = JsonNode.Parse(await GatewayTests.Success(response))!["data"]!.AsArray().ToDictionary(model => (string)model!["id"]!);
        var chat = models["antimatter/lab/model"]!;
        Assert.Equal(123L, (long)chat["created"]!);
        Assert.Equal(4096, (int)chat["max_tokens"]!);
        Assert.Equal(0.0000025m, (decimal)chat["pricing"]!["input"]!);
        Assert.Equal("speech", (string?)models["murfai/gen2/en-US-natalie"]!["type"]);
        Assert.Contains("murfai/falcon-2.1/en-US-natalie", models.Keys);
        Assert.Contains("murfai/translate/es-ES", models.Keys);
        Assert.Equal(84, models.Keys.Count(id => id.StartsWith("picsart/", StringComparison.Ordinal)));
        Assert.Equal(3, host.Requests.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Antimatter_chat_preserves_native_options_usage_and_metadata(bool stream)
    {
        await using var host = await FixtureHost.StartAsync(Reply, antimatterMurfAIPicsart: true);
        using var response = await host.PostAsync("/v1/chat/completions", $$$$"""
            {"model":"antimatter/lab/model","messages":[{"role":"user","content":"hi"}],"stream":{{{{(stream ? "true" : "false")}}}},
              "antimatter":{"model":"other","stream":false,"headers":{"Authorization":"bad"},"native_config":{"exact":[1,null,true]}}}
            """, Antimatter);
        var wire = await GatewayTests.Success(response);
        Assert.Contains("hello", wire);
        Assert.Contains("future_response", wire);
        Assert.Contains("total_tokens", wire);
        var upstream = Assert.Single(host.Requests);
        Assert.Equal("/v1/chat/completions", upstream.Path);
        Assert.Equal("Bearer antimatter-fixture", upstream.Headers["Authorization"]);
        Assert.Equal("lab/model", (string?)upstream.Body!["model"]);
        Assert.Equal(stream, (bool)upstream.Body["stream"]!);
        Assert.True((bool)upstream.Body["native_config"]!["exact"]![2]!);
        Assert.Null(upstream.Body["headers"]);
        Assert.Null(upstream.Body["antimatter"]);
    }

    [Theory]
    [InlineData("/v1/responses", """{"model":"murfai/translate/es-ES","input":"Hello","murfai":{"language":"fr-FR","native_detail":{"value":true}}}""")]
    [InlineData("/v1/messages", """{"model":"murfai/translate/es-ES","max_tokens":64,"messages":[{"role":"user","content":"Hello"}]}""")]
    [InlineData("/api/chat", """{"model":"murfai/translate","messages":[{"id":"u","role":"user","parts":[{"type":"text","text":"Hello"}]}],"providerMetadata":{"murfai":{"language":"es-ES"}}}""")]
    public async Task Murf_translation_uses_protocol_adapters_and_returns_raw_metadata(string path, string request)
    {
        await using var host = await FixtureHost.StartAsync(Reply, antimatterMurfAIPicsart: true);
        using var response = await host.PostAsync(path, request, Murf);
        var wire = await GatewayTests.Success(response);
        Assert.Contains("Hola", wire);
        Assert.Contains("translation_metrics", wire);
        var upstream = Assert.Single(host.Requests);
        Assert.Equal("/v1/text/translate", upstream.Path);
        Assert.Equal("murf-fixture", upstream.Headers["api-key"]);
        Assert.Equal("es-ES", (string?)upstream.Body!["targetLanguage"]);
        Assert.Equal("Hello", (string?)upstream.Body["texts"]![0]);
        Assert.Null(upstream.Body["language"]);
        if (path == "/v1/responses") Assert.True((bool)upstream.Body["native_detail"]!["value"]!);
    }

    [Fact]
    public async Task Murf_batch_speech_preserves_native_word_timing_and_typed_inputs_win()
    {
        await using var host = await FixtureHost.StartAsync(Reply, antimatterMurfAIPicsart: true);
        using var response = await host.PostAsync("/api/speech", """
            {"model":"murfai/gen2/en-US-natalie","text":"Hello","speed":1.2,"language":"en-US",
             "providerOptions":{"murfai":{"text":"wrong","voiceId":"wrong","rate":-50,"wordDurationsAsOriginalText":true,
                "pronunciationDictionary":{"Hello":{"pronunciation":"hello"}}}}}
            """, Murf);
        var body = JsonNode.Parse(await GatewayTests.Success(response))!;
        Assert.Equal("AQID", (string?)body["audio"]!["base64"]);
        Assert.Equal("audio/wav", (string?)body["audio"]!["mediaType"]);
        Assert.Equal("Hello", (string?)body["providerMetadata"]!["murfai"]!["wordDurations"]![0]!["word"]);
        Assert.Null(body["providerMetadata"]!["murfai"]!["encodedAudio"]);
        var upstream = Assert.Single(host.Requests);
        Assert.Equal("/v1/speech/generate", upstream.Path);
        Assert.Equal("Hello", (string?)upstream.Body!["text"]);
        Assert.Equal("en-US-natalie", (string?)upstream.Body["voiceId"]);
        Assert.Equal("en-US", (string?)upstream.Body["multiNativeLocale"]);
        Assert.Equal(10, (int)upstream.Body["rate"]!);
        Assert.True((bool)upstream.Body["wordDurationsAsOriginalText"]!);
        Assert.Equal("hello", (string?)upstream.Body["pronunciationDictionary"]!["Hello"]!["pronunciation"]);
    }

    [Fact]
    public async Task Murf_falcon_stream_preserves_part_options_and_raw_chunks()
    {
        await using var host = await FixtureHost.StartAsync(Reply, antimatterMurfAIPicsart: true);
        using var response = await host.PostAsync("/api/speech/stream", """
            {"model":"murfai/falcon-2.1","voice":"en-US-natalie","outputFormat":{"type":"mp3","rate":16000},
             "text":[{"type":"text","text":"Hel","providerOptions":{"murfai":{"locale":"en-US"}}},
                     {"type":"text","text":"lo","providerOptions":{"murfai":{"locale":"en-US"}}}],
             "includeRawChunks":true,"providerOptions":{"murfai":{"future_option":{"nested":true},"sampleRate":24000}}}
            """, Murf);
        var wire = await GatewayTests.Success(response);
        Assert.Contains("stream-start", wire);
        Assert.Contains("rawValue", wire);
        Assert.Contains("AQID", wire);
        Assert.Contains("16000", wire);
        Assert.Contains("finish", wire);
        var upstream = Assert.Single(host.Requests);
        Assert.Equal("global.api.murf.ai", upstream.Host);
        Assert.Equal("/v1/speech/stream", upstream.Path);
        Assert.Equal("Hello", (string?)upstream.Body!["text"]);
        Assert.Equal("en-US", (string?)upstream.Body["locale"]);
        Assert.Equal(16000, (int)upstream.Body["sampleRate"]!);
        Assert.True((bool)upstream.Body["future_option"]!["nested"]!);
    }

    [Theory]
    [InlineData("text2image")]
    [InlineData("text2sticker")]
    public async Task Picsart_image_workflow_preserves_native_json_without_leaking_credentials_to_assets(string operation)
    {
        await using var host = await FixtureHost.StartAsync(Reply, antimatterMurfAIPicsart: true);
        using var response = await host.PostAsync("/api/images", $$$$"""
            {"model":"picsart/{{{{operation}}}}/urn:air:sample@1","prompt":"Cloud","n":1,"size":"640x480",
             "providerOptions":{"picsart":{"color_tone":"warm","future_option":{"nested":[true]},"prompt":"wrong"}}}
            """, Picsart);
        var body = JsonNode.Parse(await GatewayTests.Success(response))!;
        Assert.Equal("data:image/png;base64,AQID", (string?)body["images"]![0]);
        Assert.Equal("png", (string?)body["providerMetadata"]!["picsart"]!["data"]![0]!["native_format"]);
        Assert.Equal(17, (int)body["providerMetadata"]!["picsart"]!["credits"]!);
        var requests = host.Requests.ToArray();
        Assert.Equal(3, requests.Length);
        Assert.Equal($"/v1/{operation}", requests[0].Path);
        Assert.Equal("picsart-fixture", requests[0].Headers["X-Picsart-API-Key"]);
        Assert.Equal("Cloud", (string?)requests[0].Body!["prompt"]);
        Assert.Equal("urn:air:sample@1", (string?)requests[0].Body!["model"]);
        Assert.Equal(640, (int)requests[0].Body!["width"]!);
        Assert.True((bool)requests[0].Body!["future_option"]!["nested"]![0]!);
        Assert.Equal($"/v1/{operation}/inferences/job-1", requests[1].Path);
        Assert.Equal("cdn.picsart.com", requests[2].Host);
        Assert.False(requests[2].Headers.ContainsKey("X-Picsart-API-Key"));
    }

    [Fact]
    public async Task Picsart_logo_takes_native_metadata_and_inline_reference_without_generic_prompt()
    {
        await using var host = await FixtureHost.StartAsync(Reply, antimatterMurfAIPicsart: true);
        using var response = await host.PostAsync("/api/images", """
            {"model":"picsart/logo/urn:air:sample@1","files":[{"data":"AQID","mediaType":"image/png"}],
             "providerOptions":{"picsart":{"brand_name":"Star Bakery","business_description":"Fresh bread","color_tone":"warm"}}}
            """, Picsart);
        await GatewayTests.Success(response);
        var submit = host.Requests.First();
        Assert.Equal("/v1/logo", submit.Path);
        Assert.Contains("brand_name", submit.RawBody);
        Assert.Contains("Star Bakery", submit.RawBody);
        Assert.Contains("business_description", submit.RawBody);
        Assert.Contains("Fresh bread", submit.RawBody);
        Assert.Contains("reference_image", submit.RawBody);
    }

    [Fact]
    public async Task Picsart_failed_inference_does_not_download_assets()
    {
        await using var host = await FixtureHost.StartAsync(request => request.Path.Contains("/inferences/", StringComparison.Ordinal)
            ? Json("""{"status":"failed","data":[{"url":"https://cdn.picsart.com/failed.png"}]}""") : Reply(request), antimatterMurfAIPicsart: true);
        using var response = await host.PostAsync("/api/images", """{"model":"picsart/text2image/urn:air:sample@1","prompt":"Cloud"}""", Picsart);
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(2, host.Requests.Count);
    }

    private static HttpResponseMessage Reply(FixtureHost.UpstreamRequest request) => (request.Host, request.Path) switch
    {
        ("useantimatter.com", "/v1/models") => Json("""{"data":[{"id":"lab/model","name":"Lab Model","created":123,"context_length":32768,"max_output":4096,"pricing":{"input":"2.5","output":8}}]}"""),
        ("useantimatter.com", "/v1/chat/completions") when request.Stream => new(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                data: {"id":"chat-1","object":"chat.completion.chunk","model":"lab/model","created":123,"choices":[{"index":0,"delta":{"role":"assistant","content":"hello"},"finish_reason":"stop"}],"usage":{"prompt_tokens":2,"completion_tokens":1,"total_tokens":3},"future_response":{"nested":true}}

                data: [DONE]

                """ + "\n", Encoding.UTF8, "text/event-stream")
        },
        ("useantimatter.com", "/v1/chat/completions") => Json("""{"id":"chat-1","object":"chat.completion","model":"lab/model","created":123,"choices":[{"index":0,"message":{"role":"assistant","content":"hello"},"finish_reason":"stop"}],"usage":{"prompt_tokens":2,"completion_tokens":1,"total_tokens":3},"future_response":{"nested":true}}"""),
        ("api.murf.ai", "/v1/speech/voices?model=gen2" or "/v1/speech/voices?model=falcon-2.1") => Json("""[{"voiceId":"en-US-natalie","displayName":"Natalie","locale":"en-US","supportedLocales":{"es-ES":{}}}]"""),
        ("api.murf.ai", "/v1/text/translate") => Json("""{"translations":[{"source_text":"Hello","translated_text":"Hola"}],"translation_metrics":{"characters":5}}"""),
        ("api.murf.ai", "/v1/speech/generate") => Json("""{"encodedAudio":"AQID","wordDurations":[{"word":"Hello","startMs":10}]}"""),
        ("global.api.murf.ai", "/v1/speech/stream") => Bytes("application/octet-stream"),
        ("genai-api.picsart.io", "/v1/text2image" or "/v1/text2sticker" or "/v1/logo") => Json("""{"inference_id":"job-1"}"""),
        ("genai-api.picsart.io", "/v1/text2image/inferences/job-1" or "/v1/text2sticker/inferences/job-1" or "/v1/logo/inferences/job-1")
            => Json("""{"status":"completed","data":[{"url":"https://cdn.picsart.com/result.png","native_format":"png"}],"credits":17}"""),
        ("cdn.picsart.com", "/result.png") => Bytes("image/png"),
        _ => new(HttpStatusCode.NotFound) { Content = new StringContent($"Unexpected fixture request: {request.Host}{request.Path}") }
    };

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Bytes(string type)
        => new(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) { Headers = { ContentType = new MediaTypeHeaderValue(type) } } };
}
