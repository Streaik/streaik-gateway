using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Streaik.Providers;
using Streaik.Providers.Anthropic;
using Streaik.Providers.BlackForestLabs;
using Streaik.Providers.CanopyWave;
using Streaik.Providers.Daglo;
using Streaik.Providers.Deepgram;
using Streaik.Providers.Fireworks;
using Streaik.Providers.Gladia;
using Streaik.Providers.ImageRouter;
using Streaik.Providers.Jina;
using Streaik.Providers.OpenAI;
using Streaik.Providers.OpenRouter;
using Streaik.Providers.VoyageAI;
using Streaik.Providers.Sluis;
using Streaik.Providers.ZeroEntropy;
using Streaik.Providers.Zyphra;

namespace Streaik.Gateway.Tests;

internal sealed class FixtureHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private FixtureHost(WebApplication app, ConcurrentQueue<UpstreamRequest> requests)
    {
        _app = app;
        Client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        Requests = requests;
    }

    public HttpClient Client { get; }
    public ConcurrentQueue<UpstreamRequest> Requests { get; }

    public static async Task<FixtureHost> StartAsync(Func<UpstreamRequest, HttpResponseMessage>? upstream = null,
        Action<IServiceCollection>? configure = null, bool media = false,
        Action<WebApplicationBuilder>? configureHost = null, string? authorization = null, bool compatibleChat = false, bool liveProviders = false,
        Action<WebApplication>? configureApp = null, bool decisions = false, bool gladia = false, bool portedProviders = false,
        bool sluisZyphraZeroEntropy = false, bool moonshotJinaBlackForestLabs = false, bool chutesPineconeRecraft = false,
        bool arliAICartesiaCeleris = false, bool basetenCohereBria = false)
    {
        var requests = new ConcurrentQueue<UpstreamRequest>();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Services.AddOpenAI().AddAnthropic();
        if (media) builder.Services.AddImageRouter().AddDeepgram();
        if (media || decisions) builder.Services.AddOpenRouter();
        if (decisions) builder.Services.AddFireworks();
        if (compatibleChat) builder.Services.AddDeepSeek().AddGroq().AddTogether().AddCerebras();
        if (liveProviders) builder.Services.AddGoogle().AddXai().AddAssemblyAI();
        if (gladia) builder.Services.AddGladia();
        if (portedProviders) builder.Services.AddCanopyWave().AddDaglo().AddVoyageAI();
        if (sluisZyphraZeroEntropy) builder.Services.AddSluis().AddZyphra().AddZeroEntropy();
        if (moonshotJinaBlackForestLabs) builder.Services.AddMoonshot().AddJina().AddBlackForestLabs();
        if (chutesPineconeRecraft) builder.Services.AddChutes().AddPinecone().AddRecraft();
        if (arliAICartesiaCeleris) builder.Services.AddArliAI().AddCartesia().AddCeleris();
        if (basetenCohereBria) builder.Services.AddBaseten().AddCohere().AddBria();
        if (authorization is null) builder.Services.AddStreaikHeaderAuth();
        var ids = new List<string> { "openai", "anthropic" };
        if (media) ids.AddRange(["deepgram", "imagerouter"]);
        if (media || decisions) ids.Add("openrouter");
        if (decisions) ids.Add("fireworks");
        if (compatibleChat) ids.AddRange(["deepseek", "groq", "together", "cerebras"]);
        if (liveProviders) ids.AddRange(["google", "xai", "assemblyai"]);
        if (gladia) ids.Add("gladia");
        if (portedProviders) ids.AddRange(["canopywave", "daglo", "voyageai"]);
        if (sluisZyphraZeroEntropy) ids.AddRange(["sluis", "zyphra", "zeroentropy"]);
        if (moonshotJinaBlackForestLabs) ids.AddRange(["moonshot", "jina", "jina-search", "blackforestlabs", "blackforestlabs-assets"]);
        if (chutesPineconeRecraft) ids.AddRange(["chutes", "pinecone", "recraft", "recraft-assets"]);
        if (arliAICartesiaCeleris) ids.AddRange(["arliai", "cartesia", "celeris"]);
        if (basetenCohereBria) ids.AddRange(["baseten", "cohere", "bria", "bria-delivery"]);
        foreach (var id in ids)
            builder.Services.AddHttpClient(id).ConfigurePrimaryHttpMessageHandler(
                () => new Handler(requests, upstream ?? Reply));
        configure?.Invoke(builder.Services);
        configureHost?.Invoke(builder);
        var app = builder.Build();
        if (authorization is not null)
        {
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapGroup("").RequireAuthorization(authorization).MapStreaikGateway();
        }
        else app.MapStreaikGateway();
        configureApp?.Invoke(app);
        await app.StartAsync();
        return new FixtureHost(app, requests);
    }

    public Task<HttpResponseMessage> PostAsync(string path, string body, params (string Name, string Value)[] headers)
        => SendAsync(path, body, headers);

    public Task<HttpResponseMessage> GetAsync(string path, params (string Name, string Value)[] headers)
        => SendAsync(path, null, headers);

    public async Task<HttpResponseMessage> PostFormAsync(string path, MultipartFormDataContent content, params (string Name, string Value)[] headers)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
        foreach (var (name, value) in headers) request.Headers.TryAddWithoutValidation(name, value);
        return await Client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> SendAsync(string path, string? body, (string Name, string Value)[] headers)
    {
        using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, path);
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        foreach (var (name, value) in headers) request.Headers.TryAddWithoutValidation(name, value);
        return await Client.SendAsync(request);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.DisposeAsync();
    }

    internal sealed record UpstreamRequest(string Host, string Path, JsonNode? Body, IReadOnlyDictionary<string, string> Headers)
    {
        public bool Stream => Headers.GetValueOrDefault("Accept") == "text/event-stream";
        public string? RawBody { get; init; }
        public string Method { get; init; } = "GET";
    }

    private sealed class Handler(ConcurrentQueue<UpstreamRequest> requests, Func<UpstreamRequest, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            var record = new UpstreamRequest(request.RequestUri!.Host, request.RequestUri.PathAndQuery,
                request.Content?.Headers.ContentType?.MediaType == "application/json" ? JsonNode.Parse(body!) : null,
                request.Headers.ToDictionary(pair => pair.Key, pair => string.Join(",", pair.Value), StringComparer.OrdinalIgnoreCase)) { RawBody = body, Method = request.Method.Method };
            requests.Enqueue(record);
            return reply(record);
        }
    }

    public static HttpResponseMessage Json(string payload, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(payload, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Sse(params string[] data) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(string.Concat(data.Select(value => $"data: {value}\n\n")), Encoding.UTF8, "text/event-stream")
    };

    private const string ResponsesText = """
        {"id":"resp_1","object":"response","model":"upstream-revision","status":"completed","created_at":1700000000,"usage":{"input_tokens":8,"output_tokens":3,"total_tokens":11},"output":[{"type":"message","id":"msg_r","role":"assistant","status":"completed","phase":"final_answer","content":[{"type":"output_text","text":"hello","annotations":[],"future_content":4}]}],"future_response":true}
        """;
    private const string MessagesText = """
        {"type":"message","id":"msg_1","model":"upstream-revision","role":"assistant","content":[{"type":"text","text":"hello","future_content":4}],"stop_reason":"end_turn","usage":{"input_tokens":6,"cache_read_input_tokens":2,"output_tokens":3},"future_response":true}
        """;

    public static HttpResponseMessage Reply(UpstreamRequest request) => request.Path switch
    {
        "/v1/models" => Json("""{"data":[{"id":"vendor/Model","owned_by":"fixture"}]}"""),
        "/v1/models?limit=100" => Json("""{"data":[{"id":"vendor/Model"}],"has_more":true,"last_id":"with space"}"""),
        "/v1/models?limit=100&after_id=with%20space" => Json("""{"data":[{"id":"second"}],"has_more":false}"""),
        "/v1/chat/completions" when request.Stream => Sse("""
            {"id":"chat_1","object":"chat.completion.chunk","created":1,"model":"upstream-revision","choices":[{"index":0,"delta":{"content":"hello"},"finish_reason":"stop"}],"future_response":true}
            """, "[DONE]"),
        "/v1/chat/completions" => Json("""
            {"id":"chat_1","object":"chat.completion","created":1,"model":"upstream-revision","choices":[{"index":0,"message":{"role":"assistant","content":"hello"},"finish_reason":"stop"}],"future_response":true}
            """),
        "/v1/messages" when request.Stream => Sse(MessageStart,
            """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":"","future_content":4}}""",
            """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"hello"}}""",
            """{"type":"content_block_stop","index":0}""", MessageDelta, MessageStop),
        "/v1/messages" => Json(MessagesText),
        "/v1/responses" when request.Stream => Sse(
            $$"""{"type":"response.created","sequence_number":0,"response":{{ResponsesText}}}""",
            """{"type":"response.output_text.delta","sequence_number":1,"item_id":"msg_r","content_index":0,"output_index":0,"delta":"hello","future_response":true}""",
            """{"type":"response.output_text.done","sequence_number":2,"item_id":"msg_r","content_index":0,"output_index":0,"text":"hello"}""",
            $$"""{"type":"response.completed","sequence_number":3,"response":{{ResponsesText}}}"""),
        "/v1/responses" => Json(ResponsesText),
        _ => throw new InvalidOperationException($"Unexpected fixture request: {request.Path}")
    };

    internal const string MessageStart = """{"type":"message_start","message":{"type":"message","id":"msg_1","model":"upstream-revision","role":"assistant","content":[],"usage":{"input_tokens":6,"cache_read_input_tokens":2,"output_tokens":0}}}""";
    internal const string MessageDelta = """{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":3}}""";
    internal const string MessageStop = """{"type":"message_stop"}""";
}
