using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Identity.Web;
using Streaik.Gateway.AzureAuth;
using Streaik.ChatCompletions.Models;
using Streaik.Providers;

namespace Streaik.Gateway.Tests;

public sealed class PricingAndAuthTests
{
    [Theory]
    [InlineData("/v1/responses", false)]
    [InlineData("/v1/responses", true)]
    [InlineData("/v1/messages", false)]
    [InlineData("/v1/messages", true)]
    [InlineData("/v1/chat/completions", false)]
    [InlineData("/v1/chat/completions", true)]
    [InlineData("/api/chat", true)]
    public async Task Anthropic_cache_rates_survive_every_gateway_protocol(string route, bool stream)
    {
        await using var host = await FixtureHost.StartAsync();
        var request = GatewayTests.Request(route, "anthropic/claude-mythos-5-1", stream);
        if (route == "/v1/chat/completions" && stream)
            request["stream_options"] = new JsonObject { ["include_usage"] = true };
        using var result = await host.PostAsync(route, request.ToJsonString(), ("X-Anthropic-Key", "fixture"), GatewayTests.AnthropicVersion);
        var body = await GatewayTests.Success(result);
        var envelopes = stream ? Frames(body) : [JsonNode.Parse(body)!];
        var estimate = Assert.Single(envelopes.Select(e => (e["response"] ?? e)["metadata"]?["gateway"] ??
            e["messageMetadata"]?["providerMetadata"]?["gateway"]).OfType<JsonNode>(), gateway => gateway["cost"] is not null);
        Assert.Equal(0.0002105m, estimate["cost"]!.GetValue<decimal>()); // six uncached, two cache reads, three output
    }

    [Fact]
    public async Task Provider_cost_and_metadata_are_not_overwritten_by_gateway_estimate()
    {
        await using var host = await FixtureHost.StartAsync(_ => FixtureHost.Json("""
            {"id":"chat_1","model":"gpt-5.2-2025-12-11","choices":[],"metadata":{"client_tag":"keep"},"usage":{"prompt_tokens":10,"completion_tokens":3,"total_tokens":13,"prompt_tokens_details":{"cached_tokens":2},"cost":0.123}}
            """));
        using var result = await host.PostAsync("/v1/chat/completions", GatewayTests.Request("/v1/chat/completions", "openai/gpt-5.2-2025-12-11", false).ToJsonString(), ("X-OpenAI-Key", "fixture"));
        var payload = await GatewayTests.Success(result);
        var body = JsonNode.Parse(payload)!;
        Assert.Equal(0.123m, body["usage"]!["cost"]!.GetValue<decimal>());
        Assert.Equal("keep", body["metadata"]!["client_tag"]!.GetValue<string>());
        Assert.Equal(0.00005635m, body["metadata"]!["gateway"]!["cost"]!.GetValue<decimal>());
        var typed = JsonSerializer.Deserialize<ChatCompletion>(payload)!;
        Assert.Equal("keep", typed.Metadata!["client_tag"]);
        Assert.Equal(0.00005635m, typed.MetadataExtensions!["gateway"].GetProperty("cost").GetDecimal());
    }

