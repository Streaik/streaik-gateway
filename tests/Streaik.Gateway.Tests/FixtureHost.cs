using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Streaik.Providers;
using Streaik.Providers.Abliteration;
using Streaik.Providers.AIMagicx;
using Streaik.Providers.Speechmatics;
using Streaik.Providers.Anthropic;
using Streaik.Providers.AtlasCloud;
using Streaik.Providers.BlackForestLabs;
using Streaik.Providers.CanopyWave;
using Streaik.Providers.Daglo;
using Streaik.Providers.Deepdub;
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
        bool arliAICartesiaCeleris = false, bool basetenCohereBria = false, bool cencoriDecartElevenLabs = false,
        bool nebiusNomicIdeogram = false, bool huggingFaceMixedbreadSegmind = false, bool tetrateUpstageTypecast = false,
        bool akashArceeAsync = false, bool nscaleDigitalOceanSpeechify = false, bool cloudRiftCoralBricksAudixa = false,
        bool crusoeAtlasCloudDeepdub = false, bool doublewordBananaPeelCreora = false, bool akumiClarifaiBespokeLabs = false,
        bool arkRouteBergetAstica = false, bool bastionTensorUnreal = false, bool aichixiaEdgeSpeakIsaacus = false,
        bool abliterationAIMagicxSpeechmatics = false, bool dandoloBinericAstria = false, bool asiOneRimeStabilityAI = false,
        bool avianFishAudioLumaAI = false, bool augureCognitivessAIHorde = false, bool tikHubAIWellSaidYollomi = false,
        bool commonstackLeonardoNeuphonic = false, bool baseApiByteSpaceCambai = false, bool aiBramhaReveAddisAI = false)
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
        if (cencoriDecartElevenLabs) builder.Services.AddCencori().AddDecart().AddElevenLabs();
        if (nebiusNomicIdeogram) builder.Services.AddNebius().AddNomic().AddIdeogram();
        if (huggingFaceMixedbreadSegmind) builder.Services.AddHuggingFace().AddMixedbread().AddSegmind();
        if (tetrateUpstageTypecast) builder.Services.AddTetrate().AddUpstage().AddTypecast();
        if (akashArceeAsync) builder.Services.AddAkashML().AddArceeAI().AddAsyncAI();
        if (nscaleDigitalOceanSpeechify) builder.Services.AddNscale().AddDigitalOcean().AddSpeechify();
        if (cloudRiftCoralBricksAudixa) builder.Services.AddCloudRift().AddCoralBricks().AddAudixa();
        if (crusoeAtlasCloudDeepdub) builder.Services.AddCrusoe().AddAtlasCloud().AddDeepdub();
        if (doublewordBananaPeelCreora) builder.Services.AddDoubleword().AddBananaPeel().AddCreora();
        if (akumiClarifaiBespokeLabs) builder.Services.AddAkumi().AddClarifai().AddBespokeLabs();
        if (arkRouteBergetAstica) builder.Services.AddArkRoute().AddBergetAI().AddAstica();
        if (bastionTensorUnreal) builder.Services.AddBastionGPT().AddTensorBlock().AddUnrealSpeech();
        if (aichixiaEdgeSpeakIsaacus) builder.Services.AddAichixia().AddEdgeSpeak().AddIsaacus();
        if (abliterationAIMagicxSpeechmatics) builder.Services.AddAbliteration().AddAIMagicx().AddSpeechmatics();
        if (dandoloBinericAstria) builder.Services.AddDandolo().AddBineric().AddAstria();
        if (asiOneRimeStabilityAI) builder.Services.AddASIOne().AddRime().AddStabilityAI();
        if (avianFishAudioLumaAI) builder.Services.AddAvian().AddFishAudio().AddLumaAI();
        if (augureCognitivessAIHorde) builder.Services.AddAugure().AddCognitivessAI().AddAIHorde();
        if (tikHubAIWellSaidYollomi) builder.Services.AddTikHubAI().AddWellSaid().AddYollomi();
        if (commonstackLeonardoNeuphonic) builder.Services.AddCommonstack().AddLeonardo().AddNeuphonic();
        if (baseApiByteSpaceCambai) builder.Services.AddBaseAPI().AddByteSpace().AddCAMBAI();
        if (aiBramhaReveAddisAI) builder.Services.AddAIBramha().AddReve().AddAddisAI();
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
        if (cencoriDecartElevenLabs) ids.AddRange(["cencori", "decart", "elevenlabs"]);
        if (nebiusNomicIdeogram) ids.AddRange(["nebius", "nomic", "ideogram", "ideogram-assets"]);
        if (huggingFaceMixedbreadSegmind) ids.AddRange(["huggingface", "mixedbread", "segmind"]);
        if (tetrateUpstageTypecast) ids.AddRange(["tetrate", "upstage", "typecast"]);
        if (akashArceeAsync) ids.AddRange(["akashml", "akashml-assets", "arceeai", "async"]);
        if (nscaleDigitalOceanSpeechify) ids.AddRange(["nscale", "digitalocean", "speechify", "speechify-stream"]);
        if (cloudRiftCoralBricksAudixa) ids.AddRange(["cloudrift", "coralbricks", "audixa", "audixa-assets"]);
        if (crusoeAtlasCloudDeepdub) ids.AddRange(["crusoe", "atlascloud", "atlascloud-catalog", "atlascloud-assets", "deepdub", "deepdub-eu"]);
        if (doublewordBananaPeelCreora) ids.AddRange(["doubleword", "bananapeel", "creora"]);
        if (akumiClarifaiBespokeLabs) ids.AddRange(["akumi", "clarifai", "clarifai-catalog", "bespokelabs"]);
        if (arkRouteBergetAstica) ids.AddRange(["arkroute", "bergetai", "astica", "astica-design"]);
        if (bastionTensorUnreal) ids.AddRange(["bastiongpt", "tensorblock", "tensorblock-assets", "unrealspeech", "unrealspeech-assets"]);
        if (aichixiaEdgeSpeakIsaacus) ids.AddRange(["aichixia", "edgespeak", "isaacus"]);
        if (abliterationAIMagicxSpeechmatics) ids.AddRange(["abliteration", "aimagicx", "speechmatics"]);
        if (dandoloBinericAstria) ids.AddRange(["dandolo", "bineric", "astria", "astria-delivery"]);
        if (asiOneRimeStabilityAI) ids.AddRange(["asione", "rime", "stabilityai"]);
        if (avianFishAudioLumaAI) ids.AddRange(["avian", "fishaudio", "lumaai", "lumaai-delivery"]);
        if (augureCognitivessAIHorde) ids.AddRange(["augure", "cognitivessai", "aihorde", "aihorde-images", "aihorde-assets"]);
        if (tikHubAIWellSaidYollomi) ids.AddRange(["tikhubai", "wellsaid", "yollomi", "yollomi-assets"]);
        if (commonstackLeonardoNeuphonic) ids.AddRange(["commonstack", "leonardo", "leonardo-assets", "neuphonic"]);
        if (baseApiByteSpaceCambai) ids.AddRange(["baseapi", "bytespace", "cambai", "cambai-assets"]);
        if (aiBramhaReveAddisAI) ids.AddRange(["aibramha", "reve", "addisai"]);
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
