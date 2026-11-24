using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Streaik.Gateway.Tests;

public sealed class DoublewordBananaPeelCreoraTests
{
    private static readonly (string, string) Doubleword = ("X-Doubleword-Key", "double-key");
    private static readonly (string, string) BananaPeel = ("X-BananaPeel-Key", "banana-key");
    private static readonly (string, string) Creora = ("X-Creora-Key", "creora-key");

    [Fact]
    public async Task Catalogs_use_scoped_credentials_and_prefix_model_ids_once()
    {
        await using var host = await FixtureHost.StartAsync(request => request.Host switch
        {
            "api.doubleword.ai" => FixtureHost.Reply(request),
            "creora.ai" => FixtureHost.Json("""{"data":[{"id":"creora/creora-image"},{"id":"creora-chat","type":"chat"},{"id":"creora-video"}]}"""),
            _ => throw new InvalidOperationException(request.Host)
        }, doublewordBananaPeelCreora: true);

        using var anonymous = await host.GetAsync("/v1/models");
        Assert.Empty((await Json(anonymous))["data"]!.AsArray());
        Assert.Empty(host.Requests);
        using var response = await host.GetAsync("/v1/models", Doubleword, BananaPeel, Creora);
        var models = (await Json(response))["data"]!.AsArray().ToDictionary(model => (string)model!["id"]!);
        Assert.Contains("doubleword/vendor/Model", models);
        Assert.Equal("Banana Peel", (string?)models["bananapeel/banana-peel"]!["name"]);
        Assert.Equal("image", (string?)models["creora/creora-image"]!["type"]);
        Assert.Equal("language", (string?)models["creora/creora-chat"]!["type"]);
        Assert.Equal("video", (string?)models["creora/creora-video"]!["type"]);
        Assert.Collection(host.Requests,
            request => Check(request, "api.doubleword.ai", "/v1/models", Doubleword),
            request => Check(request, "creora.ai", "/api/v1/models", Creora));
    }

    [Theory]
    [InlineData("doubleword", "/v1/chat/completions", false, "choices")]
    [InlineData("doubleword", "/v1/chat/completions", true, "[DONE]")]
    [InlineData("doubleword", "/v1/responses", false, "future_response")]
    [InlineData("doubleword", "/v1/responses", true, "response.completed")]
    [InlineData("doubleword", "/v1/messages", false, "future_content")]
    [InlineData("doubleword", "/v1/messages", true, "message_stop")]
    [InlineData("bananapeel", "/v1/responses", false, "future_response")]
    [InlineData("bananapeel", "/v1/responses", true, "response.completed")]
    [InlineData("bananapeel", "/v1/messages", false, "future_content")]
    [InlineData("bananapeel", "/v1/messages", true, "message_stop")]
    [InlineData("bananapeel", "/v1/chat/completions", false, "extensions")]
    [InlineData("bananapeel", "/v1/chat/completions", true, "future_response")]
    public async Task Text_routes_keep_native_options_and_stream_mode(string provider, string path, bool stream, string outputMarker)
    {
        var banana = provider == "bananapeel";
        var upstreamPath = banana && path == "/v1/chat/completions" ? "/v1/responses" : path;
        await using var host = await FixtureHost.StartAsync(request => FixtureHost.Reply(request with { Path = upstreamPath }), doublewordBananaPeelCreora: true);
        var nativeModel = banana ? "banana-peel" : "vendor/Model";
        var input = path == "/v1/responses" ? "\"input\":\"hello\"" : "\"max_tokens\":24,\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}]";
        var body = $$"""
            {"model":"{{provider}}/{{nativeModel}}","stream":{{(stream ? "true" : "false")}},{{input}},
             "{{provider}}":{"MODEL":"override","stream":{{(stream ? "false" : "true")}},"store":true,"background":true,"previous_response_id":"invalid",
             "authorization":"override","future":{"tags":[true,null,{"mode":"exact"}]} } }
            """;
        var key = banana ? BananaPeel : Doubleword;
        using var response = await host.PostAsync(path, body, key, ("anthropic-version", "2023-06-01"));
        Assert.Contains(outputMarker, await GatewayTests.Success(response));
        var request = Assert.Single(host.Requests);
        Check(request, banana ? "bananapeel.com" : "api.doubleword.ai", banana ? "/api" + upstreamPath : upstreamPath, key);
        Assert.Equal(stream, request.Stream);
        Assert.Equal(stream, (bool?)request.Body!["stream"]);
        Assert.Equal(nativeModel, (string?)request.Body["model"]);
        Assert.Equal("exact", (string?)request.Body["future"]!["tags"]![2]!["mode"]);
        Assert.Null(request.Body["MODEL"]);
        Assert.NotEqual(true, (bool?)request.Body["store"]);
        Assert.Null(request.Body["background"]);
        Assert.Null(request.Body["previous_response_id"]);
        Assert.Null(request.Body["authorization"]);
        Assert.Null(request.Body[provider]);
        Assert.Equal("2023-06-01", request.Headers["anthropic-version"]);
    }

