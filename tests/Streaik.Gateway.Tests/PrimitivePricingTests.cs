using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Streaik.OpenAI.Models;

namespace Streaik.Gateway.Tests;

public sealed class PrimitivePricingTests
{
    private static readonly (string, string) OpenAiKey = ("X-OpenAI-Key", "openai-test");
    private static readonly (string, string) RouterKey = ("X-OpenRouter-Key", "router-test");
    private const string ImageUsage = """
        {"input_tokens":50,"output_tokens":50,"total_tokens":100,
         "input_tokens_details":{"text_tokens":10,"image_tokens":40},
         "output_tokens_details":{"image_tokens":50,"text_tokens":0}}
        """;

    [Theory]
    [InlineData("gpt-image-2.6-flare", "png", false, true)]
    [InlineData("gpt-image-2.5-flare-2026-09-08", "gif", false, true)]
    [InlineData("gpt-image-2-2026-04-21", "png", true, true)]
    [InlineData("gpt-image-2.6-flare", "svg", false, false)]
    [InlineData("unlisted-image", "png", false, false)]
    public async Task OpenAI_images_use_only_documented_image_usage_and_do_not_price_unknown_or_partial_usage(
        string model, string format, bool edit, bool priced)
    {
        const string svg = """, "svg_usage":{"input_tokens":2,"model":"author","output_tokens":4,"reasoning_tokens":1,"total_tokens":6}""";
        await using var host = await FixtureHost.StartAsync(request =>
        {
            Assert.Equal(edit ? "/v1/images/edits" : "/v1/images/generations", request.Path);
            if (request.Body is { } body) Assert.Equal(model, (string?)body["model"]);
            return FixtureHost.Json($$"""{"created":1,"data":[{"b64_json":"AQID"}],"output_format":"{{format}}","usage":{{ImageUsage}}{{(format == "svg" ? svg : "")}}}""");
        });
        var files = edit ? """, "files":[{"type":"file","data":"AQID","mediaType":"image/png"}]""" : "";
        using var response = await host.PostAsync("/api/images", $$"""{"model":"openai/{{model}}","prompt":"a scene"{{files}}}""", OpenAiKey);
        var result = await Read(response);
        Assert.Equal(priced ? 0.00187m : (decimal?)null, Cost(result));
        Assert.Equal(100, (int?)result["usage"]!["totalTokens"]);
        Assert.Equal($"openai/{model}", (string?)result["response"]!["modelId"]);
    }

    [Fact]
    public void OpenAI_image_dto_keeps_documented_usage_and_does_not_define_billing_extensions()
    {
        var usage = JsonSerializer.Deserialize<OpenAIImageUsage>(ImageUsage)!;
        Assert.Equal(40, usage.InputTokensDetails!.ImageTokens);
        Assert.Equal(50, usage.OutputTokensDetails!.ImageTokens);
        Assert.Equal(new[] { "ImageTokens", "TextTokens" }, typeof(OpenAIImageTokenDetails).GetProperties().Select(p => p.Name).Order());
        var image = JsonSerializer.Deserialize<OpenAIImagesResponse>("""
            {"created":123,"output_format":"svg","safety_identifier":"opaque-id",
             "svg_usage":{"input_tokens":2,"model":"author","output_tokens":4,"reasoning_tokens":1,"total_tokens":6}}
            """)!;
        Assert.Equal("opaque-id", image.SafetyIdentifier);
        Assert.Equal(1, image.SvgUsage!.ReasoningTokens);
    }

    [Theory]
    [InlineData("0", "0")]
    [InlineData("\"0.004\"", "0.004")]
    [InlineData("-1", null)]
    [InlineData("{}", null)]
    public async Task OpenRouter_image_cost_is_numeric_and_appears_only_on_the_sdk_route(string reported, string? expected)
    {
        await using var host = await FixtureHost.StartAsync(request =>
        {
            Assert.Equal("/api/v1/images", request.Path);
            return FixtureHost.Json($$"""
                {"created":1,"data":[{"b64_json":"AQID"}],"usage":{"input_tokens":1,"output_tokens":2,"total_tokens":3,"cost":{{reported}}} }
                """);
        }, media: true);
        const string request = """{"model":"openrouter/vendor/image","prompt":"a scene"}""";
        using var sdk = await host.PostAsync("/api/images", request, RouterKey);
        var result = await Read(sdk);
        Assert.Equal(expected is null ? null : JsonNode.Parse(expected)!.GetValue<decimal>(), Cost(result));
        Assert.Equal(3, (int?)result["providerMetadata"]!["openrouter"]!["usage"]!["total_tokens"]);
        using var native = await host.PostAsync("/v1/images/generations", request, RouterKey);
        var nativeBody = await Read(native);
        Assert.Null(nativeBody["providerMetadata"]);
        Assert.Null(nativeBody["usage"]!["cost"]);
        Assert.Equal(3, (int?)nativeBody["usage"]!["total_tokens"]);
    }

