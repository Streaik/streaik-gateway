using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace Streaik.Gateway.Tests;

public sealed class AugureCognitivessAIHordeTests
{
    private const string StorageHost = "a223539ccf6caa2d76459c9727d276e6.r2.cloudflarestorage.com";
    private static readonly (string, string) HordeKey = ("x-aihorde-key", "fixture");

    [Fact]
    public async Task Discovers_models_with_provider_labels_limits_and_per_token_rates()
    {
        await using var host = await FixtureHost.StartAsync(request =>
        {
            if (request.Host == "api.augureai.ca")
            {
                Assert.Equal("Bearer fixture", request.Headers["Authorization"]);
                return FixtureHost.Json("""{"data":[{"id":"ossington-5","owned_by":"Augure"}]}""");
            }
            Assert.False(request.Headers.ContainsKey("Authorization"));
            return request.Host switch
            {
                "api.cognitivess.com" => FixtureHost.Json("""
                    {"data":[{"id":"org/model","context_length":56000,"max_output":4000,"owned_by":"Org","pricing":{"prompt":2,"completion":8,"cached_prompt":0.5}}]}
                    """),
                "oai.aihorde.net" => FixtureHost.Json("""
                    {"data":[{"id":"worker/text","clean_name":"Worker Text","backend":"Worker"},{"id":"aihorde-image"}]}
                    """),
                _ => throw new InvalidOperationException(request.Host)
            };
        }, augureCognitivessAIHorde: true);
        using var response = await host.GetAsync("/v1/models", ("x-augure-key", "fixture"));
        var models = JsonNode.Parse(await GatewayTests.Success(response))!["data"]!.AsArray()
            .ToDictionary(model => (string)model!["id"]!, model => model!);
        Assert.Equal(0.0000015m, Rate(models["augure/ossington-5"], "input"));
        var cognitive = models["cognitivessai/org/model"];
        Assert.Equal(56000, (int)cognitive["context_window"]!);
        Assert.Equal(4000, (int)cognitive["max_tokens"]!);
        Assert.Equal(0.000002m, Rate(cognitive, "input"));
        Assert.Equal(0.000008m, Rate(cognitive, "output"));
        Assert.Equal(0.0000005m, Rate(cognitive, "input_cache_read"));
        Assert.Equal("Worker Text", (string?)models["aihorde/worker/text"]["name"]);
        Assert.Equal("Worker", (string?)models["aihorde/worker/text"]["owned_by"]);
        Assert.Equal("image", (string?)models["aihorde/aihorde-image"]["type"]);
        Assert.All(host.Requests, request => Assert.Equal("/v1/models", request.Path));
    }

    [Theory]
    [InlineData("augure", "api.augureai.ca", false)]
    [InlineData("augure", "api.augureai.ca", true)]
    [InlineData("cognitivessai", "api.cognitivess.com", false)]
    [InlineData("cognitivessai", "api.cognitivess.com", true)]
    [InlineData("aihorde", "oai.aihorde.net", false)]
    [InlineData("aihorde", "oai.aihorde.net", true)]
    public async Task Native_chat_preserves_extension_fields_and_sse(string provider, string upstream, bool stream)
    {
        await using var host = await FixtureHost.StartAsync(FixtureHost.Reply, augureCognitivessAIHorde: true);
        using var response = await host.PostAsync("/v1/chat/completions", $$"""
            {"model":"{{provider}}/org/model","messages":[{"role":"user","content":"Hi"}],"stream":{{(stream ? "true" : "false")}},
             "{{provider}}":{"provider_knob":{"value":5},"model":"ignored"} }
            """, ($"x-{provider}-key", "fixture"));
        var text = await GatewayTests.Success(response);
        Assert.Contains("future_response", text);
        Assert.Contains("hello", text);
        if (stream) Assert.Contains("[DONE]", text);
        var call = Assert.Single(host.Requests);
        Assert.Equal(upstream, call.Host);
        Assert.Equal("/v1/chat/completions", call.Path);
        Assert.Equal("Bearer fixture", call.Headers["Authorization"]);
        Assert.Equal("org/model", (string?)call.Body!["model"]);
        Assert.Equal(5, (int)call.Body["provider_knob"]!["value"]!);
        Assert.Null(call.Body[provider]);
    }

