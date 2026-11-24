using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace Streaik.Gateway.Tests;

public sealed class NscaleDigitalOceanSpeechifyTests
{
    private static readonly (string, string) Nscale = ("x-nscale-key", "nscale-fixture");
    private static readonly (string, string) DigitalOcean = ("x-digitalocean-key", "digitalocean-fixture");
    private static readonly (string, string) Speechify = ("x-speechify-key", "speechify-fixture");

    [Fact]
    public async Task Models_preserve_nested_ids_categories_pricing_and_paginated_voices()
    {
        await using var host = await StartAsync(request => (request.Host, request.Path) switch
        {
            ("inference.api.nscale.com", "/v1/models") => FixtureHost.Json("""
                [{"id":"vendor/chat","context_length":4096,"pricing":{"input":"0.001","output":0.002}},
                 {"id":"stabilityai/sdxl"},{"id":"vendor/embedding"}]
                """),
            ("inference.do-ai.run", "/v1/models") => FixtureHost.Json("""{"data":[{"id":"vendor/chat"},{"id":"flux-schnell"},{"id":"vendor/embedding"}]}"""),
            ("api.sws.speechify.com", "/v1/audio/models") => FixtureHost.Json("""{"models":[{"id":"simba-english","name":"Simba","default":true}]}"""),
            ("api.sws.speechify.com", "/v1/voices?limit=200") => FixtureHost.Json("""
                {"voices":[{"id":"ava","display_name":"Ava","models":[{"name":"simba-english","languages":[{"locale":"en-US"}]}]}],
                 "has_more":true,"next_cursor":"next page"}
                """),
            ("api.sws.speechify.com", "/v1/voices?limit=200&cursor=next%20page") => FixtureHost.Json("""
                {"data":[{"voice_id":"leo","models":[{"name":"simba-english"}]}],"has_more":true,"next_cursor":"next page"}
                """),
            _ => new(HttpStatusCode.NotFound)
        });
        using var response = await host.GetAsync("/v1/models", Nscale, DigitalOcean, Speechify);
        var models = (await Json(response))["data"]!.AsArray().ToDictionary(item => (string)item!["id"]!);
        Assert.Equal("image", (string?)models["nscale/stabilityai/sdxl"]!["type"]);
        Assert.Equal("embedding", (string?)models["nscale/vendor/embedding"]!["type"]);
        Assert.Equal(4096, (int?)models["nscale/vendor/chat"]!["context_window"]);
        Assert.Equal(0.001m, (decimal?)models["nscale/vendor/chat"]!["pricing"]!["input"]);
        Assert.Equal("image", (string?)models["digitalocean/flux-schnell"]!["type"]);
        Assert.False(models.ContainsKey("digitalocean/vendor/embedding"));
        Assert.Equal("speech", (string?)models["speechify/simba-english/ava"]!["type"]);
        Assert.True(models.ContainsKey("speechify/simba-english/leo"));
        Assert.Equal(5, host.Requests.Count);
        Assert.Equal("Bearer speechify-fixture", Assert.Single(host.Requests, x => x.Path == "/v1/audio/models").Headers["Authorization"]);
    }

