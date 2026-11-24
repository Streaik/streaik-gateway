using System.Text.Json;
using Streaik.ChatCompletions.Models;
using Streaik.Messages;
using Streaik.Providers;
using Streaik.Responses;

namespace Streaik.Gateway;

internal static partial class ProtocolAdapters
{
    public static async Task MessagesViaChatAsync(GatewayCall call, MessagesRequest request, IChatCompletionsProvider provider)
    {
        var metadataProvider = call.Get<INativeChatOptionsProvider>() is not null ? call.ProviderId : null;
        var options = TakeChatOptions(metadataProvider, request.AdditionalProperties);
        var native = ChatCompletionsBridge.Request(request, request.MaxTokens);
        native.AdditionalProperties = options;
        if (request.Stream == true)
            await call.WriteMessagesStreamAsync(ChatCompletionsBridge.Updates(provider.StreamChatAsync(native, call.Headers, call.Ct), metadataProvider is not null, metadataProvider));
        else await call.WriteAsync(ChatCompletionsBridge.Result(await provider.CompleteChatAsync(native, call.Headers, call.Ct), metadataProvider));
    }

    public static async Task ResponsesViaChatAsync(GatewayCall call, ResponseRequest request, IChatCompletionsProvider provider)
    {
        var metadataProvider = call.Get<INativeChatOptionsProvider>() is not null ? call.ProviderId : null;
        var options = TakeChatOptions(metadataProvider, request.AdditionalProperties);
        var native = ChatCompletionsBridge.Request(ToMessages(request), request.MaxOutputTokens);
        native.AdditionalProperties = options;
        if (request.Stream == true)
            await call.WriteResponsesStreamAsync(ResponseUpdates(ChatCompletionsBridge.Updates(provider.StreamChatAsync(native, call.Headers, call.Ct),
                metadataProvider is not null, metadataProvider), metadataProvider));
        else
        {
            var completion = await provider.CompleteChatAsync(native, call.Headers, call.Ct);
            var message = ChatCompletionsBridge.Result(completion, metadataProvider);
            var result = ResponseResultFrom(message.Id!, completion.Created > 0 ? completion.Created : DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                message.Content.Select((block, index) => OutputItem(message.Id!, index, block)).ToList(), message.Usage, message.StopReason);
            result.Model = message.Model ?? call.NativeModel;
            AttachProviderMetadata(result, message.Metadata, metadataProvider);
            GatewayPricing.Attach(result, GatewayPricing.Cost(message.Metadata));
            await call.WriteAsync(result);
        }
    }

    private static Dictionary<string, JsonElement>? TakeChatOptions(string? provider, Dictionary<string, JsonElement>? extensions)
    {
        if (provider is null || extensions?.Remove(provider, out var native) != true) return null;
        if (native.ValueKind != JsonValueKind.Object) throw new ContractException("Provider-specific chat options must be an object.");
        var reserved = JsonSerializerOptions.Web.GetTypeInfo(typeof(ChatCompletionOptions)).Properties
            .Select(property => property.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // The normal adapters still validate every extension outside the selected provider's namespace.
        return native.EnumerateObject().Where(field => !reserved.Contains(field.Name))
            .ToDictionary(field => field.Name, field => field.Value, StringComparer.Ordinal);
    }

    private static void AttachProviderMetadata(ResponseResult result, Dictionary<string, JsonElement>? metadata, string? provider)
    {
        if (provider is not null && metadata?.TryGetValue(provider, out var raw) == true)
            (result.Metadata ??= [])[provider] = raw;
    }
}