    [Fact]
    public async Task Unpriced_models_do_not_report_zero_cost()
    {
        await using var host = await FixtureHost.StartAsync();
        using var result = await host.PostAsync("/v1/responses", GatewayTests.Request("/v1/responses", "openai/unknown", false).ToJsonString(), ("X-OpenAI-Key", "fixture"));
        Assert.Null(JsonNode.Parse(await GatewayTests.Success(result))!["metadata"]?["gateway"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Entra_identity_is_checked_and_never_forwarded_as_provider_auth(bool legacyIssuer)
    {
        var tenant = Guid.NewGuid().ToString("D");
        using var rsa = RSA.Create(2048);
        var signing = new RsaSecurityKey(rsa) { KeyId = "fixture-signing-key" };
        var publicKey = new RsaSecurityKey(rsa.ExportParameters(false)) { KeyId = signing.KeyId };
        var jwks = JsonSerializer.Serialize(new JsonWebKeySet { Keys = { JsonWebKeyConverter.ConvertFromRSASecurityKey(publicKey) } });
        await using var host = await FixtureHost.StartAsync(configureHost: builder =>
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["AzureAd:TenantId"] = tenant,
                ["AzureAd:Audience"] = "streaik-fixture",
                ["Providers:openai:ApiKey"] = "host-owned-fixture-key" });
            EntraGatewayAuth.Add(builder.Services, builder.Configuration.GetSection("AzureAd"));
            // JwtBearer and Microsoft.Identity.Web's issuer validator each load discovery metadata.
            const string discoveryClient = "entra-fixture";
            builder.Services.AddHttpClient(discoveryClient)
                .ConfigurePrimaryHttpMessageHandler(() => new EntraDiscoveryHandler(tenant, jwks));
            builder.Services.Configure<AadIssuerValidatorOptions>(options => options.HttpClientName = discoveryClient);
            builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
                .Configure<IHttpClientFactory>((options, clients) => options.Backchannel = clients.CreateClient(discoveryClient));
        }, authorization: EntraGatewayAuth.Access);

        var request = GatewayTests.Request("/v1/responses", "openai/test", false).ToJsonString();
        async Task Check(string? token, HttpStatusCode expected)
        {
            var before = host.Requests.Count;
            using var result = token is null
                ? await host.PostAsync("/v1/responses", request, ("X-OpenAI-Key", "not-auth"))
                : await host.PostAsync("/v1/responses", request, ("Authorization", $"Bearer {token}"), ("X-OpenAI-Key", "decoy"));
            Assert.True(expected == result.StatusCode,
                $"Expected {expected}, got {result.StatusCode}. {string.Join(", ", result.Headers.WwwAuthenticate)}");
            Assert.Equal(before + (expected == HttpStatusCode.OK ? 1 : 0), host.Requests.Count);
        }
        // The gateway doesn't require a configured scope or role. Microsoft.Identity.Web still validates each token.
        await Check(Token(scope: "Fixture.Read"), HttpStatusCode.OK);
        await Check(Token(role: "Fixture.App"), HttpStatusCode.OK);
        await Check(null, HttpStatusCode.Unauthorized);
        await Check(Token(scope: "Fixture.Read", aud: "wrong"), HttpStatusCode.Unauthorized);
        await Check(Token(scope: "Fixture.Read", iss: "https://issuer.example"), HttpStatusCode.Unauthorized);
        await Check(Token(scope: "Fixture.Read", tid: Guid.NewGuid().ToString("D")), HttpStatusCode.Unauthorized);
        Assert.All(host.Requests, received =>
        {
            Assert.Equal("Bearer host-owned-fixture-key", received.Headers["Authorization"]);
            Assert.DoesNotContain("X-OpenAI-Key", received.Headers.Keys, StringComparer.OrdinalIgnoreCase);
        });

        string Token(string? scope = null, string? role = null, string aud = "streaik-fixture", string? iss = null, string? tid = null)
        {
            tid ??= tenant;
            var claims = new Dictionary<string, object> { ["tid"] = tid, ["oid"] = "fixture-user", ["ver"] = legacyIssuer ? "1.0" : "2.0" };
            if (scope is not null) claims["scp"] = scope;
            if (role is not null) claims["roles"] = new[] { role };
            return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor { Issuer = iss ?? EntraIssuer(tid, legacyIssuer), Audience = aud,
                Claims = claims, SigningCredentials = new(signing, SecurityAlgorithms.RsaSha256), Expires = DateTime.UtcNow.AddMinutes(5) });
        }
    }

    private static string EntraAuthority(string tenant) => $"https://login.microsoftonline.com/{tenant}";

    private static string EntraIssuer(string tenant, bool legacy)
        => legacy ? $"https://sts.windows.net/{tenant}/" : $"{EntraAuthority(tenant)}/v2.0";

    private sealed class EntraDiscoveryHandler(string tenant, string jwks) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var authority = EntraAuthority(tenant);
            var keys = $"{authority}/discovery/keys";
            HttpResponseMessage Discovery(bool legacy) => FixtureHost.Json(JsonSerializer.Serialize(new
                { issuer = EntraIssuer(tenant, legacy), jwks_uri = keys }));
            return Task.FromResult(request.RequestUri?.AbsoluteUri switch
            {
                var uri when uri == $"{authority}/.well-known/openid-configuration" => Discovery(legacy: true),
                var uri when uri == $"{authority}/v2.0/.well-known/openid-configuration" => Discovery(legacy: false),
                var uri when uri == keys => FixtureHost.Json(jwks),
                _ => FixtureHost.Json("{}", HttpStatusCode.NotFound)
            });
        }
    }

    private static JsonNode[] Frames(string sse) => sse.Split('\n').Where(line => line.StartsWith("data: {", StringComparison.Ordinal))
        .Select(line => JsonNode.Parse(line[6..])!).ToArray();

    [Fact]
    public void Existing_AIHappy_settings_are_a_fallback_not_an_override()
    {
        var settings = new ConfigurationManager();
        settings.AddInMemoryCollection(new Dictionary<string, string?> { ["AIServices:OpenAI:ApiKey"] = "legacy-openai",
            ["AIServices:Anthropic:ApiKey"] = "legacy-anthropic", ["Providers:anthropic:ApiKey"] = "current-anthropic",
            ["AIServices:Deepgram:ApiKey"] = "legacy-deepgram", ["DEEPGRAM_API_KEY"] = "env-deepgram" });
        VaultProviderKeys.AddLegacySettings(settings, [new("openai", []), new("anthropic", []), new("deepgram", [])]);
        Assert.Equal("legacy-openai", settings["Providers:openai:ApiKey"]);
        Assert.Equal("current-anthropic", settings["Providers:anthropic:ApiKey"]);
        Assert.Null(settings["Providers:deepgram:ApiKey"]);
    }

    [Fact]
    public void Gateway_and_providers_do_not_depend_on_unified_or_Azure_host_packages()
    {
        foreach (var assembly in new[] { typeof(StreaikGateway).Assembly, typeof(IProviderCredentials).Assembly })
            Assert.DoesNotContain(assembly.GetReferencedAssemblies(), name => name.Name!.Contains("Unified", StringComparison.OrdinalIgnoreCase)
                || name.Name.StartsWith("AIHappey", StringComparison.Ordinal) || name.Name.StartsWith("Azure.", StringComparison.Ordinal));
    }
}
