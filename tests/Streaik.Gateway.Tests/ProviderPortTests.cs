using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace Streaik.Gateway.Tests;

public sealed class ProviderPortTests
{
    private const string DagloTranscripts = "/stt/v1/async/transcripts";
    private static readonly (string, string) DagloKey = ("x-daglo-key", "daglo-key");
    private static readonly (string, string) VoyageKey = ("x-voyageai-key", "voyage-key");

    [Fact]
    public async Task Models_preserve_native_ids_capabilities_and_canopywave_pricing()
    {
        await using var host = await FixtureHost.StartAsync(request =>
        {
            Assert.Equal("inference.canopywave.io", request.Host);
            Assert.Equal("/v1/models", request.Path);
            Assert.Equal("Bearer canopy-key", request.Headers["Authorization"]);
            return FixtureHost.Json("""
                {"data":[{"id":"org/Model","display_name":"Canopy Model","description":"Fixture description","context_size":32000,
                "max_output_tokens":4000,"owned_by":"org","input_token_price_per_m":"0.75","output_token_price_per_m":2}]}
                """);
        }, portedProviders: true);
        using var response = await host.GetAsync("/v1/models", ("x-canopywave-key", "canopy-key"), DagloKey, VoyageKey);
        var models = JsonNode.Parse(await GatewayTests.Success(response))!["data"]!.AsArray();
        var canopy = Assert.Single(models, model => (string?)model?["id"] == "canopywave/org/Model")!;
        Assert.Equal("Canopy Model", (string?)canopy["name"]);
        Assert.Equal("Fixture description", (string?)canopy["description"]);
        Assert.Equal(32000, (int?)canopy["context_window"]);
        Assert.Equal(4000, (int?)canopy["max_tokens"]);
        Assert.Equal(0.00000075m, decimal.Parse(canopy["pricing"]!["input"]!.ToString(), System.Globalization.CultureInfo.InvariantCulture));
        HasModel("daglo/elli", "language");
        HasModel("daglo/tts", "speech");
        HasModel("daglo/stt", "transcription");
        HasModel("voyageai/voyage-context-3", "embedding");
        HasModel("voyageai/rerank-2.5", "reranking");
        Assert.Single(host.Requests);

        void HasModel(string id, string type) => Assert.Contains(models, model => (string?)model?["id"] == id && (string?)model?["type"] == type);
    }

    [Theory]
    [InlineData("/v1/chat/completions", false)]
    [InlineData("/v1/chat/completions", true)]
    [InlineData("/v1/responses", false)]
    [InlineData("/v1/messages", true)]
    [InlineData("/api/chat", true)]
    public async Task Canopywave_chat_reuses_existing_protocol_adapters(string path, bool stream)
    {
        await using var host = await FixtureHost.StartAsync(request =>
        {
            Assert.Equal("inference.canopywave.io", request.Host);
            Assert.Equal("/v1/chat/completions", request.Path);
            Assert.Equal("Bearer canopy-key", request.Headers["Authorization"]);
            Assert.Equal("org/Model", (string?)request.Body!["model"]);
            Assert.Equal(stream, request.Stream);
            return FixtureHost.Reply(request);
        }, portedProviders: true);
        var body = path switch
        {
            "/v1/responses" => """{"model":"canopywave/org/Model","input":"hello"}""",
            "/v1/messages" => """{"model":"canopywave/org/Model","messages":[{"role":"user","content":"hello"}],"max_tokens":32,"stream":true}""",
            "/api/chat" => """{"model":"canopywave/org/Model","id":"chat","messages":[{"id":"user","role":"user","parts":[{"type":"text","text":"hello"}]}]}""",
            _ => $$"""{"model":"canopywave/org/Model","messages":[{"role":"user","content":"hello"}],"stream":{{(stream ? "true" : "false")}}}"""
        };
        using var response = await host.PostAsync(path, body, ("x-canopywave-key", "canopy-key"));
        var output = await GatewayTests.Success(response);
        Assert.Contains("hello", output);
        if (path == "/v1/responses") Assert.Contains("output_text", output);
        if (path == "/v1/messages") Assert.Contains("message_stop", output);
        if (path == "/api/chat") Assert.Contains("text-delta", output);
        Assert.Single(host.Requests);
    }

    [Fact]
    public async Task Daglo_flattens_text_chat_and_returns_provider_audio()
    {
        await using var host = await FixtureHost.StartAsync(request =>
        {
            Assert.Equal("apis.daglo.ai", request.Host);
            Assert.Equal("Bearer daglo-key", request.Headers["Authorization"]);
            if (request.Path == "/nlp/v1/sync/chat/completions")
            {
                Assert.Equal("elli", (string?)request.Body!["model"]);
                Assert.Equal("hello world", (string?)request.Body["messages"]![0]!["content"]);
                return FixtureHost.Reply(request with { Path = "/v1/chat/completions" });
            }
            Assert.Equal("/tts/v1/sync/audios", request.Path);
            Assert.Equal("hello", (string?)request.Body!["text"]);
            Assert.Single(request.Body!.AsObject());
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
        }, portedProviders: true);
        using var chat = await host.PostAsync("/v1/chat/completions", """
            {"model":"daglo/elli","messages":[{"role":"user","content":[{"type":"text","text":"hello "},{"type":"text","text":"world"}]}],"stream":true}
            """, DagloKey);
        Assert.Contains("[DONE]", await GatewayTests.Success(chat));
        using var speech = await host.PostAsync("/api/speech", """{"model":"daglo/tts","text":"hello"}""", DagloKey);
        var audio = JsonNode.Parse(await GatewayTests.Success(speech))!["audio"]!;
        Assert.Equal("audio/wav", (string?)audio["mediaType"]);
        Assert.Equal("AQID", (string?)audio["base64"]);
    }