    [Theory]
    [InlineData("nscale", "/v1/chat/completions", false)]
    [InlineData("nscale", "/v1/responses", true)]
    [InlineData("nscale", "/api/chat", true)]
    [InlineData("digitalocean", "/v1/chat/completions", true)]
    [InlineData("digitalocean", "/v1/messages", false)]
    public async Task Chat_uses_compatible_endpoint_and_preserves_extensions(string provider, string path, bool stream)
    {
        await using var host = await StartAsync(request => request.Stream
            ? FixtureHost.Sse("""
                {"id":"chat-1","object":"chat.completion.chunk","model":"vendor/chat","created":1,"choices":[{"index":0,"delta":{"role":"assistant","content":"hello"},"finish_reason":"stop"}],"native_reply":{"detail":[1,2]}}
                """, "[DONE]")
            : FixtureHost.Json("""
                {"id":"chat-1","object":"chat.completion","model":"vendor/chat","created":1,"choices":[{"index":0,"message":{"role":"assistant","content":"hello"},"finish_reason":"stop"}],"native_reply":{"detail":[1,2]}}
                """));
        var payload = JsonNode.Parse(path switch
        {
            "/v1/responses" => """{"input":"Hi"}""",
            "/v1/messages" => """{"messages":[{"role":"user","content":"Hi"}],"max_tokens":8}""",
            "/api/chat" => """{"messages":[{"id":"u","role":"user","parts":[{"type":"text","text":"Hi"}]}]}""",
            _ => """{"messages":[{"role":"user","content":"Hi"}],"tools":[]}"""
        })!.AsObject();
        payload["model"] = $"{provider}/vendor/chat";
        if (path != "/api/chat") payload["stream"] = stream;
        // The existing cross-protocol OpenAI and Messages adapters reject root options they cannot translate.
        if (path == "/v1/chat/completions") payload["native_request"] = JsonNode.Parse("""{"flag":true}""");
        if (path == "/api/chat") payload["providerMetadata"] = new JsonObject
            { [provider] = JsonNode.Parse("""{"native_request":{"flag":true}}""") };
        using var response = await host.PostAsync(path, payload.ToJsonString(), provider == "nscale" ? Nscale : DigitalOcean);
        var wire = await GatewayTests.Success(response);
        Assert.Contains("hello", wire);
        Assert.Contains("native_reply", wire);
        var sent = Assert.Single(host.Requests);
        Assert.Equal(provider == "nscale" ? "inference.api.nscale.com" : "inference.do-ai.run", sent.Host);
        Assert.Equal("/v1/chat/completions", sent.Path);
        Assert.Equal($"Bearer {provider}-fixture", sent.Headers["Authorization"]);
        Assert.Equal("vendor/chat", (string?)sent.Body!["model"]);
        if (path is "/v1/chat/completions" or "/api/chat") Assert.True((bool?)sent.Body["native_request"]!["flag"]);
        if (provider == "digitalocean") Assert.Null(sent.Body["tools"]);
    }

    [Fact]
    public async Task Nscale_preserves_embedding_request_extensions_and_sdk_raw_metadata()
    {
        await using var host = await StartAsync(request =>
        {
            Assert.Equal("/v1/embeddings", request.Path);
            Assert.Equal("vendor/embedding", (string?)request.Body!["model"]);
            Assert.Equal("query", (string?)request.Body["native_config"]!["mode"]);
            return WithHeaders(FixtureHost.Json("""
                {"model":"vendor/embedding","data":[{"index":0,"embedding":[0.5,1]}],"usage":{"prompt_tokens":3,"total_tokens":3},"native_extra":{"revision":7}}
                """));
        });
        using var native = await host.PostAsync("/v1/embeddings", """
            {"model":"nscale/vendor/embedding","input":"hello","native_config":{"mode":"query"}}
            """, Nscale);
        Assert.Equal("nscale/vendor/embedding", (string?)(await Json(native))["model"]);
        using var sdk = await host.PostAsync("/api/embeddings", """
            {"model":"nscale/vendor/embedding","values":["hello"],"providerOptions":{"nscale":{"native_config":{"mode":"query"}}}}
            """, Nscale);
        var result = await Json(sdk);
        Assert.Equal(0.5, (double?)result["embeddings"]![0]![0]);
        Assert.Equal(3, (int?)result["usage"]!["tokens"]);
        Assert.Contains("native_extra", result.ToJsonString());
        Assert.Contains("request-42", result.ToJsonString());
        Assert.DoesNotContain("secret-cookie", result.ToJsonString());
    }

