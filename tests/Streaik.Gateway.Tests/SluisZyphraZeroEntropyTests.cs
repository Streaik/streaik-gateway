using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace Streaik.Gateway.Tests;

public sealed class SluisZyphraZeroEntropyTests
{
    private static readonly (string, string) SluisKey = ("x-sluis-key", "sluis-key");
    private static readonly (string, string) ZyphraKey = ("x-zyphra-key", "zyphra-key");
    private static readonly (string, string) ZeroEntropyKey = ("x-zeroentropy-key", "zeroentropy-key");

    [Fact]
    public async Task Models_include_static_shortcuts_and_only_the_active_credentials_voices()
    {
        await using var host = await FixtureHost.StartAsync(request => (request.Host, request.Path) switch
        {
            ("api.sluis.ai", "/v1/models") when request.Headers["Authorization"] == "Bearer sluis-key" => FixtureHost.Json("""
                {"data":[{"id":"sluis/auto"},{"id":"vendor/model","display_name":"Live model","context_window":32000}]}
                """),
            ("api.zyphracloud.com", "/api/v1/audio/default-voices") => FixtureHost.Json("""[{"voice_id":"alloy","display_name":"Alloy"}]"""),
            ("api.zyphracloud.com", "/api/v1/audio/cloned-voices") when request.Headers["Authorization"] == "Bearer zyphra-key" =>
                FixtureHost.Json("""[{"voice_id":"ALLOY"},{"voice_id":"private","name":"My Voice"}]"""),
            ("api.zyphracloud.com", "/api/v1/audio/cloned-voices") when request.Headers["Authorization"] == "Bearer second-key" =>
                FixtureHost.Json("""[{"voice_id":"second"}]"""),
            _ => throw new InvalidOperationException($"Unexpected request: {request.Host}{request.Path}")
        }, sluisZyphraZeroEntropy: true);

        using var anonymous = await host.GetAsync("/v1/models");
        var defaults = await GatewayTests.Success(anonymous);
        Assert.Contains("sluis/sluis/auto", defaults);
        Assert.Contains("zyphra/zyphra/ZONOS2", defaults);
        Assert.Contains("zeroentropy/zembed-1", defaults);
        Assert.Contains("zeroentropy/zerank-2", defaults);
        Assert.Empty(host.Requests);

        using var keyed = await host.GetAsync("/v1/models", SluisKey, ZyphraKey);
        var models = JsonNode.Parse(await GatewayTests.Success(keyed))!["data"]!.AsArray();
        Assert.Single(models, item => (string?)item?["id"] == "sluis/sluis/auto");
        Assert.Equal(32000, (int?)Assert.Single(models, item => (string?)item?["id"] == "sluis/vendor/model")!["context_window"]);
        Assert.Single(models, item => ((string?)item?["id"])?.Equals("zyphra/zyphra/ZONOS2/alloy", StringComparison.OrdinalIgnoreCase) == true);
        Assert.Equal("speech", (string?)Assert.Single(models, item => (string?)item?["id"] == "zyphra/zyphra/ZONOS2/private")!["type"]);

        using var second = await host.GetAsync("/v1/models", ("x-zyphra-key", "second-key"));
        var secondModels = await GatewayTests.Success(second);
        Assert.Contains("ZONOS2/second", secondModels);
        Assert.DoesNotContain("ZONOS2/private", secondModels);
        Assert.Equal(5, host.Requests.Count);
    }

