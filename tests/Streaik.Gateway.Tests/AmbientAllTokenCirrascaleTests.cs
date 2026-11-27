using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Streaik.Gateway.Tests;

public sealed class AmbientAllTokenCirrascaleTests
{
    private static readonly (string, string) Ambient = ("X-Ambient-Key", "ambient-key");
    private static readonly (string, string) AllToken = ("X-AllToken-Key", "alltoken-key");
    private static readonly (string, string) Cirrascale = ("X-Cirrascale-Key", "cirrascale-key");

    [Fact]
    public async Task Model_listings_preserve_native_ids_and_use_only_the_matching_key()
    {
        await using var host = await Start(request => request.Host switch
        {
            "api.ambient.xyz" => FixtureHost.Json("""{"data":[{"id":"lab/model","name":"Ambient Model","context_length":8192}]}"""),
            "api.alltoken.ai" => FixtureHost.Json("""{"data":[{"id":"lab/paint","type":"image","owned_by":"lab"}]}"""),
            "aisuite.cirrascale.com" => FixtureHost.Json("""{"llm":["lab/model"],"text_to_image":["lab/paint"],"unsupported":["not-listed"]}"""),
            _ => throw new InvalidOperationException(request.Host)
        });
        using var response = await host.GetAsync("/v1/models", Ambient, AllToken, Cirrascale);
        var models = (await Body(response))["data"]!.AsArray().ToDictionary(node => (string)node!["id"]!);
        Assert.Equal("Ambient Model", (string?)models["ambient/lab/model"]!["name"]);
        Assert.Equal(8192, (int?)models["ambient/lab/model"]!["context_window"]);
        Assert.Equal("lab", (string?)models["alltoken/lab/paint"]!["owned_by"]);
        Assert.Equal("language", (string?)models["cirrascale/lab/model"]!["type"]);
        Assert.Equal("image", (string?)models["cirrascale/lab/paint"]!["type"]);
        Assert.Equal("reranking", (string?)models["cirrascale/BAAI/bge-reranker-base"]!["type"]);
        Assert.DoesNotContain("cirrascale/not-listed", models.Keys);
        Assert.Equal(3, host.Requests.Count);
        AssertRequest(host.Requests.Single(r => r.Host == "api.ambient.xyz"), "/v1/models", Ambient);
        AssertRequest(host.Requests.Single(r => r.Host == "api.alltoken.ai"), "/v1/models", AllToken);
        AssertRequest(host.Requests.Single(r => r.Host == "aisuite.cirrascale.com"), "/apis/v2/models", Cirrascale);
    }