    [Theory]
    [InlineData("cognitivessai", "messages", false, "messages")]
    [InlineData("cognitivessai", "messages", true, "messages")]
    [InlineData("cognitivessai", "responses", false, "responses")]
    [InlineData("cognitivessai", "responses", true, "responses")]
    [InlineData("augure", "messages", true, "chat/completions")]
    [InlineData("augure", "responses", false, "chat/completions")]
    [InlineData("aihorde", "messages", false, "chat/completions")]
    [InlineData("aihorde", "responses", true, "chat/completions")]
    public async Task Messages_and_responses_choose_the_correct_native_or_bridged_endpoint(string provider, string protocol, bool stream, string upstream)
    {
        await using var host = await FixtureHost.StartAsync(FixtureHost.Reply, augureCognitivessAIHorde: true);
        var input = protocol == "messages"
            ? "\"messages\":[{\"role\":\"user\",\"content\":\"Hi\"}],\"max_tokens\":80"
            : "\"input\":\"Hi\"";
        using var response = await host.PostAsync($"/v1/{protocol}", $$"""
            {"model":"{{provider}}/org/model",{{input}},"stream":{{(stream ? "true" : "false")}},"{{provider}}":{"provider_knob":true} }
            """, ($"x-{provider}-key", "fixture"), ("anthropic-version", "2023-06-01"));
        var text = await GatewayTests.Success(response);
        Assert.Contains("hello", text);
        var call = Assert.Single(host.Requests);
        Assert.Equal($"/v1/{upstream}", call.Path);
        Assert.True((bool?)call.Body!["provider_knob"]);
        Assert.Null(call.Body[provider]);
        if (upstream == protocol) Assert.Contains(stream && protocol == "messages" ? "future_content" : "future_response", text);
        if (upstream == "messages") Assert.Equal("2023-06-01", call.Headers["anthropic-version"]);
    }

    [Theory]
    [InlineData("augure")]
    [InlineData("cognitivessai")]
    [InlineData("aihorde")]
    public async Task Vercel_text_uses_chat_completions(string provider)
    {
        await using var host = await FixtureHost.StartAsync(FixtureHost.Reply, augureCognitivessAIHorde: true);
        using var response = await host.PostAsync("/api/chat", $$"""
            {"model":"{{provider}}/org/model","messages":[{"id":"u1","role":"user","parts":[{"type":"text","text":"Hi"}]}]}
            """, ($"x-{provider}-key", "fixture"));
        Assert.Contains("hello", await GatewayTests.Success(response));
        Assert.Equal("/v1/chat/completions", Assert.Single(host.Requests).Path);
    }

    [Fact]
    public async Task AI_Horde_Vercel_image_generation_polls_and_preserves_raw_job_metadata()
    {
        var polls = 0;
        await using var host = await FixtureHost.StartAsync(request => request.Path == "/api/v2/generate/check/job" && ++polls == 1
            ? FixtureHost.Json("""{"done":false}""") : HordeImageReply(request), augureCognitivessAIHorde: true);
        using var response = await host.PostAsync("/api/images", """
            {"model":"aihorde/aihorde-image","prompt":"Windmill","n":1,"seed":42,"aspectRatio":"1:1","size":"512x512",
             "providerOptions":{"aihorde":{"params":{"steps":7},"models":["explicit-model"],"trusted_workers":true}}}
            """, HordeKey);
        var result = JsonNode.Parse(await GatewayTests.Success(response))!;
        Assert.Equal("data:image/webp;base64,AQID", (string?)result["images"]![0]);
        Assert.Equal("aspectRatio", (string?)result["warnings"]![0]!["feature"]);
        Assert.Equal("native-worker", (string?)result["providerMetadata"]!["aihorde"]!["generations"]![0]!["worker_name"]);
        Assert.True((bool?)result["providerMetadata"]!["aihorde"]!["future_job"]!["keep"]);
        Assert.Equal(2, polls);
        var outgoing = host.Requests.First().Body!;
        Assert.Equal("Windmill", (string?)outgoing["prompt"]);
        Assert.Equal("42", (string?)outgoing["params"]!["seed"]);
        Assert.Equal(512, (int)outgoing["params"]!["width"]!);
        Assert.Equal(512, (int)outgoing["params"]!["height"]!);
        Assert.Equal(7, (int)outgoing["params"]!["steps"]!);
        Assert.Equal("explicit-model", (string?)outgoing["models"]![0]);
        Assert.True((bool?)outgoing["trusted_workers"]);
        Assert.Null(outgoing["model"]);
    }