    [Fact]
    public async Task Catalog_speech_cost_is_only_on_the_sdk_response_and_finish_part()
    {
        await using var host = await FixtureHost.StartAsync(_ => Binary("audio/mpeg"));
        using var batch = await host.PostAsync("/api/speech", """{"model":"openai/tts-1","text":"hello","voice":"alloy"}""", OpenAiKey);
        Assert.Equal(0.000075m, Cost(await Read(batch)));
        using var stream = await host.PostAsync("/api/speech/stream", """{"model":"openai/tts-1","text":[{"text":"hello"}],"voice":"alloy"}""", OpenAiKey);
        var frames = (await GatewayTests.Success(stream)).Split('\n').Where(line => line.StartsWith("data: {", StringComparison.Ordinal))
            .Select(line => JsonNode.Parse(line[6..])!).ToList();
        var finish = Assert.Single(frames, part => Cost(part).HasValue);
        Assert.Equal("finish", (string?)finish["type"]);
        Assert.Equal(0.000075m, Cost(finish));
        using var native = await host.PostAsync("/v1/audio/speech", """{"model":"openai/tts-1","input":"hello","voice":"alloy"}""", OpenAiKey);
        Assert.Equal(HttpStatusCode.OK, native.StatusCode);
        Assert.Equal("audio/mpeg", native.Content.Headers.ContentType!.MediaType);
        Assert.Equal(new byte[] { 1, 2, 3 }, await native.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Embedding_uses_the_input_meter_and_rerank_preserves_provider_usage()
    {
        await using var host = await FixtureHost.StartAsync(request => request.Path switch
        {
            "/v1/embeddings" => FixtureHost.Json("""{"model":"text-embedding-3-small","data":[{"index":0,"embedding":[1.0,0.0]}],"usage":{"prompt_tokens":20,"total_tokens":21}}"""),
            "/api/v1/rerank" => FixtureHost.Json("""{"id":"r1","results":[{"index":0,"relevance_score":0.8}],"usage":{"cost":0.015,"native":true}}"""),
            _ => throw new InvalidOperationException(request.Path)
        }, media: true);
        using var embed = await host.PostAsync("/api/embeddings", """{"model":"openai/text-embedding-3-small","values":["a"]}""", OpenAiKey);
        var embeddings = await Read(embed);
        Assert.Equal(0.0000004m, Cost(embeddings));
        Assert.Equal(21, (int?)embeddings["usage"]!["tokens"]);
        using var rerank = await host.PostAsync("/api/rerank", """{"model":"openrouter/vendor/rank","query":"a","documents":{"type":"text","values":["a"]}}""", RouterKey);
        var ranking = await Read(rerank);
        Assert.Equal(0.015m, Cost(ranking));
        Assert.True((bool?)ranking["providerMetadata"]!["openrouter"]!["usage"]!["native"]);
    }

    [Fact]
    public async Task OpenRouter_video_has_no_cost_until_completion()
    {
        var polls = 0;
        await using var host = await FixtureHost.StartAsync(request => request.Path switch
        {
            "/api/v1/videos" => FixtureHost.Json("""{"id":"job","usage":{"cost":12}}"""),
            "/api/v1/videos/job" when Interlocked.Increment(ref polls) == 1 => FixtureHost.Json("""{"status":"pending","usage":{"cost":12}}"""),
            "/api/v1/videos/job" => FixtureHost.Json("""{"status":"completed","usage":{"cost":0.75}}"""),
            "/api/v1/videos/job/content?index=0" => Binary("video/mp4"),
            _ => throw new InvalidOperationException(request.Path)
        }, services => services.AddStreaikReferences().AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider()), media: true);
        using var start = await host.PostAsync("/api/videos", """{"model":"openrouter/vendor/video","prompt":"move"}""", RouterKey);
        var operation = await Read(start);
        Assert.Null(Cost(operation));
        var path = "/api/videos/" + (string?)operation["operation"];
        using var pending = await host.GetAsync(path, RouterKey);
        Assert.Null(Cost(await Read(pending)));
        using var completed = await host.GetAsync(path, RouterKey);
        Assert.Equal(0.75m, Cost(await Read(completed)));
    }

    private static decimal? Cost(JsonNode result)
    {
        Assert.Null(result["gateway"]);
        Assert.Null(result["cost"]);
        return result["providerMetadata"]?["gateway"]?["cost"]?.GetValue<decimal>();
    }

    private static async Task<JsonNode> Read(HttpResponseMessage response) => JsonNode.Parse(await GatewayTests.Success(response))!;

    private static HttpResponseMessage Binary(string mime)
        => new(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) { Headers = { ContentType = new(mime) } } };
}