    [Theory]
    [InlineData("/v1/chat/completions", false)]
    [InlineData("/v1/chat/completions", true)]
    [InlineData("/v1/responses", false)]
    [InlineData("/v1/responses", true)]
    [InlineData("/v1/messages", false)]
    [InlineData("/v1/messages", true)]
    [InlineData("/api/chat", true)]
    public async Task Sluis_routing_prefers_native_responses_and_preserves_extensions(string route, bool stream)
    {
        var isMessages = route == "/v1/messages";
        var isVercel = route == "/api/chat";
        var path = route == "/v1/chat/completions" ? route : "/v1/responses";
        await using var host = await FixtureHost.StartAsync(request =>
        {
            Assert.Equal("api.sluis.ai", request.Host);
            Assert.Equal(path, request.Path);
            Assert.Equal("Bearer sluis-key", request.Headers["Authorization"]);
            Assert.Equal(stream, request.Stream);
            Assert.Equal("sluis/auto", (string?)request.Body!["model"]);
            if (!isMessages) Assert.Equal("eu", (string?)request.Body["vendor_route"]?["region"]);
            if (isVercel)
            {
                Assert.False((bool?)request.Body["store"]);
                Assert.Null(request.Body["STORE"]);
                Assert.Null(request.Body["MODEL"]);
            }
            return FixtureHost.Reply(request);
        }, sluisZyphraZeroEntropy: true);
        var body = route switch
        {
            "/v1/responses" => """{"input":"hello","vendor_route":{"region":"eu"}}""",
            "/v1/messages" => """{"messages":[{"role":"user","content":"hello"}],"max_tokens":32}""",
            "/api/chat" => """
                {"id":"chat","messages":[{"id":"user","role":"user","parts":[{"type":"text","text":"hello"}]}],
                 "providerMetadata":{"sluis":{"vendor_route":{"region":"eu"},"MODEL":"other","STORE":true}}}
                """,
            _ => """{"messages":[{"role":"user","content":"hello"}],"vendor_route":{"region":"eu"}}"""
        };
        var input = JsonNode.Parse(body)!;
        input["model"] = "sluis/sluis/auto";
        if (!isVercel) input["stream"] = stream;
        using var response = await host.PostAsync(route, input.ToJsonString(), SluisKey);
        var output = await GatewayTests.Success(response);
        Assert.Contains("hello", output);
        if (!isMessages) Assert.Contains("future_response", output);
        Assert.Single(host.Requests);
    }

    [Fact]
    public async Task Zyphra_chat_keeps_scoped_extensions_and_the_provider_response_metadata()
    {
        await using var host = await FixtureHost.StartAsync(_ => FixtureHost.Sse("""
                {"id":"z1","model":"zyphra/ZAYA1-8B","choices":[{"index":0,"delta":{"content":"hello"},"finish_reason":"stop"}],"custom_id":"abc"}
                """, """
                {"id":"z1","choices":[],"providerMetadata":{"zyphra":{"region":"us"}}}
                """, "[DONE]"), sluisZyphraZeroEntropy: true);
        using var response = await host.PostAsync("/api/chat", """
            {"model":"zyphra/zyphra/ZAYA1-8B","id":"chat","messages":[{"id":"u","role":"user","parts":[{"type":"text","text":"hello"}]}],
             "providerMetadata":{"zyphra":{"sampling":{"top_k":8},"STREAM":false,"tools":[{"name":"not-a-chat-tool","input_schema":{"type":"object"}}]},
                 "sluis":{"shouldNotForward":true}}}
            """, ZyphraKey);
        var output = await GatewayTests.Success(response);
        var request = Assert.Single(host.Requests);
        Assert.Equal("api.zyphracloud.com", request.Host);
        Assert.Equal("/api/v1/chat/completions", request.Path);
        Assert.Equal("Bearer zyphra-key", request.Headers["Authorization"]);
        Assert.Equal("zyphra/ZAYA1-8B", (string?)request.Body!["model"]);
        Assert.True((bool?)request.Body["stream"]);
        Assert.Null(request.Body["STREAM"]);
        Assert.True(request.Body["tools"] is null or JsonArray { Count: 0 });
        Assert.Equal(8, (int?)request.Body["sampling"]?["top_k"]);
        Assert.Null(request.Body["shouldNotForward"]);
        Assert.Contains("text-delta", output);
        Assert.Contains("\"zyphra\":{\"region\":\"us\",\"raw\":{\"custom_id\":\"abc\"}}", output);
    }

