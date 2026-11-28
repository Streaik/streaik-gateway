using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Streaik.Catalog;
using Streaik.ChatCompletions.Models;
using Streaik.Messages;
using Streaik.OpenAI.Models;
using Streaik.Providers;
using Streaik.Responses;

namespace Streaik.Gateway;

internal readonly record struct GatewayPrice(decimal Amount);

internal static partial class GatewayPricing
{
    private static readonly ConcurrentDictionary<string, JsonObject> Catalog = new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;

    public static ModelPricing? Rates(string provider, string model, string? modelType = null)
    {
        if (RateCard(provider, model) is not { } price || price["input"] is null || price["output"] is null) return null;
        var rates = price.Deserialize<ModelPricing>(Json);
        // The catalog's Luna rates are for chat. Decisions charge only for input.
        return rates is not null && provider == "openai" && modelType == "decision" && (model is "gpt-6-luna" or "openai/gpt-6-luna")
            ? new ModelPricing { Input = rates.Input, Output = 0 } : rates;
    }

    private static JsonObject? RateCard(string provider, string model)
    {
        var data = Catalog.GetOrAdd(provider, id => ProviderCatalog.ReadPricing(id) ?? new());
        var qualified = model.StartsWith(provider + "/", StringComparison.Ordinal) ? model : $"{provider}/{model}";
        return (data[qualified] as JsonObject)?["pricing"] as JsonObject;
    }

