using System.Text.Json;
using Streaik.ChatCompletions.Models;
using Streaik.Messages;
using Streaik.Providers;
using Streaik.Responses;
using Streaik.Vercel.Models;

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
            await WriteMessageAsResponseAsync(call, message, completion.Created, metadataProvider);
        }
    }

    internal static async Task<ResponseResult> HostedResponseRoundAsync(GatewayCall call, ResponseRequest request, bool preferChat = false)
    {
        if (!preferChat && call.Get<IResponsesProvider>() is { } responses)
            return await responses.CreateResponseAsync(request, call.Headers, call.Ct);
        if (!call.PreferChatBridge && call.Get<IMessagesProvider>() is { } messages)
        {
            StreaikGateway.ValidateAnthropicMessageInputs(call, request.MaxOutputTokens);
            return MessageAsResponse(call, await messages.CreateMessageAsync(ToMessages(request), call.Headers, call.Ct), 0, call.ProviderId);
        }
        if (call.GetChatBridge() is not { } chat) throw new ContractException("This provider does not support hosted tools on this route.", 422);
        var metadataProvider = call.Get<INativeChatOptionsProvider>() is not null ? call.ProviderId : null;
        var extensions = request.AdditionalProperties;
        try
        {
            request.AdditionalProperties = extensions is null ? null : new(extensions);
            var options = TakeChatOptions(metadataProvider, request.AdditionalProperties);
            var native = ChatCompletionsBridge.Request(ToMessages(request), request.MaxOutputTokens);
            native.AdditionalProperties = options;
            var completion = await chat.CompleteChatAsync(native, call.Headers, call.Ct);
            return MessageAsResponse(call, ChatCompletionsBridge.Result(completion, metadataProvider), completion.Created, metadataProvider);
        }
        finally { request.AdditionalProperties = extensions; }
    }

    internal static IAsyncEnumerable<Streaik.Responses.Streaming.ResponseStreamPart> HostedResponseStreamAsync(GatewayCall call, ResponseRequest request, bool preferChat = false)
    {
        if (!preferChat && call.Get<IResponsesProvider>() is { } responses) return responses.StreamResponseAsync(request, call.Headers, call.Ct);
        if (!call.PreferChatBridge && call.Get<IMessagesProvider>() is { } messages)
        {
            StreaikGateway.ValidateAnthropicMessageInputs(call, request.MaxOutputTokens);
            return ResponseUpdates(messages.StreamMessageAsync(ToMessages(request), call.Headers, call.Ct));
        }
        if (call.GetChatBridge() is not { } chat) throw new ContractException("This provider does not support hosted tools on this route.", 422);
        var metadataProvider = call.Get<INativeChatOptionsProvider>() is not null ? call.ProviderId : null;
        var extensions = request.AdditionalProperties;
        try
        {
            request.AdditionalProperties = extensions is null ? null : new(extensions);
            var options = TakeChatOptions(metadataProvider, request.AdditionalProperties);
            var mistral = preferChat && call.ProviderId == "mistral";
            var native = ChatCompletionsBridge.Request(ToMessages(request, mapReasoningEffort: mistral), request.MaxOutputTokens);
            native.AdditionalProperties = options;
            if (mistral && request.Reasoning?.Effort is { } effort) native.ReasoningEffort = new(effort);
            return ResponseUpdates(ChatCompletionsBridge.Updates(chat.StreamChatAsync(native, call.Headers, call.Ct), metadataProvider is not null,
                metadataProvider, translatePresentation: mistral), metadataProvider);
        }
        finally { request.AdditionalProperties = extensions; }
    }

    internal static async Task WriteTranscriptAsync(GatewayCall call, TranscriptionResponse transcript, bool stream, bool responses)
    {
        var id = $"msg_{Guid.NewGuid():N}";
        var text = new MessageContentBlock { Type = "text", Text = transcript.Text };
        MessagesResponse Message(bool complete) => new() { Id = id, Type = "message", Role = "assistant", Model = call.NativeModel,
            Content = complete ? [text] : [], StopReason = complete ? "end_turn" : null, Metadata = complete ? transcript.ProviderMetadata : null };
        if (stream && responses) await call.WriteResponsesStreamAsync(ResponseUpdates(Parts(), call.ProviderId));
        else if (stream) await call.WriteMessagesStreamAsync(Parts());
        else if (responses) await WriteMessageAsResponseAsync(call, Message(true), 0, call.ProviderId);
        else await call.WriteAsync(Message(true));

        async IAsyncEnumerable<MessageStreamPart> Parts()
        {
            await Task.CompletedTask;
            yield return new() { Type = "message_start", Message = Message(false) };
            yield return new() { Type = "content_block_start", Index = 0, ContentBlock = new() { Type = "text", Text = "" } };
            yield return new() { Type = "content_block_delta", Index = 0, Delta = new() { Type = "text_delta", Text = transcript.Text } };
            yield return new() { Type = "content_block_stop", Index = 0 };
            yield return new() { Type = "message_delta", Delta = new() { StopReason = "end_turn" } };
            yield return new() { Type = "message_stop", Metadata = transcript.ProviderMetadata };
        }
    }

    private static async Task WriteMessageAsResponseAsync(GatewayCall call, MessagesResponse message, long created, string? metadataProvider)
        => await call.WriteAsync(MessageAsResponse(call, message, created, metadataProvider));

    private static ResponseResult MessageAsResponse(GatewayCall call, MessagesResponse message, long created, string? metadataProvider)
    {
        var result = ResponseResultFrom(message.Id!, created > 0 ? created : DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            message.Content.Select((block, index) => OutputItem(message.Id!, index, block)).ToList(), message.Usage, message.StopReason);
        result.Model = message.Model ?? call.NativeModel;
        AttachProviderMetadata(result, message.Metadata, metadataProvider);
        GatewayPricing.Attach(result, GatewayPricing.Cost(message.Metadata));
        return result;
    }

    private static Dictionary<string, JsonElement>? TakeChatOptions(string? provider, Dictionary<string, JsonElement>? extensions)
    {
        if (provider is null || extensions?.Remove(provider, out var native) != true) return null;
        if (native.ValueKind != JsonValueKind.Object) throw new ContractException("Provider-specific chat options must be an object.");
        var reserved = JsonSerializerOptions.Web.GetTypeInfo(typeof(ChatCompletionOptions)).Properties
            .Select(property => property.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        reserved.UnionWith([provider, "background", "previous_response_id", "conversation", "input", "authorization",
            "api-key", "api_key", "apiKey", "x-api-key", "headers"]);
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
