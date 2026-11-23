using Streaik.Messages;
using Streaik.Providers;
using Streaik.Responses;

namespace Streaik.Gateway;

internal static partial class ProtocolAdapters
{
    public static async Task MessagesViaChatAsync(GatewayCall call, MessagesRequest request, IChatCompletionsProvider provider)
    {
        var native = ChatCompletionsBridge.Request(request, request.MaxTokens);
        if (request.Stream == true)
            await call.WriteMessagesStreamAsync(ChatCompletionsBridge.Updates(provider.StreamChatAsync(native, call.Headers, call.Ct)));
        else await call.WriteAsync(ChatCompletionsBridge.Result(await provider.CompleteChatAsync(native, call.Headers, call.Ct)));
    }

    public static async Task ResponsesViaChatAsync(GatewayCall call, ResponseRequest request, IChatCompletionsProvider provider)
    {
        var native = ChatCompletionsBridge.Request(ToMessages(request), request.MaxOutputTokens);
        if (request.Stream == true)
            await call.WriteResponsesStreamAsync(ResponseUpdates(ChatCompletionsBridge.Updates(provider.StreamChatAsync(native, call.Headers, call.Ct))));
        else
        {
            var completion = await provider.CompleteChatAsync(native, call.Headers, call.Ct);
            var message = ChatCompletionsBridge.Result(completion);
            var result = ResponseResultFrom(message.Id!, completion.Created > 0 ? completion.Created : DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                message.Content.Select((block, index) => OutputItem(message.Id!, index, block)).ToList(), message.Usage, message.StopReason);
            result.Model = message.Model ?? call.NativeModel;
            GatewayPricing.Attach(result, GatewayPricing.Cost(message.Metadata));
            await call.WriteAsync(result);
        }
    }
}
