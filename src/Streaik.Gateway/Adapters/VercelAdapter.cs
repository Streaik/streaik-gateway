using System.Text.Json;
using Streaik.ChatCompletions.Models;
using Microsoft.Extensions.DependencyInjection;
using Streaik.Providers;
using Streaik.Vercel.Models;
using Streaik.Vercel.Security;

namespace Streaik.Gateway;

internal static partial class VercelAdapter
{
    private const string NativeBlock = "nativeBlock";
    private const string NativeItem = "nativeItem";
    private static readonly JsonSerializerOptions Web = JsonSerializerOptions.Web;
    private static readonly HashSet<string> OwnedRequestFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "model", "input", "messages", "instructions", "prompt", "stream", "stream_options", "store", "background",
        "previous_response_id", "conversation", "include", "tools", "tool_choice",
        "authorization", "api_key", "apiKey", "x-api-key", "headers"
    };

    public static async Task RunAsync(GatewayCall call, ChatRequest request)
    {
        var services = call.Context.RequestServices;
        var signer = services.GetService<ToolApprovalSigner>();
        var reviews = request.Messages.SelectMany(m => m.Parts).OfType<ToolInvocationPart>()
            .Where(tool => tool.Approval is not null || tool.State is "approval-requested" or "approval-responded").ToList();
        if (reviews.Any(tool => tool.ProviderExecuted == true))
            throw new ContractException("API Chat only accepts client tool result approvals.");
        var reviewClientTools = request.ClientToolResultApproval && request.Tools is { Count: > 0 };
        // A registered, user-bound signer can be unavailable for requests without a subject.
        // That is distinct from a host intentionally running with signing disabled.
        if (signer is null && (reviewClientTools || reviews.Count != 0)
            && services.GetService<IServiceProviderIsService>()?.IsService(typeof(ToolApprovalSigner)) == true)
            throw new ContractException("Tool approval signing is unavailable for this request.");
        try { signer?.ValidateIncoming(request.Messages); }
        catch (ToolApprovalSignatureException error) { throw new ContractException(error.Message); }

        IAsyncEnumerable<UIMessagePart> stream;
        if (!call.PreferChatBridge && call.Get<IResponsesProvider>() is { } responses)
        {
            var native = await ResponseRequestFromAsync(call, request);
            await GatewayContainerArtifacts.PrepareAsync(call, native);
            var artifacts = new GatewayContainerArtifacts.Turn(call);
            stream = ResponseParts(call, request, responses.StreamResponseAsync(native, call.Headers, call.Ct), artifacts,
                native.Include?.Contains(EncryptedReasoningInclude, StringComparer.Ordinal) == true);
        }
        else if (!call.PreferChatBridge && call.Get<IMessagesProvider>() is { } messages)
            stream = MessageParts(call, request, messages.StreamMessageAsync(MessagesRequestFrom(call, request), call.Headers, call.Ct));
        else if (call.GetChatBridge() is { } chat)
        {
            var nativeOptions = call.Get<INativeChatOptionsProvider>() is not null;
            if ((!nativeOptions && request.ProviderMetadata?.ContainsKey(call.ProviderId) == true)
                || request.Messages.SelectMany(message => message.Parts).OfType<ToolInvocationPart>().Any(tool => tool.ProviderExecuted == true))
                throw new ContractException("Provider-specific Vercel options require a native adapter.");
            var translatePresentation = call.ProviderId == "cohere";
            var native = ChatCompletionsBridge.Request(MessagesRequestFrom(call, request, viaChat: true), request.MaxOutputTokens, translatePresentation);
            if (nativeOptions) native.AdditionalProperties = ApplyNativeOptions(call, request, native, Web);
            stream = MessageParts(call, request, ChatCompletionsBridge.Updates(chat.StreamChatAsync(native, call.Headers, call.Ct), nativeOptions,
                translatePresentation: translatePresentation));
        }
        else if (call.Get<IImageProvider>() is { } images)
        {
            await ImageAdapters.VercelAsync(call, request, images);
            return;
        }
        else
        {
            await GatewayCall.ErrorAsync(call.Context, Protocol.Vercel, 400, "Provider does not support this protocol.");
            return;
        }
        if (reviewClientTools)
            stream = ReviewClientResults(stream, request.Tools!.Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal), call.Ct);
        await call.WriteVercelStreamAsync(signer is null ? stream : signer.SignOutgoing(stream, call.Ct));
    }

    private static async IAsyncEnumerable<UIMessagePart> ReviewClientResults(IAsyncEnumerable<UIMessagePart> stream,
        HashSet<string> declared, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var part in stream.WithCancellation(cancellationToken))
        {
            yield return part;
            if (part is ToolCallPart { ProviderExecuted: false } tool && declared.Contains(tool.ToolName))
                yield return new ToolApprovalRequestUIPart
                { ToolCallId = tool.ToolCallId, ApprovalId = Guid.NewGuid().ToString("N") };
        }
    }

    private static T? Replay<T>(Dictionary<string, Dictionary<string, object>?>? metadata, string provider, string key, JsonSerializerOptions json)
        where T : class
        => metadata?.TryGetValue(provider, out var values) == true && values?.TryGetValue(key, out var value) == true
            && value is JsonElement { ValueKind: JsonValueKind.Object } element ? element.Deserialize<T>(json) : null;

    private static T? ReplayPart<T>(Dictionary<string, object>? metadata, string provider, string key, JsonSerializerOptions json)
        where T : class
        => metadata?.TryGetValue(provider, out var value) == true && value is JsonElement { ValueKind: JsonValueKind.Object } element
            && element.TryGetProperty(key, out var native) && native.ValueKind == JsonValueKind.Object ? native.Deserialize<T>(json) : null;

    private static JsonElement? Option(ChatRequest request, string provider, string key)
        => request.ProviderMetadata?.TryGetValue(provider, out var value) == true && value.ValueKind == JsonValueKind.Object
            && value.TryGetProperty(key, out var setting) ? setting : null;

    private static Dictionary<string, JsonElement>? ApplyNativeOptions<T>(GatewayCall call, ChatRequest request, T native, JsonSerializerOptions json)
        where T : class
    {
        if (request.ProviderMetadata?.TryGetValue(call.ProviderId, out var options) != true) return null;
        if (options.ValueKind != JsonValueKind.Object) throw new ContractException("Provider-specific chat options must be an object.");
        var contract = json.GetTypeInfo(typeof(T)).Properties.ToDictionary(property => property.Name, StringComparer.OrdinalIgnoreCase);
        var extensions = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in options.EnumerateObject())
        {
            if (OwnedRequestFields.Contains(property.Name) || property.Name.Equals(call.ProviderId, StringComparison.OrdinalIgnoreCase)) continue;
            // The protocol's own JSON contract handles known fields; future fields remain raw JSON.
            if (!contract.TryGetValue(property.Name, out var field)) extensions[property.Name] = property.Value;
            else if (!field.IsExtensionData && field.Set is { } set)
            {
                try { set(native, property.Value.Deserialize(field.PropertyType, json)); }
                catch (JsonException) { throw new ContractException($"Invalid provider option '{field.Name}'."); }
            }
        }
        return extensions;
    }

    private static Dictionary<string, Dictionary<string, object>?> ToolMetadata(string provider, string key, JsonElement data)
        => new(StringComparer.Ordinal) { [provider] = new() { [key] = data } };

    private static JsonElement ToolInput(object? value) => value as JsonElement? ?? JsonSerializer.SerializeToElement(value ?? new { }, Web);
    private static string ToolOutput(object? value) => value switch
    {
        string text => text, JsonElement { ValueKind: JsonValueKind.String } text => text.GetString()!, _ => JsonSerializer.Serialize(value, Web)
    };

    private static string ToolName(ToolInvocationPart part) => part.ToolName
        ?? (part.Type.StartsWith("tool-", StringComparison.Ordinal) ? part.Type[5..] : throw new ContractException("Missing tool name."));

    // Approvals have already been verified against their call and signed execution kind.
    // Never read or translate any submitted output on the denial path.
    private static (string Output, bool IsError) ClientToolResult(ToolInvocationPart tool)
    {
        if (tool.Approval is { } approval)
        {
            if (approval.Approved == false && tool.State is "approval-responded" or "output-denied")
                return ("The user declined to share this tool result.", true);
            if (approval.Approved != true || tool.State is not ("output-available" or "output-error" or "approval-responded")
                || (tool.Output is null && tool.ErrorText is null))
                throw new ContractException("Client function calls require an approved result before continuing.");
        }
        else if (tool.State is not ("output-available" or "output-error"))
            throw new ContractException("Client function calls require a result before continuing.");
        return (tool.ErrorText ?? ToolOutput(tool.Output), tool.State == "output-error");
    }

    private static FinishUIPart Finish(GatewayCall call, ChatRequest request, string reason, int? input, int? output,
        string provider, object? nativeUsage, Dictionary<string, JsonElement>? additional, long? timestamp = null, GatewayPrice? cost = null)
    {
        var metadata = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (additional?.TryGetValue("providerMetadata", out var supplied) == true && supplied.ValueKind == JsonValueKind.Object)
            foreach (var entry in supplied.EnumerateObject()) metadata[entry.Name] = entry.Value;
        if (additional is not null && call.Get<INativeChatOptionsProvider>() is not null)
        {
            var raw = additional.Where(pair => pair.Key is not ("providerMetadata" or "metadata" or "error")).ToDictionary();
            if (raw.Count != 0)
            {
                var scoped = metadata.TryGetValue(provider, out var existing) && existing.ValueKind == JsonValueKind.Object
                    ? existing.EnumerateObject().ToDictionary(property => property.Name, property => property.Value) : new();
                scoped.TryAdd("raw", JsonSerializer.SerializeToElement(raw, Web));
                if (nativeUsage is not null) scoped.TryAdd("usage", JsonSerializer.SerializeToElement(nativeUsage, Web));
                metadata[provider] = JsonSerializer.SerializeToElement(scoped, Web);
            }
        }
        if (nativeUsage is not null && !metadata.ContainsKey(provider))
            metadata[provider] = JsonSerializer.SerializeToElement(new { usage = nativeUsage }, Web);
        return new FinishUIPart { FinishReason = reason, MessageMetadata = new FinishMessageMetadata
        {
            Model = call.Model, Timestamp = timestamp is > 0 ? DateTimeOffset.FromUnixTimeSeconds(timestamp.Value) : DateTimeOffset.UtcNow,
            Temperature = request.Temperature, Usage = new Usage { PromptTokens = input, CompletionTokens = output,
                TotalTokens = input.HasValue && output.HasValue ? input.Value + output.Value : null },
            ProviderMetadata = GatewayPricing.Attach(metadata.Count == 0 ? null : metadata, cost)
        } };
    }
}