    [Fact]
    public async Task Nscale_image_metadata_and_image_chat_keep_the_native_generation_endpoint()
    {
        await using var host = await StartAsync(request =>
        {
            Assert.Equal("/v1/images/generations", request.Path);
            Assert.Equal("black-forest-labs/FLUX.1-schnell", (string?)request.Body!["model"]);
            Assert.Null(request.Body["stream"]);
            Assert.Null(request.Body["partial_images"]);
            return FixtureHost.Json("""{"created":17,"data":[{"b64_json":"AQID","native_item":{"label":"keep"}}],"native_extra":{"seed":7}}""");
        });
        const string model = "nscale/black-forest-labs/FLUX.1-schnell";
        using var sdk = await host.PostAsync("/api/images", $$$$$"""
            {"model":"{{{{{model}}}}}","prompt":"draw","providerOptions":{"nscale":{"native_config":{"steps":[1,2]}}}}
            """, Nscale);
        var result = await Json(sdk);
        Assert.Equal("data:image/png;base64,AQID", (string?)result["images"]![0]);
        var metadata = result["providerMetadata"]!["nscale"]!;
        Assert.Equal(7, (int?)metadata["native_extra"]!["seed"]);
        Assert.Equal("keep", (string?)metadata["data"]![0]!["native_item"]!["label"]);
        Assert.Null(metadata["data"]![0]!["b64_json"]);
        Assert.Equal(2, (int?)Assert.Single(host.Requests).Body!["native_config"]!["steps"]![1]);

        using var stream = await host.PostAsync("/v1/images/generations", $$"""
            {"model":"{{model}}","prompt":"draw","stream":true,"partial_images":1}
            """, Nscale);
        var wire = await GatewayTests.Success(stream);
        Assert.Contains("image_generation.completed", wire);
        Assert.DoesNotContain("image_generation.partial_image", wire);

        using var chat = await host.PostAsync("/v1/chat/completions", $$"""
            {"model":"{{model}}","messages":[{"role":"user","content":"draw"}]}
            """, Nscale);
        Assert.Contains("AQID", await GatewayTests.Success(chat));
        Assert.Equal(3, host.Requests.Count);
    }