    [Theory]
    [InlineData("/v1/chat/completions", false)]
    [InlineData("/v1/responses", false)]
    [InlineData("/v1/responses", true)]
    [InlineData("/v1/messages", false)]
    [InlineData("/v1/messages", true)]
    public async Task Ambient_preserves_native_protocols_and_upstream_extension_fields(string path, bool stream)
    {
        await using var host = await Start(FixtureHost.Reply);
        var request = GatewayTests.Request(path, "ambient/lab/model", stream);
        request["ambient"] = JsonNode.Parse("""{"new_field":{"list":[0,false,null]},"model":"not-the-model"}""");
        using var response = await host.PostAsync(path, request.ToJsonString(), Ambient, AllToken, ("anthropic-version", "2023-06-01"));
        var text = await GatewayTests.Success(response);
        Assert.Contains(stream && path == "/v1/messages" ? "future_content" : "future_response", text);
        var upstream = Assert.Single(host.Requests, r => r.Path != "/v1/models");
        Assert.Equal("api.ambient.xyz", upstream.Host);
        AssertRequest(upstream, path, Ambient);
        Assert.Equal("lab/model", (string?)upstream.Body!["model"]);
        Assert.Equal("[0,false,null]", upstream.Body["new_field"]!["list"]!.ToJsonString());
        if (path == "/v1/messages") Assert.Equal("2023-06-01", upstream.Headers["anthropic-version"]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Ambient_chat_ends_without_done_and_keeps_usage_if_supplied(bool trailer)
    {
        const string finished = """{"id":"chat_1","model":"model","created":1700000000000,"choices":[{"index":0,"delta":{"content":"hello"},"finish_reason":"stop"}],"future_response":{"value":4}}""";
        const string usage = """{"id":"chat_1","model":"model","created":1700000000000,"choices":[],"usage":{"prompt_tokens":2,"completion_tokens":3,"total_tokens":5}}""";
        await using var host = await Start(request => request.Path == "/v1/models" ? FixtureHost.Reply(request) : new(HttpStatusCode.OK)
        {
            Content = new StringContent($": keepalive\n\ndata: {finished}\n\n" + (trailer ? $"data: {usage}\n\n" : ": keepalive\n\n")
                + "data: must-not-be-read\n\n", Encoding.UTF8, "text/event-stream")
        });
        using var response = await host.PostAsync("/v1/chat/completions", """
            {"model":"ambient/model","messages":[{"role":"user","content":"hi"}],"stream":true}
            """, Ambient);
        var text = await GatewayTests.Success(response);
        Assert.Contains("data: [DONE]", text);
        Assert.Contains("future_response", text);
        Assert.DoesNotContain("1700000000000", text);
        Assert.Equal(trailer, text.Contains("\"total_tokens\":5", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("alltoken", "api.alltoken.ai", "/v1/chat/completions")]
    [InlineData("cirrascale", "aisuite.cirrascale.com", "/apis/v2/chat/completions")]
    public async Task Translated_requests_use_the_native_chat_endpoint(string id, string domain, string path)
    {
        await using var host = await Start(request => request.Path.EndsWith("/models", StringComparison.Ordinal)
            ? FixtureHost.Json("""{"data":[{"id":"lab/model","type":"language"}]}""")
            : FixtureHost.Reply(request with { Path = "/v1/chat/completions" }));
        foreach (var clientPath in new[] { "/v1/responses", "/v1/messages" })
        {
            using var response = await host.PostAsync(clientPath, GatewayTests.Request(clientPath, id + "/lab/model", false).ToJsonString(), AllToken, Cirrascale);
            Assert.Contains("hello", await GatewayTests.Success(response));
        }
        var upstream = host.Requests.Where(r => r.Method == "POST").ToArray();
        Assert.Equal(2, upstream.Length);
        Assert.All(upstream, request =>
        {
            Assert.Equal(domain, request.Host);
            AssertRequest(request, path, id == "alltoken" ? AllToken : Cirrascale);
            Assert.Equal("lab/model", (string?)request.Body!["model"]);
        });
    }

    [Fact]
    public async Task Cirrascale_preserves_image_options_completion_metadata_and_rerank_diagnostics()
    {
        const string ranked = """{"id":"ranking-1","data":[{"index":1,"relevance_score":0.625,"future_rank":true}],"debug":{"ignored":null}}""";
        await using var host = await Start(request => request.Path switch
        {
            "/apis/v2/images/generations" => FixtureHost.Json("""{"created":1,"data":[{"b64_json":"YWJj","diagnostic":{"index":3}}],"future":{"v":[2,false,null]}}"""),
            "/apis/v2/reranker" => FixtureHost.Json(ranked),
            _ => throw new InvalidOperationException(request.Path)
        });
        using var image = await host.PostAsync("/api/images", """
            {"model":"cirrascale/lab/paint","prompt":"draw","size":"512x512","seed":27,"providerOptions":{"cirrascale":{"guidance_scale":3.5,"future":{"nested":[false,null]},"model":"wrong"}}}
            """, Cirrascale);
        var result = await Body(image);
        var metadata = result["providerMetadata"]!["cirrascale"]!;
        Assert.Equal("[2,false,null]", metadata["future"]!["v"]!.ToJsonString());
        Assert.Equal(3, (int?)metadata["data"]![0]!["diagnostic"]!["index"]);
        Assert.Null(metadata["data"]![0]!["b64_json"]);
        var sent = Assert.Single(host.Requests);
        AssertRequest(sent, "/apis/v2/images/generations", Cirrascale);
        Assert.Equal("lab/paint", (string?)sent.Body!["model"]);
        Assert.Equal(27, (int?)sent.Body["seed"]);
        Assert.Equal(3.5, (double?)sent.Body["guidance_scale"]);
        Assert.Equal("b64_json", (string?)sent.Body["response_format"]);
        Assert.Equal("[false,null]", sent.Body["future"]!["nested"]!.ToJsonString());

        using var stream = await host.PostAsync("/v1/images/generations", """{"model":"cirrascale/lab/paint","prompt":"draw","stream":true}""", Cirrascale);
        var frames = await GatewayTests.Success(stream);
        Assert.Contains("event: image_generation.completed", frames);
        Assert.Contains("providerMetadata", frames);

        using var rerank = await host.PostAsync("/api/rerank", """
            {"model":"cirrascale/BAAI/bge-reranker-base","query":"needle","documents":{"type":"object","values":["a",{"text":"b"}]},"topN":1,
             "providerOptions":{"cirrascale":{"future":false},"alltoken":{"future":true}}}
            """, Cirrascale);
        var ranking = await Body(rerank);
        Assert.Equal(0.625, (double?)ranking["ranking"]![0]!["relevanceScore"]);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(ranked), ranking["providerMetadata"]!["cirrascale"]));
        var rerankUpstream = host.Requests.Last();
        AssertRequest(rerankUpstream, "/apis/v2/reranker", Cirrascale);
        Assert.Equal(1, (int?)rerankUpstream.Body!["top_n"]);
        Assert.Equal("b", (string?)rerankUpstream.Body["documents"]![1]!["text"]);
        Assert.False((bool?)rerankUpstream.Body["future"]);
        Assert.Null(rerankUpstream.Body["alltoken"]);
    }

    [Fact]
    public async Task AllToken_routes_image_models_without_a_chat_call_and_polls_async_generation()
    {
        await using var host = await Start(request => request.Path switch
        {
            "/v1/models" => FixtureHost.Json("""{"data":[{"id":"lab/paint","type":"image"}]}"""),
            "/v1/images/generations/async" => FixtureHost.Json("""{"id":"image 7","status":"queued"}"""),
            "/v1/images/generations/image%207" => FixtureHost.Json("""{"status":"completed","data":[{"b64_json":"YWJj"}],"future":{"score":0.5}}"""),
            _ => throw new InvalidOperationException(request.Path)
        });
        using var response = await host.PostAsync("/v1/chat/completions", """{"model":"alltoken/lab/paint","messages":[{"role":"user","content":"draw"}]}""", AllToken);
        Assert.Contains("YWJj", await GatewayTests.Success(response));
        Assert.Collection(host.Requests,
            r => AssertRequest(r, "/v1/models", AllToken),
            r => AssertRequest(r, "/v1/images/generations/async", AllToken),
            r => AssertRequest(r, "/v1/images/generations/image%207", AllToken));
    }

    [Fact]
    public async Task AllToken_edits_with_presigned_source_and_mask_and_credential_free_output_download()
    {
        await using var host = await Start(request => request.Path switch
        {
            "/v1/uploads/presign" => FixtureHost.Json(request.Body!["purpose"]!.GetValue<string>() == "image_edit_source"
                ? """{"upload_id":"source-id","upload_url":"https://uploads.example.com/source?signature=source","required_headers":{"x-amz-checksum-sha256":"abc"}}"""
                : """{"upload_id":"mask-id","upload_url":"https://uploads.example.com/mask?signature=mask"}"""),
            "/source?signature=source" or "/mask?signature=mask" => new(HttpStatusCode.OK),
            "/v1/images/edits" => FixtureHost.Json("""{"status":"completed","output_format":"png","data":[{"r2_url":"https://outputs.example.com/image?signature=secret","diagnostic":7}],"usage":{"total_tokens":4},"future":{"v":false}}"""),
            "/image?signature=secret" => Binary("image/png"),
            _ => throw new InvalidOperationException(request.Path)
        });
        using var response = await host.PostAsync("/v1/images/edits", """
            {"model":"alltoken/lab/paint","prompt":"retouch","images":[{"image_url":"data:image/png;base64,YWJj"}],"mask":{"image_url":"data:image/png;base64,YWJj"},"alltoken":{"future_option":{"v":0},"image_upload_id":"wrong","api_key":"wrong"}}
            """, AllToken, Ambient);
        var body = await Body(response);
        Assert.Equal("YWJj", (string?)body["data"]![0]!["b64_json"]);
        Assert.Equal(4, (int?)body["usage"]!["total_tokens"]);
        var metadata = body["providerMetadata"]!["alltoken"]!;
        Assert.Equal(7, (int?)metadata["data"]![0]!["diagnostic"]);
        Assert.False((bool?)metadata["future"]!["v"]);
        Assert.DoesNotContain("signature=secret", body.ToJsonString());
        var requests = host.Requests.ToArray();
        Assert.Equal(6, requests.Length);
        AssertRequest(requests[0], "/v1/uploads/presign", AllToken);
        Assert.Equal("kAFQmDzST7DWlj99KOF/cg==", (string?)requests[0].Body!["content_md5"]);
        Assert.Equal("PUT", requests[1].Method);
        Assert.Equal("abc", requests[1].Headers["x-amz-checksum-sha256"]);
        AssertRequest(requests[2], "/v1/uploads/presign", AllToken);
        Assert.Equal("image_edit_mask", (string?)requests[2].Body!["purpose"]);
        Assert.Equal("PUT", requests[3].Method);
        AssertRequest(requests[4], "/v1/images/edits", AllToken);
        Assert.Equal("source-id", (string?)requests[4].Body!["image_upload_id"]);
        Assert.Equal("mask-id", (string?)requests[4].Body!["mask_upload_id"]);
        Assert.Equal(0, (int?)requests[4].Body!["future_option"]!["v"]);
        Assert.Null(requests[4].Body!["api_key"]);
        foreach (var request in new[] { requests[1], requests[3], requests[5] })
        {
            Assert.False(request.Headers.ContainsKey("Authorization"));
            Assert.False(request.Headers.ContainsKey(AllToken.Item1));
            Assert.False(request.Headers.ContainsKey(Ambient.Item1));
        }
    }

    [Fact]
    public async Task AllToken_video_jobs_keep_metadata_across_status_and_download_without_credentials()
    {
        var polls = 0;
        await using var host = await Start(request => request.Path switch
        {
            "/v1/videos/generations" => FixtureHost.Json("""{"id":"job 1","status":"queued","future":{"v":0}}"""),
            "/v1/videos/generations/job%201" when Interlocked.Increment(ref polls) == 1 => FixtureHost.Json("""{"status":"running","percent":25}"""),
            "/v1/videos/generations/job%201" => FixtureHost.Json("""{"status":"completed","video_url":"https://outputs.example.com/clip?signature=secret","future":{"v":[null,true]}}"""),
            "/clip?signature=secret" => Binary("video/mp4"),
            _ => throw new InvalidOperationException(request.Path)
        });
        using var start = await host.PostAsync("/api/videos", """
            {"model":"alltoken/lab/video","prompt":"animate","duration":5,"aspectRatio":"16:9","generateAudio":false,"fps":30,
             "image":{"type":"url","url":"https://inputs.example.com/frame.png"},"providerOptions":{"alltoken":{"future_option":[false,null]},"cirrascale":{"future_option":123}}}
            """, AllToken);
        var initial = await Body(start);
        Assert.Equal(0, (int?)initial["providerMetadata"]!["alltoken"]!["future"]!["v"]);
        var operation = (string)initial["operation"]!;
        using var pending = await host.GetAsync("/api/videos/" + operation, AllToken);
        var progress = await Body(pending);
        Assert.Equal("pending", (string?)progress["status"]);
        Assert.Equal(25, (int?)progress["providerMetadata"]!["alltoken"]!["percent"]);
        using var complete = await host.GetAsync("/api/videos/" + operation, AllToken);
        var result = await Body(complete);
        Assert.Equal("completed", (string?)result["status"]);
        Assert.Equal("YWJj", (string?)result["videos"]![0]!["data"]);
        Assert.Equal("[null,true]", result["providerMetadata"]!["alltoken"]!["future"]!["v"]!.ToJsonString());
        Assert.DoesNotContain("signature=secret", result.ToJsonString());
        var submit = host.Requests.First();
        AssertRequest(submit, "/v1/videos/generations", AllToken);
        Assert.Equal("16:9", (string?)submit.Body!["ratio"]);
        Assert.Equal("first_frame", (string?)submit.Body["content"]![0]!["role"]);
        Assert.Equal("[false,null]", submit.Body["future_option"]!.ToJsonString());
        Assert.Null(submit.Body["cirrascale"]);
        Assert.False(host.Requests.Last().Headers.ContainsKey("Authorization"));
    }

    private static void AssertRequest(FixtureHost.UpstreamRequest request, string path, (string Name, string Key) credentials)
    {
        Assert.Equal(path, request.Path);
        Assert.Equal("Bearer " + credentials.Key, request.Headers["Authorization"]);
        Assert.DoesNotContain(request.Headers.Keys, name => name is "X-Ambient-Key" or "X-AllToken-Key" or "X-Cirrascale-Key");
    }

    private static Task<FixtureHost> Start(Func<FixtureHost.UpstreamRequest, HttpResponseMessage> reply)
        => FixtureHost.StartAsync(reply, configure: services => services.AddStreaikReferences(), ambientAllTokenCirrascale: true);

    private static async Task<JsonNode> Body(HttpResponseMessage response) => JsonNode.Parse(await GatewayTests.Success(response))!;

    private static HttpResponseMessage Binary(string type) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent("abc"u8.ToArray()) { Headers = { ContentType = new(type) } }
    };
}