    public static async ValueTask<GatewayPrice?> EstimateAsync(GatewayCall call, string? upstreamModel, object? usage)
    {
        if (Reported(call.ProviderId, usage) is { } reported) return reported;
        if (usage is JsonElement { ValueKind: JsonValueKind.Object } wire)
            usage = wire.Deserialize<ResponseUsage>(ResponseJson.Default);
        if (usage is ChatCompletionUsage { PromptTokensDetails: { } detail } && (detail.AudioTokens > 0 || detail.ImageTokens > 0))
            return null;
        if (usage is ChatCompletionUsage { CompletionTokensDetails.AudioTokens: > 0 }
            || usage is MessagesUsage { ServerToolUse: not null }) return null;

        (int? Input, int? Output, int? Read, int? Write, bool IncludesCache) counts = usage switch
        {
            ChatCompletionUsage chat when chat.TotalTokens > 0 => (chat.PromptTokens, chat.CompletionTokens,
                chat.PromptTokensDetails?.CachedTokens, chat.PromptTokensDetails?.CacheWriteTokens, true),
            ResponseUsage responses => (responses.InputTokens, responses.OutputTokens,
                responses.InputTokensDetails?.CachedTokens, responses.InputTokensDetails?.CacheWriteTokens, true),
            MessagesUsage messages => (messages.InputTokens, messages.OutputTokens,
                messages.CacheReadInputTokens, messages.CacheCreationInputTokens, false),
            _ => default
        };
        if (counts.Input is not { } input || counts.Output is not { } output) return null;
        var read = counts.Read ?? 0;
        var write = counts.Write ?? 0;
        var uncached = input - (counts.IncludesCache ? (long)read + write : 0);
        if (uncached < 0 || output < 0 || read < 0 || write < 0 || uncached + output + read + write == 0) return null;

        var rates = (upstreamModel is null ? null : Rates(call.ProviderId, upstreamModel)) ?? Rates(call.ProviderId, call.NativeModel);
        if (rates is null && call.ProviderId == "arceeai" && call.Get<IModelListingProvider>() is { } listing)
        {
            // Arcee exposes its rates via model discovery rather than the static catalog.
            ModelPricing? fallback = null;
            const string prefix = "arceeai/";
            var upstreamId = upstreamModel?.StartsWith(prefix, StringComparison.Ordinal) == true ? upstreamModel[prefix.Length..] : upstreamModel;
            using var lookup = CancellationTokenSource.CreateLinkedTokenSource(call.Ct);
            lookup.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                await foreach (var model in listing.ListModelsAsync(lookup.Token).WithCancellation(lookup.Token))
                {
                    if (model.Id == upstreamId && model.Pricing is not null) { rates = model.Pricing; break; }
                    if (model.Id == call.NativeModel) fallback = model.Pricing;
                }
            }
            catch (Exception ex) when (!call.Ct.IsCancellationRequested && ex is HttpRequestException or IOException or JsonException or OperationCanceledException)
            {
                // A rate-card failure should not fail a successful inference.
            }
            rates ??= fallback;
        }
        if (rates is null || (read > 0 && rates.InputCacheRead is null) || (write > 0 && rates.InputCacheWrite is null)) return null;
        return new GatewayPrice(uncached * rates.Input + output * rates.Output
            + read * (rates.InputCacheRead ?? 0) + write * (rates.InputCacheWrite ?? 0));
    }

    public static GatewayPrice? Reported(string provider, object? usage)
    {
        if (provider != "openrouter") return null;
        var amount = usage switch
        {
            ChatCompletionUsage chat => Amount(chat.AdditionalProperties?.GetValueOrDefault("cost") ?? default),
            ResponseUsage response => Amount(response.AdditionalProperties?.GetValueOrDefault("cost") ?? default),
            MessagesUsage message => Amount(message.AdditionalProperties?.GetValueOrDefault("cost") ?? default),
            JsonElement { ValueKind: JsonValueKind.Object } element when element.TryGetProperty("cost", out var cost) => Amount(cost),
            _ => null
        };
        return amount is { } value ? new GatewayPrice(value) : null;
    }

    public static GatewayPrice? Cost(Dictionary<string, JsonElement>? metadata)
        => metadata?.TryGetValue("gateway", out var gateway) == true && gateway.ValueKind == JsonValueKind.Object
           && gateway.TryGetProperty("cost", out var cost) && Amount(cost) is { } amount
           ? new GatewayPrice(amount) : null;

    public static GatewayPrice? Cost(ResponseResult response)
        => Cost(response.Metadata?.ToDictionary(x => x.Key, x => JsonSerializer.SerializeToElement(x.Value, Json)));

    public static Dictionary<string, JsonElement>? CostMetadata(GatewayPrice? cost) => Attach((Dictionary<string, JsonElement>?)null, cost);

    public static Dictionary<string, JsonElement>? ChatMetadata(Dictionary<string, JsonElement>? extras)
        => extras?.TryGetValue("metadata", out var metadata) == true && metadata.ValueKind == JsonValueKind.Object
            ? metadata.Deserialize<Dictionary<string, JsonElement>>() : null;

    private static decimal? Amount(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number when value.TryGetDecimal(out var number) && number >= 0 => number,
        JsonValueKind.String when decimal.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            && number >= 0 => number,
        _ => null
    };

    public static Dictionary<string, JsonElement>? Attach(Dictionary<string, JsonElement>? metadata, GatewayPrice? cost)
    {
        if (cost is null) return metadata;
        var gateway = metadata?.TryGetValue("gateway", out var prior) == true
            ? prior.ValueKind == JsonValueKind.Object ? JsonObject.Create(prior) : null : new JsonObject();
        if (gateway is null || (gateway.TryGetPropertyValue("cost", out var existing) && Amount(JsonSerializer.SerializeToElement(existing)) is not null)) return metadata;
        gateway["cost"] = cost.Value.Amount;

        (metadata ??= [])["gateway"] = JsonSerializer.SerializeToElement(gateway);
        return metadata;
    }

    public static void Attach(ResponseResult response, GatewayPrice? cost)
    {
        if (cost is null) return;
        var metadata = response.Metadata?.ToDictionary(x => x.Key, x => JsonSerializer.SerializeToElement(x.Value, Json));
        var enriched = Attach(metadata, cost);
        if (enriched?.TryGetValue("gateway", out var gateway) == true)
            (response.Metadata ??= [])["gateway"] = gateway;
    }

    public static Dictionary<string, JsonElement>? AttachChat(Dictionary<string, JsonElement>? extras, GatewayPrice? cost)
    {
        if (cost is null) return extras;
        Dictionary<string, JsonElement>? metadata = null;
        if (extras?.TryGetValue("metadata", out var prior) == true)
        {
            if (prior.ValueKind != JsonValueKind.Object) return extras;
            metadata = prior.Deserialize<Dictionary<string, JsonElement>>();
        }
        (extras ??= [])["metadata"] = JsonSerializer.SerializeToElement(Attach(metadata, cost));
        return extras;
    }
}
