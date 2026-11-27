using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Streaik.Providers;

namespace Streaik.Gateway;

public static class StreaikReferences
{
    // Configure a shared key ring and application name in a multi-instance host.
    public static IServiceCollection AddStreaikReferences(this IServiceCollection services)
    {
        services.AddDataProtection().SetApplicationName("Streaik.Gateway");
        services.AddSingleton(new ReferencesEnabled());
        return services;
    }
}

internal sealed class ReferencesEnabled;

internal sealed class GatewayReferences(GatewayCall call, string purpose)
{
    private readonly IDataProtector protector = (call.Context.RequestServices.GetService<IDataProtectionProvider>()
        ?? throw new ContractException("This gateway has not enabled protected references.", 501))
        .CreateProtector("Streaik.Gateway.References.v1", purpose, call.ProviderId, CallerBinding(call));

    public string Protect<T>(T payload, bool historical = false) => historical
        ? protector.Protect(JsonSerializer.Serialize(payload))
        : protector.ToTimeLimitedDataProtector().Protect(JsonSerializer.Serialize(payload), TimeSpan.FromHours(1));

    public T Read<T>(string token, bool historical = false) where T : class
    {
        if (token.Length is 0 or > 8192) throw new ContractException("Invalid or expired reference.", 404);
        try { return JsonSerializer.Deserialize<T>(historical ? protector.Unprotect(token) : protector.ToTimeLimitedDataProtector().Unprotect(token)) ?? throw new JsonException(); }
        catch (Exception e) when (e is CryptographicException or FormatException or JsonException)
        { throw new ContractException("Invalid or expired reference.", 404); }
    }

    private static string CallerBinding(GatewayCall call)
    {
        var resolver = call.Context.RequestServices.GetService<IProviderCredentials>();
        var local = call.Get<ILocalFixtureProvider>() is not null;
        var binding = "local-fixture";
        if (!local)
        {
            var key = resolver?.GetApiKey(call.ProviderId);
            if (string.IsNullOrWhiteSpace(key)) throw new ProviderCredentialException(call.ProviderId);
            binding = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        }
        var user = call.Context.User;
        if (user.Identity?.IsAuthenticated == true)
        {
            var subject = user.FindFirstValue("oid") ?? user.FindFirstValue("sub") ?? user.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(subject)) throw new ContractException("The caller has no stable identity.", 403);
            return JsonSerializer.Serialize(new[] { "authenticated", user.FindFirstValue("tid") ?? "", user.FindFirstValue("iss") ?? "", subject, binding });
        }
        if (local) return binding;
        if (resolver is not IHeaderProviderCredentials)
            throw new ContractException("Protected references require authenticated callers or BYOK.", 403);
        return "byok:" + binding;
    }
}