    [Fact]
    public async Task AI_Horde_edits_with_mask_and_extra_images_and_streams_completion()
    {
        await using var host = await FixtureHost.StartAsync(HordeImageReply, augureCognitivessAIHorde: true);
        using var response = await host.PostAsync("/v1/images/edits", """
            {"model":"aihorde/aihorde-image","prompt":"Windmill","stream":true,"images":[{"image_url":"data:image/png;base64,Ag=="},{"image_url":"Aw=="}],"mask":{"image_url":"BA=="}}
            """, HordeKey);
        var output = await GatewayTests.Success(response);
        Assert.Contains("image_edit.completed", output);
        Assert.Contains("AQID", output);
        var outgoing = host.Requests.First().Body!;
        Assert.Equal("Ag==", (string?)outgoing["source_image"]);
        Assert.Equal("BA==", (string?)outgoing["source_mask"]);
        Assert.Equal("inpainting", (string?)outgoing["source_processing"]);
        Assert.Equal("Aw==", (string?)outgoing["extra_source_images"]![0]!["image"]);
        Assert.Null(outgoing["models"]);
    }

    [Fact]
    public async Task AI_Horde_image_model_routes_to_images_instead_of_text_chat()
    {
        await using var host = await FixtureHost.StartAsync(HordeImageReply, augureCognitivessAIHorde: true);
        using var response = await host.PostAsync("/v1/responses", """{"model":"aihorde/aihorde-image","input":"Windmill"}""", HordeKey);
        var data = JsonNode.Parse(await GatewayTests.Success(response))!;
        Assert.Equal("AQID", (string?)data["output"]![0]!["result"]);
        Assert.True((bool?)data["metadata"]!["aihorde"]!["future_job"]!["keep"]);
        Assert.Equal("/api/v2/generate/async", host.Requests.First().Path);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AI_Horde_stops_on_fault_or_rejects_an_untrusted_asset(bool faulted)
    {
        await using var host = await FixtureHost.StartAsync(request => request.Path switch
        {
            "/api/v2/generate/check/job" when faulted => FixtureHost.Json("""{"faulted":true,"done":false}"""),
            "/api/v2/generate/status/job" => FixtureHost.Json("""{"generations":[{"img":"https://invalid.example/not-trusted.webp"}]}"""),
            _ => HordeImageReply(request)
        }, augureCognitivessAIHorde: true);
        using var response = await host.PostAsync("/v1/images/generations", """{"model":"aihorde/aihorde-image","prompt":"Windmill"}""", HordeKey);
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(faulted ? 2 : 3, host.Requests.Count);
    }

    private static decimal Rate(JsonNode model, string field)
        => decimal.Parse(model["pricing"]![field]!.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture);

    private static HttpResponseMessage HordeImageReply(FixtureHost.UpstreamRequest request)
    {
        Assert.False(request.Headers.ContainsKey("Authorization"));
        if (request.Host == StorageHost)
        {
            Assert.Equal("/stable-horde/job.webp", request.Path);
            Assert.False(request.Headers.ContainsKey("apikey"));
            Assert.False(request.Headers.ContainsKey("Client-Agent"));
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) { Headers = { ContentType = new MediaTypeHeaderValue("image/webp") } } };
        }
        Assert.Equal("aihorde.net", request.Host);
        Assert.Equal("fixture", request.Headers["apikey"]);
        Assert.StartsWith("Streaik:", request.Headers["Client-Agent"]);
        return request.Path switch
        {
            "/api/v2/generate/async" => FixtureHost.Json("""{"id":"job"}"""),
            "/api/v2/generate/check/job" => FixtureHost.Json("""{"done":true}"""),
            "/api/v2/generate/status/job" => FixtureHost.Json($$"""
                {"done":true,"future_job":{"keep":true},"generations":[{"img":"https://{{StorageHost}}/stable-horde/job.webp","worker_name":"native-worker","state":"ok"}]}
                """),
            _ => throw new InvalidOperationException(request.Path)
        };
    }
}