    [Fact]
    public async Task DigitalOcean_preserves_image_links_without_fetching_them()
    {
        await using var host = await StartAsync(request =>
        {
            Assert.Equal("inference.do-ai.run", request.Host);
            Assert.Equal("/v1/images/generations", request.Path);
            Assert.Equal(3, (int?)request.Body!["native_config"]!["steps"]);
            return FixtureHost.Json("""
                {"created":17,"data":[{"url":"https://images.example/output.png"}],"usage":{"input_tokens":2,"output_tokens":1,"total_tokens":3},"native_extra":[1,2]}
                """);
        });
        using var response = await host.PostAsync("/api/images", """
            {"model":"digitalocean/flux-schnell","prompt":"draw","providerOptions":{"digitalocean":{"native_config":{"steps":3}}}}
            """, DigitalOcean);
        var result = await Json(response);
        Assert.Equal("https://images.example/output.png", (string?)result["images"]![0]);
        Assert.Equal(3, (int?)result["usage"]!["totalTokens"]);
        Assert.Equal(2, (int?)result["providerMetadata"]!["digitalocean"]!["native_extra"]![1]);
        Assert.Single(host.Requests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task System_One_translates_predicate_choice_and_score_and_keeps_raw_metadata(bool native)
    {
        await using var host = await StartAsync(request =>
        {
            Assert.Equal("/v1/systemone", request.Path);
            Assert.Equal("Bearer digitalocean-fixture", request.Headers["Authorization"]);
            Assert.Equal("systemone", (string?)request.Body!["model"]);
            Assert.Equal("Evidence", (string?)request.Body["state"]![0]);
            Assert.Equal("noul", (string?)request.Body["questions"]![native ? "question-0" : "flag"]!["type"]);
            var names = native ? new[] { "question-0", "question-1", "question-2", "option-0", "option-1" } : ["flag", "pick", "rating", "a", "b"];
            return WithHeaders(FixtureHost.Json($$$"""
                {"model":"systemone","answers":{
                  "{{{names[0]}}}":{"type":"noul","noul":0.75},
                  "{{{names[1]}}}":{"type":"choice","choice":"{{{names[4]}}}","confidence":0.8,"probabilities":{"{{{names[3]}}}":0.2,"{{{names[4]}}}":0.8}},
                  "{{{names[2]}}}":{"type":"score","score":1.5,"confidence":0.7,"legend":{"0":"low","1":"mid","2":"high"},"probabilities":{"0":0.1,"1":0.3,"2":0.6}}
                },"usage":{"input_tokens":4,"output_tokens":2},"native_extra":{"trace":[3]}}
                """));
        });
        using var response = await host.PostAsync(native ? "/v1/decisions" : "/api/decisions", native ? """
            {"model":"digitalocean/systemone","input":[{"role":"user","content":[{"type":"input_text","text":"Evidence"}]}],"questions":[
              {"type":"predicate","name":"flag","instructions":"Flag?"},
              {"type":"choice","name":"pick","instructions":"Pick","choices":[{"value":"a"},{"value":true}]},
              {"type":"score","name":"rating","instructions":"Rate","levels":[{"label":"low"},{"label":"mid"},{"label":"high"}]}]}
            """ : """
            {"model":"digitalocean/systemone","stateFormat":"sdk","state":[{"type":"text","text":"Evidence"}],"questions":{
              "flag":{"type":"boolean","instructions":"Flag?"},
              "pick":{"type":"choice","instructions":"Pick","criteria":{"a":"First","b":"Second"}},
              "rating":{"type":"score","instructions":"Rate","criteria":["low","mid","high"]}}}
            """, DigitalOcean);
        var result = await Json(response);
        Assert.Equal(3, (int?)result["providerMetadata"]!["digitalocean"]!["native_extra"]!["trace"]![0]);
        if (native)
        {
            Assert.Equal(0.75, (double?)result["answers"]![0]!["probability"]);
            Assert.True((bool?)result["answers"]![1]!["choice"]);
            Assert.Equal(1.5, (double?)result["answers"]![2]!["score"]);
        }
        else
        {
            Assert.Equal(0.75, (double?)result["answers"]!["flag"]!["probability"]);
            Assert.Equal("b", (string?)result["answers"]!["pick"]!["choice"]);
            Assert.Equal(1.5, (double?)result["answers"]!["rating"]!["score"]);
            Assert.Equal("request-42", (string?)result["response"]!["id"]);
        }
    }

    [Fact]
    public async Task Speechify_buffered_speech_preserves_metadata_and_model_voice_takes_precedence()
    {
        await using var host = await StartAsync(request =>
        {
            Assert.Equal("api.sws.speechify.com", request.Host);
            Assert.Equal("/v1/audio/speech", request.Path);
            Assert.Equal("Bearer speechify-fixture", request.Headers["Authorization"]);
            Assert.Equal("simba-english", (string?)request.Body!["model"]);
            Assert.Equal("ava", (string?)request.Body["voice_id"]);
            Assert.Equal("<speak>Hello</speak>", (string?)request.Body["input"]);
            Assert.Equal("wav", (string?)request.Body["audio_format"]);
            Assert.Equal("en-US", (string?)request.Body["language"]);
            Assert.True((bool?)request.Body["options"]!["native"]!["flag"]);
            return WithHeaders(FixtureHost.Json("""
                {"audio_data":"AQID","audio_format":"wav","speech_marks":{"word":["Hello"]},"future":{"samples":3}}
                """));
        });
        using var response = await host.PostAsync("/api/speech", """
            {"model":"speechify/simba-english/ava","text":"<speak>Hello</speak>","voice":"ignored","language":"en-US",
             "providerOptions":{"speechify":{"voice_id":"also-ignored","options":{"native":{"flag":true}}}}}
            """, Speechify);
        var result = await Json(response);
        Assert.Equal("AQID", (string?)result["audio"]!["base64"]);
        var metadata = result["providerMetadata"]!["speechify"]!;
        Assert.Equal("Hello", (string?)metadata["speech_marks"]!["word"]![0]);
        Assert.Equal(3, (int?)metadata["future"]!["samples"]);
        Assert.Null(metadata["audio_data"]);
        Assert.DoesNotContain("secret-cookie", result.ToJsonString());
    }

    [Fact]
    public async Task Speechify_stream_supports_sdk_config_raw_chunks_and_native_wav_fallback()
    {
        await using var host = await StartAsync(request => request.Path == "/v1/audio/speech"
            ? FixtureHost.Json("""{"audio_data":"AQID","audio_format":"wav"}""")
            : WithHeaders(new(HttpStatusCode.OK) { Content = Audio() }));
        using var stream = await host.PostAsync("/api/speech/stream", """
            {"model":"speechify/simba-english","voice":"ava","text":[{"text":"Hel","providerOptions":{"speechify":{"native":true}}},{"text":"lo"}],
             "providerOptions":{"speechify":{"native":true}},"outputFormat":{"type":"mp3","rate":24000},"includeRawChunks":true}
            """, Speechify);
        var wire = await GatewayTests.Success(stream);
        Assert.Contains("request-42", wire);
        Assert.DoesNotContain("secret-cookie", wire);
        Assert.Contains("\"type\":\"raw\"", wire);
        Assert.Contains("AQID", wire);
        Assert.Contains("\"type\":\"finish\"", wire);
        var sdk = Assert.Single(host.Requests);
        Assert.Equal("api.speechify.ai", sdk.Host);
        Assert.Equal("/v1/audio/stream", sdk.Path);
        Assert.Equal("Hello", (string?)sdk.Body!["input"]);
        Assert.Equal("mp3_24000_128", (string?)sdk.Body["output_format"]);
        Assert.True((bool?)sdk.Body["native"]);

        using var native = await host.PostAsync("/v1/audio/speech", """
            {"model":"speechify/simba-english/ava","input":"Hello","response_format":"wav","stream_format":"sse"}
            """, Speechify);
        var nativeWire = await GatewayTests.Success(native);
        Assert.Contains("AQID", nativeWire);
        Assert.Contains("speech.audio.done", nativeWire);
        Assert.Equal("/v1/audio/speech", host.Requests.Last().Path);
    }

    [Fact]
    public async Task Speechify_chat_stream_routes_to_pcm_synthesis_and_rejects_conflicting_sdk_voice_parts()
    {
        await using var host = await StartAsync(request =>
        {
            Assert.Equal("api.sws.speechify.com", request.Host);
            Assert.Equal("/v1/audio/stream", request.Path);
            Assert.Equal("pcm_24000", (string?)request.Body!["output_format"]);
            Assert.Equal("ava", (string?)request.Body["voice_id"]);
            var audio = Audio();
            audio.Headers.ContentType = new("audio/L16");
            return new(HttpStatusCode.OK) { Content = audio };
        });
        using var response = await host.PostAsync("/v1/chat/completions", """
            {"model":"speechify/simba-english/ava","messages":[{"role":"user","content":"Say hello"}],"stream":true,"audio":{"voice":"ignored","format":"pcm16"}}
            """, Speechify);
        Assert.Contains("AQID", await GatewayTests.Success(response));
        using var conflict = await host.PostAsync("/api/speech/stream", """
            {"model":"speechify/simba-english","text":[{"text":"Hi","voice":"ava"},{"text":" there","voice":"leo"}]}
            """, Speechify);
        Assert.Equal(HttpStatusCode.BadRequest, conflict.StatusCode);
        Assert.Single(host.Requests);
    }

    private static Task<FixtureHost> StartAsync(Func<FixtureHost.UpstreamRequest, HttpResponseMessage> reply)
        => FixtureHost.StartAsync(reply, nscaleDigitalOceanSpeechify: true);

    private static async Task<JsonNode> Json(HttpResponseMessage response) => JsonNode.Parse(await GatewayTests.Success(response))!;

    private static ByteArrayContent Audio()
    {
        var content = new ByteArrayContent([1, 2, 3]);
        content.Headers.ContentType = new MediaTypeHeaderValue("audio/mpeg");
        return content;
    }

    private static HttpResponseMessage WithHeaders(HttpResponseMessage response)
    {
        response.Headers.TryAddWithoutValidation("x-request-id", "request-42");
        response.Headers.TryAddWithoutValidation("Set-Cookie", "secret-cookie=1");
        return response;
    }
}
