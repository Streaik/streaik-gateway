using System.Text.Json;
using Streaik.Messages;
using Streaik.Providers;
using Streaik.Responses;

namespace Streaik.Gateway;

internal static partial class ProtocolAdapters
{
    public static async Task MessagesViaChatAsync(GatewayCall call, MessagesRequest request, IChatCompletionsProvider provider)
    {
        var native = ChatCompletionsBridge.Request(request, request.MaxTokens);
        var metadataProvider = call.Get<INativeChatOptionsProvider>() is not null ? call.ProviderId : null;
        if (request.Stream == true)
            await call.WriteMessagesStreamAsync(ChatCompletionsBridge.Updates(provider.StreamChatAsync(native, call.Headers, call.Ct), metadataProvider is not null, metadataProvider));
        else await call.WriteAsync(ChatCompletionsBridge.Result(await provider.CompleteChatAsync(native, call.Headers, call.Ct), metadataProvider));
    }

    public static async Task ResponsesViaChatAsync(GatewayCall call, ResponseRequest request, IChatCompletionsProvider provider)
    {
        var native = ChatCompletionsBridge.Request(ToMessages(request), request.MaxOutputTokens);
        var metadataProvider = call.Get<INativeChatOptionsProvider>() is not null ? call.ProviderId : null;
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

    private static void AttachProviderMetadata(ResponseResult result, Dictionary<string, JsonElement>? metadata, string? provider)
    {
        if (provider is not null && metadata?.TryGetValue(provider, out var raw) == true)
            (result.Metadata ??= [])[provider] = raw;
    }
}
