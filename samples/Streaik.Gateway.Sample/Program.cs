using Scalar.AspNetCore;
using Streaik.Gateway;
using Streaik.Gateway.Sample;
using Streaik.Providers;
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
using Streaik.Vercel.Security;

var builder = WebApplication.CreateBuilder(args);


builder.WebHost.ConfigureKestrel(o =>
{
    o.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(230);
    o.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(230);
    o.Limits.MaxRequestBodySize = null;
});

if (builder.Environment.IsDevelopment())
    builder.WebHost.ConfigureKestrel(options =>
        options.ListenLocalhost(5079));

builder.Services.AddOpenAI().AddAnthropic().AddImageRouter().AddDeepgram().AddGladia().AddOpenRouter().AddFireworks()
    .AddDeepSeek().AddGroq().AddTogether().AddCerebras().AddGoogle().AddXai().AddAssemblyAI()
    .AddCanopyWave().AddDaglo().AddVoyageAI().AddSluis().AddZyphra().AddZeroEntropy()
    .AddMoonshot().AddJina().AddBlackForestLabs().AddChutes().AddPinecone().AddRecraft()
    .AddArliAI().AddCartesia().AddCeleris()
    .AddBaseten().AddCohere().AddBria()
    .AddCencori().AddDecart().AddElevenLabs()
    .AddNebius().AddNomic().AddIdeogram()
    .AddTetrate().AddUpstage().AddTypecast()
    .AddHuggingFace().AddMixedbread().AddSegmind()
    .AddAkashML().AddArceeAI().AddAsyncAI()
    .AddNscale().AddDigitalOcean().AddSpeechify()
    .AddCloudRift().AddCoralBricks().AddAudixa()
    .AddCrusoe().AddAtlasCloud().AddDeepdub()
    .AddDoubleword().AddBananaPeel().AddCreora()
    .AddAkumi().AddClarifai().AddBespokeLabs()
    .AddArkRoute().AddBergetAI().AddAstica()
    .AddBastionGPT().AddTensorBlock().AddUnrealSpeech()
    .AddStreaikHeaderAuth().AddStreaikReferences();
if (builder.Configuration["Gateway:ApprovalSigningKey"] is { } secret && !string.IsNullOrWhiteSpace(secret))
    builder.Services.AddSingleton(new ToolApprovalSigner(secret));

builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
           .AllowAnyHeader()
           .AllowAnyOrigin()
           .AllowAnyMethod()
            .WithExposedHeaders("WWW-Authenticate")));

builder.Services.AddOpenApi(GatewayOpenApi.Configure);
builder.Services.AddOpenApi(GatewayCapabilityOpenApi.DocumentName, GatewayCapabilityOpenApi.Configure);

var app = builder.Build();
app.UseCors();
app.MapStreaikGateway();
app.MapOpenApi();
app.MapScalarApiReference(options => options
    .AddDocument("v1", "By API family", isDefault: true)
    .AddDocument(GatewayCapabilityOpenApi.DocumentName, "By capability"));
app.Run();
