using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Streaik.Providers;

namespace Streaik.Gateway;

internal interface IHeaderProviderCredentials : IProviderCredentials;

public static class GatewayHeaderAuth
{
    // Opt-in: Authorization is an upstream credential here, not gateway authentication.
    public static IServiceCollection AddStreaikHeaderAuth(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.Replace(ServiceDescriptor.Scoped<IProviderCredentials, HeaderCredentials>());
        return services;
    }

    internal static readonly object ActiveProvider = new();

    private sealed class HeaderCredentials(IHttpContextAccessor accessor) : IHeaderProviderCredentials
    {
        public string? GetApiKey(string providerId)
        {
            var context = accessor.HttpContext;
            if (context is null) return null;
            if (context.Items.TryGetValue(ActiveProvider, out var active) && Equals(providerId, active)
                && AuthenticationHeaderValue.TryParse(context.Request.Headers.Authorization.ToString(), out var auth)
                && auth.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(auth.Parameter))
                return auth.Parameter;

            var values = context.Request.Headers[$"x-{providerId}-key"];
            return values.Count == 1 && !string.IsNullOrWhiteSpace(values[0]) ? values[0]!.Trim() : null;
        }
    }
}

internal static class GatewayHeaders
{
    private static readonly HashSet<string> Common = new(StringComparer.OrdinalIgnoreCase)
        { "HTTP-Referer", "OpenAI-Beta", "X-Title" };

    private static readonly HashSet<string> Blocked = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization", "Proxy-Authorization", "Cookie", "Set-Cookie", "Host", "X-Api-Key",
        "Connection", "Keep-Alive", "Proxy-Connection", "TE", "Trailer", "Transfer-Encoding", "Upgrade",
        "Content-Length", "Content-Type", "Accept", "Forwarded"
    };

    public static IReadOnlyDictionary<string, string> Select(IHeaderDictionary headers, ProviderRegistration registration)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var connection = new HashSet<string>(headers.Connection.ToString().Split(',', StringSplitOptions.TrimEntries), StringComparer.OrdinalIgnoreCase);
        var prefix = registration.Id + "-";
        foreach (var (name, values) in headers)
        {
            if (Blocked.Contains(name) || connection.Contains(name) || name.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("-key", StringComparison.OrdinalIgnoreCase) || name.EndsWith("-token", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("-secret", StringComparison.OrdinalIgnoreCase) || name.EndsWith("-authorization", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("-cookie", StringComparison.OrdinalIgnoreCase)) continue;
            var value = values.ToString();
            if (string.IsNullOrWhiteSpace(value)) continue;
            if (Common.Contains(name) || registration.AdditionalHeaders.Contains(name, StringComparer.OrdinalIgnoreCase)
                || (name.Length > prefix.Length && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                || (name.Length > prefix.Length + 2 && name.StartsWith("x-" + prefix, StringComparison.OrdinalIgnoreCase)))
                result[name] = value;
        }
        return result;
    }
}
