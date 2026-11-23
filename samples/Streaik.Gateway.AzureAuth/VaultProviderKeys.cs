using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Streaik.Providers;

namespace Streaik.Gateway.AzureAuth;

internal static class VaultProviderKeys
{
    // Overlay settings only for explicitly registered provider IDs; no secret enumeration is needed.
    public static async Task LoadAsync(WebApplicationBuilder builder)
    {
        var providers = builder.Services.Select(service => service.ImplementationInstance).OfType<ProviderRegistration>().ToArray();
        AddLegacySettings(builder.Configuration, providers);
        var section = builder.Configuration.GetSection("KeyVault");
        if (string.IsNullOrWhiteSpace(section["VaultUri"])) return;
        if (!Uri.TryCreate(section["VaultUri"], UriKind.Absolute, out var vault) || vault.Scheme != Uri.UriSchemeHttps
            || vault.AbsolutePath != "/" || !string.IsNullOrEmpty(vault.UserInfo + vault.Query + vault.Fragment))
            throw new InvalidOperationException("KeyVault:VaultUri must be an HTTPS vault origin.");

        TokenCredential identity = builder.Environment.IsDevelopment()
            ? new DefaultAzureCredential()
            : new ManagedIdentityCredential(section["ManagedIdentityClientId"] is { Length: > 0 } clientId
                ? ManagedIdentityId.FromUserAssignedClientId(clientId) : ManagedIdentityId.SystemAssigned);
        var client = new SecretClient(vault, identity, new SecretClientOptions { Diagnostics = { IsLoggingContentEnabled = false } });
        var keys = new Dictionary<string, string?>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));

        foreach (var provider in providers)
        {
            var name = section[$"ProviderSecrets:{provider.Id}"] ?? provider.Id;
            try
            {
                var secret = (await client.GetSecretAsync(name, cancellationToken: timeout.Token)).Value;
                if (string.IsNullOrWhiteSpace(secret.Value))
                    throw new InvalidOperationException($"Key Vault returned an empty key for '{provider.Id}'.");
                keys[$"Providers:{provider.Id}:ApiKey"] = secret.Value;
            }
            catch (RequestFailedException error) when (error.Status == 404) { /* Use local settings. */ }
            catch (Exception error) when (error is RequestFailedException or AuthenticationFailedException or OperationCanceledException)
            {
                throw new InvalidOperationException($"Could not load the Key Vault credential for '{provider.Id}'.");
            }
        }

        builder.Configuration.AddInMemoryCollection(keys);
    }

    internal static void AddLegacySettings(ConfigurationManager settings, IEnumerable<ProviderRegistration> providers)
    {
        var compatible = new Dictionary<string, string?>();
        foreach (var provider in providers)
        {
            var path = $"Providers:{provider.Id}:ApiKey";
            if (settings[path] is null && settings[$"{provider.Id.ToUpperInvariant()}_API_KEY"] is null
                && settings[$"AIServices:{provider.Id}:ApiKey"] is { Length: > 0 } legacy)
                compatible[path] = legacy;
        }
        settings.AddInMemoryCollection(compatible);
    }
}
