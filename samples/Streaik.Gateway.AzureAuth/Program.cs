using System.Security.Claims;
using System.Text.Json;
using Streaik.Gateway;
using Streaik.Gateway.AzureAuth;
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
using Streaik.Vercel.Security;

var builder = WebApplication.CreateBuilder(args);
EntraGatewayAuth.Add(builder.Services, builder.Configuration.GetSection("AzureAd"));
if (builder.Configuration["Gateway:ApprovalSigningKey"] is { } secret && !string.IsNullOrWhiteSpace(secret))
{
    builder.Services.AddHttpContextAccessor();
    builder.Services.AddScoped<ToolApprovalSigner>(services =>
    {
        var user = services.GetRequiredService<IHttpContextAccessor>().HttpContext?.User;
        var subject = user?.FindFirst(ClaimTypes.NameIdentifier) ?? user?.FindFirst("sub");
        // A missing authenticated subject must never fall back to a host-wide approval.
        return user?.Identity?.IsAuthenticated == true && subject is not null && !string.IsNullOrWhiteSpace(subject.Value)
            ? new ToolApprovalSigner(secret, JsonSerializer.Serialize(new[] { subject.Issuer, subject.Value })) : null!;
    });
}
builder.Services.AddOpenAI().AddAnthropic().AddImageRouter().AddDeepgram().AddGladia().AddOpenRouter().AddFireworks()
    .AddDeepSeek().AddGroq().AddTogether().AddCerebras().AddGoogle().AddXai().AddAssemblyAI()
    .AddCanopyWave().AddDaglo().AddVoyageAI().AddSluis().AddZyphra().AddZeroEntropy()
    .AddMoonshot().AddJina().AddBlackForestLabs().AddChutes().AddPinecone().AddRecraft()
    .AddArliAI().AddCartesia().AddCeleris().AddBaseten().AddCohere().AddBria()
    .AddCencori().AddDecart().AddElevenLabs()
    .AddNebius().AddNomic().AddIdeogram()
    .AddHuggingFace().AddMixedbread().AddSegmind().AddStreaikReferences();
await VaultProviderKeys.LoadAsync(builder);

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapGroup("").RequireAuthorization(EntraGatewayAuth.Access).MapStreaikGateway();
await app.RunAsync();
