using System.Text.Json;
using System.Text.Json.Nodes;
using Streaik.OpenAI.Models;
using Streaik.Providers;
using Streaik.Vercel.Models;

namespace Streaik.Gateway;

internal static partial class GatewayPricing
{
    // Provider metadata is retained as-is. Only documented USD fields are promoted to gateway.cost.
    private static JsonElement Property(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) ? property : default;

    private static JsonElement Native(Dictionary<string, JsonElement>? metadata, string provider)
        => metadata?.GetValueOrDefault(provider) ?? default;

    private static GatewayPrice? Scale(decimal? units, decimal? rate)
    {
        if (units is not >= 0 || rate is not >= 0) return null;
        try { return new GatewayPrice(units.Value * rate.Value); }
        catch (OverflowException) { return null; } // An invalid meter must not fail successful inference.
    }

    // The catalog's existing `pricing.input` is per second for these models, not per token.
    public static Dictionary<string, JsonElement>? Transcription(GatewayCall call, Dictionary<string, JsonElement>? metadata)
    {
        if (Cost(metadata) is not null) return metadata;
        var native = Native(metadata, call.ProviderId);
        var cost = call.ProviderId switch
        {
            "gladia" when call.NativeModel is "solaria-1" or "solaria-3" => Scale(
                Amount(Property(Property(native, "metadata"), "billing_time")), CatalogInput(call.ProviderId, call.NativeModel)),
            "openrouter" => Reported(call.ProviderId, native), // Transcription adapter stores the usage object directly.
            _ => null
        };
        return Attach(metadata, cost);
    }

    private static decimal? CatalogInput(string provider, string model)
        => RateCard(provider, model)?["input"] is { } rate ? Amount(JsonSerializer.SerializeToElement(rate, Json)) : null;

    public static Dictionary<string, JsonElement>? Decision(GatewayCall call, DecisionResponse result)
    {
        var metadata = result.ProviderMetadata;
        if (Cost(metadata) is not null) return metadata;
        var native = Native(metadata, call.ProviderId);
        var nativeModel = Property(native, "model") is { ValueKind: JsonValueKind.String } model ? model.GetString() : null;
        var cost = call.ProviderId switch
        {
            "openrouter" => Reported(call.ProviderId, Property(native, "usage")),
            // Luna uses the catalog input rate for both capabilities, but decisions have no output charge.
            "openai" when (nativeModel ?? call.NativeModel) is "gpt-6-luna" or "openai/gpt-6-luna"
                => Scale(result.Usage?.InputTokens, CatalogInput(call.ProviderId, "gpt-6-luna")),
            _ => null
        };
        return Attach(metadata, cost);
    }

    public static Dictionary<string, JsonElement>? Image(GatewayCall call, OpenAIImagesResponse result, bool hasImageInput)
    {
        var metadata = ImageAdapters.ProviderMetadata(result, call.ProviderId);
        if (Cost(metadata) is not null) return metadata;
        if (call.ProviderId == "openrouter")
        {
            var usage = ImageProviderUsage.Get(result);
            if (usage.ValueKind == JsonValueKind.Object)
                (metadata ??= []).TryAdd(call.ProviderId, JsonSerializer.SerializeToElement(new { usage }, Json));
            return Attach(metadata, Reported(call.ProviderId, usage));
        }
        // SVG authoring is billed separately; do not publish a partial rendering-only cost.
        return call.ProviderId == "openai" && result.SvgUsage is null && result.OutputFormat != "svg"
            ? Attach(metadata, OpenAiImage(call.NativeModel, result.Usage, hasImageInput)) : metadata;
    }

    private static GatewayPrice? OpenAiImage(string model, OpenAIImageUsage? usage, bool hasImageInput)
    {
        // These modality-specific rates do not fit the catalog's single input/output rate pair.
        if (model is not ("gpt-image-2" or "gpt-image-2-2026-04-21" or "gpt-image-2.5-sunburst" or "gpt-image-2.5-flare"
            or "gpt-image-2.5-sunburst-2026-09-08" or "gpt-image-2.5-flare-2026-09-08"
            or "gpt-image-2.6-sunburst" or "gpt-image-2.6-flare") || usage?.InputTokens is not >= 0
            || usage.OutputTokens is not >= 0 || usage.OutputTokensDetails?.TextTokens is not (null or 0)) return null;

        var detail = usage.InputTokensDetails;
        var image = detail?.ImageTokens ?? 0;
        var text = detail?.TextTokens ?? (usage.InputTokens.Value - image);
        if (image < 0 || text < 0 || (long)image + text != usage.InputTokens ||
            (hasImageInput && detail?.ImageTokens is null) ||
            (usage.OutputTokensDetails?.ImageTokens is { } imageOutput && imageOutput != usage.OutputTokens)) return null;

        // The official Images usage has no cached-token meter; use the published standard rates.
        return new GatewayPrice((image * 8m + text * 5m + usage.OutputTokens.Value * 30m) / 1_000_000m);
    }

    public static void RemoveNativeCost(Dictionary<string, JsonElement>? extensions)
    {
        if (extensions?.TryGetValue("providerMetadata", out var value) == true && WithoutGatewayCost(value) is { } metadata)
        {
            if (metadata.Count == 0) extensions.Remove("providerMetadata");
            else extensions["providerMetadata"] = JsonSerializer.SerializeToElement(metadata, Json);
        }
    }

    public static JsonElement RemoveNativeImageEventCost(JsonElement evt)
    {
        if (WithoutGatewayCost(Property(evt, "providerMetadata")) is not { } metadata) return evt;
        var body = JsonObject.Create(evt)!;
        if (metadata.Count == 0) body.Remove("providerMetadata");
        else body["providerMetadata"] = metadata;
        return JsonSerializer.SerializeToElement(body, Json);
    }

    private static JsonObject? WithoutGatewayCost(JsonElement value)
    {
        if (Property(value, "gateway") is not { ValueKind: JsonValueKind.Object } gateway
            || !gateway.TryGetProperty("cost", out _)) return null;
        var result = JsonObject.Create(value)!;
        var entry = (JsonObject)result["gateway"]!;
        entry.Remove("cost");
        if (entry.Count == 0) result.Remove("gateway");
        return result;
    }

    public static Dictionary<string, JsonElement>? OpenRouterUsage(string provider, Dictionary<string, JsonElement>? metadata)
        => Attach(metadata, Reported(provider, Property(Native(metadata, provider), "usage")));

    public static Dictionary<string, JsonElement>? Speech(GatewayCall call, Dictionary<string, JsonElement>? metadata, int? characters)
        => call.ProviderId == "openai" && call.NativeModel is "tts-1" or "tts-1-hd" or "tts-1-1106" or "tts-1-hd-1106"
            ? Attach(metadata, Scale(characters, CatalogInput(call.ProviderId, call.NativeModel))) : metadata;

    public static Dictionary<string, JsonElement>? Embedding(GatewayCall call, OpenAIEmbeddingResponse result)
    {
        if (call.ProviderId != "openai" || result.Usage?.PromptTokens is not > 0) return null;
        var model = string.IsNullOrEmpty(result.Model) ? call.NativeModel : result.Model;
        if (model is not ("text-embedding-3-small" or "text-embedding-3-large")) return null;
        return CostMetadata(Scale(result.Usage.PromptTokens, CatalogInput(call.ProviderId, model)));
    }
}