    [Theory]
    [InlineData("doubleword", "vendor/Model", "api.doubleword.ai", "/v1/chat/completions")]
    [InlineData("bananapeel", "banana-peel", "bananapeel.com", "/api/v1/responses")]
    [InlineData("creora", "creora-chat", "creora.ai", "/api/v1/chat/completions")]
    public async Task Vercel_uses_the_correct_protocol_and_keeps_nested_extensions(string provider, string model, string upstreamHost, string upstreamPath)
    {
        await using var host = await FixtureHost.StartAsync(request => FixtureHost.Reply(request with { Path = upstreamPath.StartsWith("/api/") ? upstreamPath[4..] : upstreamPath }),
            doublewordBananaPeelCreora: true);
        using var response = await host.PostAsync("/api/chat", $$"""
            {"model":"{{provider}}/{{model}}","messages":[{"id":"m","role":"user","parts":[{"type":"text","text":"go"}]}],
             "providerMetadata":{"{{provider}}":{"model":"ignored","native_tune":{"deep":[0,false]} } } }
            """, Doubleword, BananaPeel, Creora);
        var result = await GatewayTests.Success(response);
        Assert.Contains("hello", result);
        Assert.Contains("future_response", result);
        var request = Assert.Single(host.Requests);
        Check(request, upstreamHost, upstreamPath, provider == "doubleword" ? Doubleword : provider == "bananapeel" ? BananaPeel : Creora);
        Assert.Equal(model, (string?)request.Body!["model"]);
        Assert.False((bool?)request.Body["native_tune"]!["deep"]![1]);
    }

    [Fact]
    public async Task Doubleword_embeddings_keep_native_input_and_expose_safe_raw_metadata_on_Vercel()
    {
        await using var host = await FixtureHost.StartAsync(_ => WithHeaders(FixtureHost.Json("""
            {"object":"list","data":[{"index":0,"object":"embedding","embedding":[1.0,0.5]}],"usage":{"prompt_tokens":2,"total_tokens":2},"future":{"raw":[true]}}
            """)), doublewordBananaPeelCreora: true);
        using var native = await host.PostAsync("/v1/embeddings", """
            {"model":"doubleword/vendor/embed","input":[[10,12]],"debug_options":{"flag":true}}
            """, Doubleword);
        Assert.Equal("doubleword/vendor/embed", (string?)(await Json(native))["model"]);
        using var vercel = await host.PostAsync("/api/embeddings", """
            {"model":"doubleword/vendor/embed","values":["one"],"providerOptions":{"doubleword":{"input":["bad"],"debug_options":{"flag":true}}}}
            """, Doubleword);
        var metadata = (await Json(vercel))["response"]!;
        Assert.True((bool?)metadata["body"]!["future"]!["raw"]![0]);
        Assert.Equal("port-test", (string?)Assert.Single(metadata["headers"]!.AsObject(), pair => pair.Key.Equals("x-request-id", StringComparison.OrdinalIgnoreCase)).Value);
        Assert.DoesNotContain("secret-cookie", metadata.ToJsonString());
        Assert.All(host.Requests, request =>
        {
            Check(request, "api.doubleword.ai", "/v1/embeddings", Doubleword);
            Assert.True((bool?)request.Body!["debug_options"]!["flag"]);
        });
        Assert.Equal(10, (int?)host.Requests.First().Body!["input"]![0]![0]);
        Assert.Equal("one", (string?)host.Requests.Last().Body!["input"]![0]);
    }