    [Theory]
    [InlineData("json", false)]
    [InlineData("verbose_json", false)]
    [InlineData("text", false)]
    [InlineData("json", true)]
    public async Task Daglo_transcription_polls_and_preserves_openai_formats(string format, bool stream)
    {
        var polls = 0;
        await using var host = await FixtureHost.StartAsync(request =>
        {
            Assert.Equal("Bearer daglo-key", request.Headers["Authorization"]);
            if (request.Method == "POST")
            {
                Assert.Equal(DagloTranscripts, request.Path);
                Assert.Contains("name=sttConfig", request.RawBody);
                Assert.Contains("\"model\":\"stt\"", request.RawBody);
                Assert.Contains("\"language\":\"ko\"", request.RawBody);
                return FixtureHost.Json("""{"rid":"job with space"}""");
            }
            Assert.Equal(DagloTranscripts + "/job%20with%20space", request.Path);
            if (++polls == 1 && format == "verbose_json") return FixtureHost.Json("""{"status":"transcribing"}""");
            return FixtureHost.Json("""
                {"status":"transcribed","sttResults":[{"transcript":"hello","words":[
                {"word":"hello","startTime":{"seconds":"0"},"endTime":{"seconds":"1","nanos":250000000}}]}]}
                """);
        }, portedProviders: true);
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent([1, 2, 3]) { Headers = { ContentType = new MediaTypeHeaderValue("audio/wav") } }, "file", "audio.wav");
        form.Add(new StringContent("daglo/stt"), "model");
        form.Add(new StringContent("ko"), "language");
        form.Add(new StringContent(format), "response_format");
        if (stream) form.Add(new StringContent("true"), "stream");
        using var response = await host.PostFormAsync("/v1/audio/transcriptions", form, DagloKey);
        var output = await GatewayTests.Success(response);
        if (stream)
        {
            Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
            Assert.Contains("transcript.text.delta", output);
            Assert.Contains("transcript.text.done", output);
        }
        else if (format == "text") Assert.Equal("hello", output);
        else
        {
            var result = JsonNode.Parse(output)!;
            Assert.Equal("hello", (string?)result["text"]);
            if (format == "verbose_json")
            {
                Assert.Equal(2, polls);
                Assert.Equal(1.25, (double?)result["segments"]![0]!["end"]);
            }
        }
    }

    [Fact]
    public async Task Daglo_vercel_transcription_uses_nested_options_and_reports_terminal_failures()
    {
        var polls = 0;
        await using var host = await FixtureHost.StartAsync(request =>
        {
            if (request.Method == "POST")
            {
                Assert.Equal(DagloTranscripts, request.Path);
                Assert.Contains("\"language\":\"en\"", request.RawBody);
                return FixtureHost.Json("""{"rid":"job"}""");
            }
            Assert.Equal(DagloTranscripts + "/job", request.Path);
            if (++polls == 1) return FixtureHost.Json("""{"status":"transcribed","sttResults":[{"transcript":"hello"}]}""");
            return FixtureHost.Json("""{"status":"input_error"}""");
        }, portedProviders: true);
        const string body = """
            {"model":"daglo/stt","mediaType":"audio/wav","audio":"AQID","providerOptions":{"daglo":{"sttConfig":{"language":"en"}}}}
            """;
        using var response = await host.PostAsync("/api/transcriptions", body, DagloKey);
        var result = JsonNode.Parse(await GatewayTests.Success(response))!;
        Assert.Equal("hello", (string?)result["text"]);
        Assert.Equal("en", (string?)result["language"]);
        using var failed = await host.PostAsync("/api/transcriptions", body, DagloKey);
        Assert.Equal(422, (int)failed.StatusCode);
        Assert.Equal(2, polls);
    }

    [Theory]
    [InlineData("voyage-4", "/v1/embeddings", "[\"x\"]")]
    [InlineData("voyage-context-3", "/v1/contextualizedembeddings", "[[\"x\",\"y\"]]")]
    [InlineData("voyage-multimodal-3.5", "/v1/multimodalembeddings", """[{"content":[{"type":"image_url","image_url":{"url":"data:image/png;base64,AQID"}},{"type":"input_video","input_video":{"url":"https://example.com/video"}}]}]""")]
    public async Task Voyage_embeddings_map_options_inputs_vectors_and_usage(string model, string endpoint, string input)
    {
        var multimodal = model.StartsWith("voyage-multimodal", StringComparison.Ordinal);
        var contextual = model.StartsWith("voyage-context", StringComparison.Ordinal);
        await using var host = await FixtureHost.StartAsync(request =>
        {
            Assert.Equal("api.voyageai.com", request.Host);
            Assert.Equal(endpoint, request.Path);
            Assert.Equal("Bearer voyage-key", request.Headers["Authorization"]);
            var fields = request.Body!;
            Assert.Equal(model, (string?)fields["model"]);
            Assert.Equal(256, (int?)fields["output_dimension"]);
            Assert.Equal("query", (string?)fields["input_type"]);
            if (multimodal)
            {
                Assert.Equal("base64", (string?)fields["output_encoding"]);
                Assert.Equal("image_base64", (string?)fields["inputs"]![0]!["content"]![0]!["type"]);
                Assert.Equal("https://example.com/video", (string?)fields["inputs"]![0]!["content"]![1]!["video_url"]);
                return FixtureHost.Json("""{"data":[{"embedding":"AQID"}],"usage":{"total_tokens":7}}""");
            }
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(input), fields[contextual ? "inputs" : "input"]));
            return FixtureHost.Json(contextual
                ? """{"data":[{"data":[{"embedding":[1]},{"embedding":[2]}]}],"usage":{"total_tokens":7}}"""
                : """{"data":[{"embedding":[1]}],"usage":{"total_tokens":7}}""");
        }, portedProviders: true);
        using var response = await host.PostAsync("/v1/embeddings", $$"""
            {"model":"voyageai/{{model}}","input":{{input}},"dimensions":256,"input_type":"query","encoding_format":"{{(multimodal ? "base64" : "float")}}"}
            """, VoyageKey);
        var result = JsonNode.Parse(await GatewayTests.Success(response))!;
        Assert.Equal("voyageai/" + model, (string?)result["model"]);
        Assert.Equal(7, (int?)result["usage"]!["total_tokens"]);
        Assert.Equal(contextual ? 2 : 1, result["data"]!.AsArray().Count);
        if (contextual) Assert.Equal(1, (int?)result["data"]![1]!["index"]);
        if (multimodal) Assert.Equal("AQID", (string?)result["data"]![0]!["embedding"]);
    }

    [Fact]
    public async Task Voyage_vercel_embeddings_keep_vectors_and_token_count()
    {
        await using var host = await FixtureHost.StartAsync(request =>
        {
            Assert.Equal("/v1/contextualizedembeddings", request.Path);
            Assert.Equal("document", (string?)request.Body!["input_type"]);
            Assert.Equal("ch1", (string?)request.Body["inputs"]![0]![0]);
            Assert.Equal("ch2", (string?)request.Body["inputs"]![0]![1]);
            return FixtureHost.Json("""{"data":[{"data":[{"embedding":[1]},{"embedding":[2]}]}],"usage":{"total_tokens":7}}""");
        }, portedProviders: true);
        using var response = await host.PostAsync("/api/embeddings", """
            {"model":"voyageai/voyage-context-3","values":["ch1","ch2"],"providerOptions":{"voyageai":{"input_type":"document","inputs":[["ch1","ch2"]]}}}
            """, VoyageKey);
        var result = JsonNode.Parse(await GatewayTests.Success(response))!;
        Assert.Equal(2, (int?)result["embeddings"]![1]![0]);
        Assert.Equal(7, (int?)result["usage"]!["tokens"]);
    }

    [Fact]
    public async Task Voyage_reranks_with_provider_options_and_preserves_ranking()
    {
        await using var host = await FixtureHost.StartAsync(request =>
        {
            Assert.Equal("/v1/rerank", request.Path);
            Assert.Equal("Bearer voyage-key", request.Headers["Authorization"]);
            var fields = request.Body!;
            Assert.Equal("rerank-2.5", (string?)fields["model"]);
            Assert.Equal("needle", (string?)fields["query"]);
            Assert.Equal(1, (int?)fields["top_k"]);
            Assert.Equal(false, (bool?)fields["truncation"]);
            Assert.Equal("needle", (string?)fields["documents"]![1]);
            return FixtureHost.Json("""{"id":"rank_1","data":[{"index":1,"relevance_score":0.9}]}""");
        }, portedProviders: true);
        using var response = await host.PostAsync("/api/rerank", """
            {"model":"voyageai/rerank-2.5","query":"needle","documents":{"type":"text","values":["hay","needle"]},
            "topN":1,"providerOptions":{"voyageai":{"truncation":false,"query":"not forwarded"}}}
            """, VoyageKey);
        var result = JsonNode.Parse(await GatewayTests.Success(response))!;
        Assert.Equal(1, (int?)result["ranking"]![0]!["index"]);
        Assert.Equal(0.9, (double?)result["ranking"]![0]!["relevanceScore"]);
        Assert.Equal("rank_1", (string?)result["response"]!["id"]);
    }
}