    [Theory]
    [InlineData("/v1/audio/speech", false)]
    [InlineData("/v1/audio/speech", true)]
    [InlineData("/api/speech", false)]
    [InlineData("/api/speech/stream", true)]
    public async Task Zyphra_speech_uses_native_audio_and_existing_stream_framing(string path, bool stream)
    {
        await using var host = await FixtureHost.StartAsync(request =>
        {
            Assert.Equal("api.zyphracloud.com", request.Host);
            Assert.Equal("/api/v1/audio/speech", request.Path);
            Assert.Equal("Bearer zyphra-key", request.Headers["Authorization"]);
            Assert.Equal("zyphra/ZONOS2", (string?)request.Body!["model"]);
            Assert.Equal("hello", (string?)request.Body["input"]);
            Assert.Equal("preset", (string?)request.Body["voice"]);
            Assert.Equal("wav", (string?)request.Body["response_format"]);
            Assert.Equal(stream, (bool?)request.Body["stream"]);
            Assert.Null(request.Body["stream_format"]);
            Assert.Equal(4, (int?)request.Body["vendor_extension"]?["seed"]);
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) { Headers = { ContentType = new MediaTypeHeaderValue("audio/wav") } } };
        }, sluisZyphraZeroEntropy: true);
        var json = path switch
        {
            "/api/speech" => """
                {"model":"zyphra/zyphra/ZONOS2","text":"hello","outputFormat":"wav","providerOptions":{"zyphra":{"voice":"preset","vendor_extension":{"seed":4}}}}
                """,
            "/api/speech/stream" => """
                {"model":"zyphra/zyphra/ZONOS2/preset","text":[{"type":"text","text":"hel"},{"type":"text","text":"lo"}],
                 "outputFormat":{"type":"wav"},"providerOptions":{"zyphra":{"stream":false,"vendor_extension":{"seed":4}}}}
                """,
            _ => $$$"""
                {"model":"zyphra/zyphra/ZONOS2/preset","input":"hello","voice":"ignored","response_format":"wav",
                 "stream_format":"{{{(stream ? "sse" : "audio")}}}","vendor_extension":{"seed":4}}
                """
        };
        using var response = await host.PostAsync(path, json, ZyphraKey);
        var output = await GatewayTests.Success(response);
        if (stream)
        {
            Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
            Assert.Contains("AQID", output);
            Assert.Contains(path.StartsWith("/v1/") ? "speech.audio.done" : "finish", output);
        }
        else if (path.StartsWith("/v1/"))
        {
            Assert.Equal("audio/wav", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal(new byte[] { 1, 2, 3 }, await response.Content.ReadAsByteArrayAsync());
        }
        else Assert.Equal("AQID", (string?)JsonNode.Parse(output)?["audio"]?["base64"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ZeroEntropy_embeddings_keep_native_options_and_the_existing_response_shapes(bool vercel)
    {
        await using var host = await FixtureHost.StartAsync(request =>
        {
            Assert.Equal("api.zeroentropy.dev", request.Host);
            Assert.Equal("/v1/models/embed", request.Path);
            Assert.Equal("Bearer zeroentropy-key", request.Headers["Authorization"]);
            Assert.Equal("zembed-1", (string?)request.Body!["model"]);
            Assert.Equal("hello", (string?)request.Body["input"]?[0]);
            Assert.Equal("query", (string?)request.Body["input_type"]);
            Assert.Equal(2, (int?)request.Body["dimensions"]);
            Assert.Equal(vercel ? "float" : "base64", (string?)request.Body["encoding_format"]);
            Assert.Equal(8, (int?)request.Body["future_option"]?["seed"]);
            var response = FixtureHost.Json($$"""
                {"results":[{"embedding":{{(vercel ? "[0.1,0.2]" : "\"AAE=\"")}}}],"usage":{"total_tokens":3},"request_id":"native-1"}
                """);
            response.Headers.Add("x-request-id", "native-1");
            response.Headers.TryAddWithoutValidation("Set-Cookie", "private=fixture");
            return response;
        }, sluisZyphraZeroEntropy: true);
        using var response = await host.PostAsync(vercel ? "/api/embeddings" : "/v1/embeddings", vercel ? """
            {"model":"zeroentropy/zembed-1","values":["hello"],"dimensions":2,
             "providerOptions":{"zeroentropy":{"model":"override","input_type":"query","future_option":{"seed":8}}}}
            """ : """
            {"model":"zeroentropy/zembed-1","input":["hello"],"dimensions":2,"encoding_format":"base64","input_type":"query","future_option":{"seed":8}}
            """, ZeroEntropyKey);
        var result = JsonNode.Parse(await GatewayTests.Success(response))!;
        if (vercel)
        {
            Assert.Equal(0.1, (double?)result["embeddings"]?[0]?[0]);
            Assert.Equal(3, (int?)result["usage"]?["tokens"]);
            Assert.Equal("native-1", (string?)result["response"]?["body"]?["request_id"]);
            Assert.Contains(result["response"]!["headers"]!.AsObject(), pair =>
                pair.Key.Equals("x-request-id", StringComparison.OrdinalIgnoreCase) && (string?)pair.Value == "native-1");
            Assert.DoesNotContain("private=fixture", result.ToJsonString());
        }
        else
        {
            Assert.Equal("AAE=", (string?)result["data"]?[0]?["embedding"]);
            Assert.Equal(3, (int?)result["usage"]?["total_tokens"]);
            Assert.Equal("zeroentropy/zembed-1", (string?)result["model"]);
            Assert.Null(result["response"]);
        }
    }

    [Fact]
    public async Task ZeroEntropy_rerank_serializes_structured_documents_and_keeps_raw_result()
    {
        await using var host = await FixtureHost.StartAsync(request =>
        {
            Assert.Equal("api.zeroentropy.dev", request.Host);
            Assert.Equal("/v1/models/rerank", request.Path);
            Assert.Equal("Bearer zeroentropy-key", request.Headers["Authorization"]);
            Assert.Equal("zerank-2", (string?)request.Body!["model"]);
            Assert.Equal("needle", (string?)request.Body["query"]);
            Assert.Null(request.Body["QUERY"]);
            Assert.Equal(1, (int?)request.Body["top_n"]);
            Assert.True((bool?)request.Body["vendor_option"]?["enabled"]);
            Assert.Equal("hay", (string?)request.Body["documents"]?[0]);
            Assert.Equal("needle", (string?)JsonNode.Parse((string)request.Body["documents"]![1]!)?["title"]);
            return FixtureHost.Json("""{"id":"rerank-1","results":[{"index":1,"relevance_score":0.91,"vendor_field":true}]}""");
        }, sluisZyphraZeroEntropy: true);
        using var response = await host.PostAsync("/api/rerank", """
            {"model":"zeroentropy/zerank-2","query":"needle","documents":{"type":"object","values":["hay",{"title":"needle"}]},"topN":1,
             "providerOptions":{"zeroentropy":{"top_n":4,"QUERY":"override","vendor_option":{"enabled":true}}}}
            """, ZeroEntropyKey);
        var result = JsonNode.Parse(await GatewayTests.Success(response))!;
        Assert.Equal(1, (int?)result["ranking"]?[0]?["index"]);
        Assert.Equal(0.91, (double?)result["ranking"]?[0]?["relevanceScore"]);
        Assert.True((bool?)result["response"]?["body"]?["results"]?[0]?["vendor_field"]);
        Assert.Equal("zeroentropy/zerank-2", (string?)result["response"]?["modelId"]);
    }
}
