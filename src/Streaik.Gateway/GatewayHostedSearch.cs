using System.Text.Json;
using Streaik.Providers;
using Streaik.Responses;
using Streaik.Tools;
using Streaik.Vercel.Models;

namespace Streaik.Gateway;

internal static class GatewayHostedSearch
{
    internal static async Task<HostedSearchRoute?> ResolveAsync(GatewayCall turn, string model, CancellationToken ct)
    {
        // Do not make the selector's provider active: the answer provider owns the inbound bearer.
        var slash = model.IndexOf('/');
        if (slash < 1 || slash == model.Length - 1) return null;
        using var lookup = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lookup.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var call = new GatewayCall(turn.Context, model, activateProvider: false, cancellationToken: lookup.Token);
            if (call.Get<IModelListingProvider>() is not { } listing) return null;
            var provider = call.Get<IDecisionsProvider>();
            var hasLanguage = false;
            await foreach (var entry in listing.ListModelsAsync(lookup.Token).WithCancellation(lookup.Token))
            {
                if (!string.Equals(entry.Id, call.NativeModel, StringComparison.Ordinal)) continue;
                // Use the same classification as /v1/models without modifying a provider's cached entry.
                var variant = new Streaik.OpenAI.Models.Model { Id = entry.Id, Type = entry.Type, Tags = entry.Tags };
                GatewayModelMetadata.Normalize(variant, call.ProviderId);
                if (variant.Type == "decision" && provider is not null)
                    return new(Decide: async (body, token) =>
                    {
                        var request = body.Deserialize<DecisionRequest>(JsonSerializerOptions.Web)!;
                        request.Model = call.NativeModel;
                        var result = await provider.DecideAsync(request, call.Headers, token);
                        return JsonSerializer.SerializeToElement(result, JsonSerializerOptions.Web);
                    });
                hasLanguage |= variant.Type == "language" && variant.Tags?.Contains("realtime") != true;
            }

            if (hasLanguage && (call.Get<IResponsesProvider>() is not null || call.Get<IMessagesProvider>() is not null || call.GetChatBridge() is not null))
                return new(Complete: async (instructions, input, token) =>
                {
                    var search = new GatewayCall(turn.Context, model, activateProvider: false, cancellationToken: token);
                    var result = await ProtocolAdapters.HostedResponseRoundAsync(search, new ResponseRequest
                    {
                        Model = search.NativeModel, Input = new ResponseInput(input), Instructions = instructions,
                        Stream = false, Store = false, MaxOutputTokens = 2048,
                    });
                    if (result.Error is not null || result.Status != "completed") return null;
                    return result.OutputText ?? string.Concat(result.OutputItems.OfType<ResponseInputMessage>().Where(message => message.Role == ResponseRole.Assistant)
                        .Select(message => message.Content.Text ?? string.Concat((message.Content.Parts ?? []).OfType<OutputTextPart>().Select(part => part.Text))));
                });
        }
        catch (Exception error) when (!ct.IsCancellationRequested && error is (HttpRequestException or JsonException or
            ProviderCredentialException or ContractException or OperationCanceledException)) { }
        ct.ThrowIfCancellationRequested();
        return null;
    }
}