    [Fact]
    public async Task Creora_buffered_chat_keeps_native_tools_and_safe_response_metadata()
    {
        await using var host = await FixtureHost.StartAsync(request => WithHeaders(FixtureHost.Reply(request with { Path = "/v1/chat/completions" })),
            doublewordBananaPeelCreora: true);
        using var response = await host.PostAsync("/v1/chat/completions", """
            {"model":"creora/creora-network","messages":[{"role":"user","content":"go"}],
             "creora":{"tools":[{"type":"native_tool","settings":{"depth":2}}],"model":"ignore"}}
            """, Creora);
        var metadata = (await Json(response))["providerMetadata"]!["creora"]!;
        Assert.True((bool?)metadata["response"]!["future_response"]);
        Assert.DoesNotContain("secret-cookie", metadata.ToJsonString());
        var request = Assert.Single(host.Requests);
        Check(request, "creora.ai", "/api/v1/chat/completions", Creora);
        Assert.Equal("creora-network", (string?)request.Body!["model"]);
        Assert.Equal(2, (int?)request.Body["tools"]![0]!["settings"]!["depth"]);
    }

    [Fact]
    public async Task Creora_generates_images_and_preserves_per_image_metadata_across_routes()
    {
        await using var host = await FixtureHost.StartAsync(_ => WithHeaders(FixtureHost.Json("""
            {"created":1,"data":[{"b64_json":"aGVsbG8=","native_image":{"seed":3}}],"diagnostics":{"nodes":["x"]}}
            """)), doublewordBananaPeelCreora: true);
        using var image = await host.PostAsync("/api/images", """
            {"model":"creora/creora-image","prompt":"draw","providerOptions":{"creora":{"motion":{"strength":2},"prompt":"ignore"}}}
            """, Creora);
        var result = await Json(image);
        Assert.Contains("data:image/webp;base64,aGVsbG8=", result["images"]!.ToJsonString());
        var metadata = result["providerMetadata"]!["creora"]!;
        Assert.Equal(3, (int?)metadata["data"]![0]!["native_image"]!["seed"]);
        Assert.Null(metadata["data"]![0]!["b64_json"]);
        Assert.Equal("x", (string?)metadata["diagnostics"]!["nodes"]![0]);
        using var stream = await host.PostAsync("/v1/images/generations", """
            {"model":"creora/creora-image","prompt":"draw","stream":true,"partial_images":1}
            """, Creora);
        Assert.Contains("image_generation.completed", await GatewayTests.Success(stream));
        using var adapted = await host.PostAsync("/v1/responses", """{"model":"creora/creora-image","input":"draw"}""", Creora);
        Assert.Contains("native_image", await GatewayTests.Success(adapted));
        Assert.Equal(2, (int?)host.Requests.First().Body!["motion"]!["strength"]);
        Assert.All(host.Requests, request =>
        {
            Check(request, "creora.ai", "/api/v1/images/generations", Creora);
            Assert.Equal("draw", (string?)request.Body!["prompt"]);
            Assert.Null(request.Body["stream"]);
            Assert.Null(request.Body["partial_images"]);
        });
    }

    [Fact]
    public async Task Creora_associates_video_status_and_content_with_the_signed_operation()
    {
        var poll = 0;
        await using var host = await FixtureHost.StartAsync(request => request.Path switch
        {
            "/api/v1/videos" => WithHeaders(FixtureHost.Json("""{"id":"job1","status":"queued","queue":{"ahead":[2]}}""")),
            "/api/v1/videos/job1" => FixtureHost.Json(Interlocked.Increment(ref poll) switch
            {
                1 => """{"status":"in_progress","queue":{"ahead":[1]}}""",
                2 => """{"status":"completed","result":{"fps":30}}""",
                _ => """{"status":"failed","error":{"code":"quota"}}"""
            }),
            "/api/v1/videos/job1/content" => new(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) { Headers = { ContentType = new MediaTypeHeaderValue("video/mp4") } } },
            _ => throw new InvalidOperationException(request.Path)
        }, configure: services => services.AddStreaikReferences(), doublewordBananaPeelCreora: true);

        using var start = await host.PostAsync("/api/videos", """
            {"model":"creora/creora-video","prompt":"move","duration":3,"providerOptions":{"creora":{"MODEL":"ignore","prompt":"ignore","motion":{"speed":2}}}}
            """, Creora);
        var started = await Json(start);
        Assert.Equal(2, (int?)started["providerMetadata"]!["creora"]!["queue"]!["ahead"]![0]);
        Assert.Equal("duration", (string?)started["warnings"]![0]!["feature"]);
        var operation = (string)started["operation"]!;
        Assert.StartsWith("creora/", operation);
        using var pending = await host.GetAsync("/api/videos/" + operation, Creora);
        Assert.Equal(1, (int?)(await Json(pending))["providerMetadata"]!["creora"]!["queue"]!["ahead"]![0]);
        using var completed = await host.GetAsync("/api/videos/" + operation, Creora);
        var output = await Json(completed);
        Assert.Equal("AQID", (string?)output["videos"]![0]!["data"]);
        Assert.Equal(30, (int?)output["providerMetadata"]!["creora"]!["result"]!["fps"]);
        using var failed = await host.GetAsync("/api/videos/" + operation, Creora);
        Assert.Equal("quota", (string?)(await Json(failed))["providerMetadata"]!["creora"]!["error"]!["code"]);
        Assert.Equal("creora-video", (string?)host.Requests.First().Body!["model"]);
        Assert.Equal(2, (int?)host.Requests.First().Body!["motion"]!["speed"]);
        Assert.Equal(new[] { "/api/v1/videos", "/api/v1/videos/job1", "/api/v1/videos/job1", "/api/v1/videos/job1/content", "/api/v1/videos/job1" },
            host.Requests.Select(request => request.Path));
        Assert.All(host.Requests, request => Check(request, "creora.ai", request.Path, Creora));
    }

    [Fact]
    public async Task Creora_rejects_unsupported_reference_inputs_without_contacting_upstream()
    {
        await using var host = await FixtureHost.StartAsync(configure: services => services.AddStreaikReferences(), doublewordBananaPeelCreora: true);
        using var video = await host.PostAsync("/api/videos", """
            {"model":"creora/creora-video","prompt":"move","image":{"type":"url","url":"https://example.com/reference.png"}}
            """, Creora);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, video.StatusCode);
        using var edit = await host.PostAsync("/v1/images/edits", """
            {"model":"creora/creora-image","prompt":"draw","images":[{"image_url":"https://example.com/reference.png"}]}
            """, Creora);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, edit.StatusCode);
        Assert.Empty(host.Requests);
    }

    private static async Task<JsonNode> Json(HttpResponseMessage response) => JsonNode.Parse(await GatewayTests.Success(response))!;

    private static void Check(FixtureHost.UpstreamRequest request, string host, string path, (string, string) key)
    {
        Assert.Equal(host, request.Host);
        Assert.Equal(path, request.Path);
        Assert.Equal("Bearer " + key.Item2, request.Headers["Authorization"]);
        Assert.DoesNotContain(request.Headers.Keys, name => name.EndsWith("-Key", StringComparison.OrdinalIgnoreCase));
    }

    private static HttpResponseMessage WithHeaders(HttpResponseMessage response)
    {
        response.Headers.TryAddWithoutValidation("x-request-id", "port-test");
        response.Headers.TryAddWithoutValidation("Set-Cookie", "secret-cookie");
        return response;
    }
}
