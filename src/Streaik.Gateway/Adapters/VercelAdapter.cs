using System.Text.Json;
using Streaik.ChatCompletions.Models;
using Microsoft.Extensions.DependencyInjection;
using Streaik.Messages;
using Streaik.Providers;
using Streaik.Tools;
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

    public static async Task RunAsync(GatewayCall call, ChatRequest request, HostedToolDefinition[]? hosted = null, McpToolDefinition[]? providerMcp = null)
    {
        var services = call.Context.RequestServices;
        var signer = services.GetService<ToolApprovalSigner>();
        var converted = request.Messages.SelectMany(m => m.Parts).OfType<ToolInvocationPart>().Any(UpstreamMcpReview.IsReview);
        var upstreamReviews = services.GetService<HostedToolsEnabled>() is not null;
        var hostedApproval = new HostedMcpChatApproval(call, request, hosted, providerMcp, Option(request, call.ProviderId, "tools"), signer, upstreamReviews);
        var mistralChat = call.ProviderId == "mistral";
        var nativeResponses = !mistralChat && (!call.PreferChatBridge || providerMcp is { Length: > 0 }) && call.Get<IResponsesProvider>() is not null;
        var messagesMcp = !nativeResponses && providerMcp is { Length: > 0 } && call.ProviderId == "anthropic" && call.Get<IMessagesProvider>() is not null;
        if (providerMcp is { Length: > 0 } && !nativeResponses && !messagesMcp)
            throw new ContractException("The selected provider route cannot forward provider-executed MCP.", 422);
        if (converted && ((!nativeResponses && hosted is not { Length: > 0 }) || !upstreamReviews))
            throw new ContractException("This route cannot continue the MCP result review.", 422);
        var reviews = request.Messages.SelectMany(m => m.Parts).OfType<ToolInvocationPart>()
            .Where(tool => tool.Approval is not null || tool.State is "approval-requested" or "approval-responded").ToList();
        if (reviews.Any(tool => tool.ProviderExecuted == true && !UpstreamMcpReview.IsReview(tool) && !ProviderMcpApproval.IsApproval(tool)
            && !HostedMcpChatApproval.IsApproval(tool)))
            throw new ContractException("API Chat does not accept this type of tool approval.");
        if (request.Messages.SelectMany(m => m.Parts).OfType<ToolInvocationPart>().Any(HostedMcpChatApproval.IsContinuation)
            && !nativeResponses && hosted is not { Length: > 0 })
            throw new ContractException("This route cannot continue the hosted MCP execution approval.", 422);
        if (!nativeResponses && reviews.Any(ProviderMcpApproval.IsApproval))
            throw new ContractException("This route cannot continue provider MCP execution approval.", 422);
        // A registered, user-bound signer can be unavailable for requests without a subject.
        // That is distinct from a host intentionally running with signing disabled.
        var signingUnavailable = signer is null
            && services.GetService<IServiceProviderIsService>()?.IsService(typeof(ToolApprovalSigner)) == true;
        if (signingUnavailable && (request.Tools?.Count > 0 || reviews.Count != 0 || HasNativeClientTools(call, request)
            || hosted?.OfType<McpToolDefinition>().Any(tool => tool.RequireApproval is not McpApprovalPolicy.Never) == true))
            throw new ContractException("Tool approval signing is unavailable for this request.");
        try { signer?.ValidateIncoming(request.Messages.Select(message => new UIMessage { Id = message.Id, Role = message.Role,
            Parts = message.Parts.Where(part => part is not ToolInvocationPart tool || !HostedMcpChatApproval.IsContinuation(tool)).ToList() })); }
        catch (ToolApprovalSignatureException error) { throw new ContractException(error.Message); }

        // Hosted activity describes an already-completed host round; never trust it as inference history.
        foreach (var message in request.Messages.Where(message => message.Role == Role.assistant))
            message.Parts.RemoveAll(part => part is ToolInvocationPart { ProviderExecuted: true, Approval: null } tool
                && tool.ToolMetadata?.GetValueOrDefault(HostedToolActivity.UiMetadataKey).ValueKind == JsonValueKind.True);

        Dictionary<string, HostedMcpChatApproval.Result> hostedReplay;
        try { hostedReplay = await hostedApproval.ResolveAsync(); }
        catch (ToolApprovalSignatureException error) { throw new ContractException(error.Message); }

        Func<Streaik.Responses.ResponseMcpApprovalRequestItem, Task<IReadOnlyList<UIMessagePart>>>? ConvertReview(
            Streaik.Responses.ResponseRequest native) => nativeResponses && !signingUnavailable && (upstreamReviews || providerMcp is { Length: > 0 })
            ? item => Task.FromResult(hostedApproval.Issue(item))
            : null;

        IAsyncEnumerable<UIMessagePart> WithApprovals(IAsyncEnumerable<UIMessagePart> parts)
        {
            parts = RequestClientToolApprovals(parts, signingUnavailable, call.Ct);
            return signer is null ? parts : signer.SignOutgoing(parts, call.Ct);
        }

        async Task<bool> WriteApprovedActivity(Func<UIMessagePart, Task> write)
        {
            if (hostedApproval.CompletedParts.Count == 0) return false;
            await write(new StartStepUIPart());
            foreach (var part in hostedApproval.CompletedParts) await write(part);
            return true;
        }

        Streaik.Responses.ResponseRequest HostedChatRound() => new() { Stream = true, ToolChoice = request.ToolChoice switch
        {
            null => null,
            "auto" or "required" or "none" => JsonSerializer.SerializeToElement(request.ToolChoice),
            var named => JsonSerializer.SerializeToElement(new { type = "function", name = named })
        } };

        IAsyncEnumerable<UIMessagePart> stream;
        if (hosted is { Length: > 0 } && mistralChat && call.GetChatBridge() is { } mistral)
        {
            var initial = MessagesRequestFrom(call, request, viaChat: true, hosted: hosted, hostedReplay: hostedReplay);
            if (initial.Tools?.Any(tool => GatewayHostedTools.IsReserved(tool.Name)) == true)
                throw new ContractException("Client function names cannot use the Streaik hosted function prefix.");
            var choice = request.ToolChoice switch
            {
                null => null, "auto" or "none" => new MessageToolChoice { Type = request.ToolChoice },
                "required" => new MessageToolChoice { Type = "any" }, var name => new MessageToolChoice { Type = "tool", Name = name }
            };
            await call.WriteVercelStreamAsync(async write =>
            {
                var started = await WriteApprovedActivity(write);
                IReadOnlyList<MessageStreamPart> events = [];
                async Task Observe(HostedToolActivity activity)
                {
                    if (!started) { await write(new StartStepUIPart()); started = true; }
                    await write(HostedActivityPart(activity));
                }
                // Reuse the Chat Completions dialect for both input and output. Only tool calls
                // are projected to Responses for dispatch; reasoning stays in the assistant turn.
                var handled = await GatewayHostedTools.RunAsync(call, HostedChatRound(), hosted, hostedApproval, Observe, readRound: async next =>
                {
                    var round = ProtocolAdapters.PrepareHostedCompletionsRound(initial, next, choice);
                    var native = ChatCompletionsBridge.Request(round, request.MaxOutputTokens, translatePresentation: true, documentUrls: true);
                    if (request.Reasoning is { } effort) native.ReasoningEffort = new(effort);
                    native.AdditionalProperties = ApplyNativeOptions(call, request, native, Web);
                    return await ProtocolAdapters.ReadHostedMessageUpdatesAsync(call,
                        ChatCompletionsBridge.Updates(mistral.StreamChatAsync(native, call.Headers, call.Ct), preserveNativeFields: true, translatePresentation: true),
                        captureChat: parts => events = parts);
                });
                return WithApprovals(HostedMessageParts(call, events, [], handled.Approval, started));
            });
            return;
        }
        else if (hosted is { Length: > 0 } && call.ProviderId == "anthropic" && !call.PreferChatBridge
            && !nativeResponses && call.Get<IMessagesProvider>() is { } anthropic)
        {
            var native = MessagesRequestFrom(call, request, hosted: hosted, hostedReplay: hostedReplay);
            var headers = MessagesHeaders(call, request);
            if (MessagesMcp.WireTools(native).Any(tool => tool.ValueKind == JsonValueKind.Object
                && tool.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String && GatewayHostedTools.IsReserved(name.GetString())))
                throw new ContractException("Client function names cannot use the Streaik hosted function prefix.");
            if (messagesMcp) headers = MessagesMcp.Add(providerMcp!, native, headers);
            if (request.ToolChoice is { } choice)
                native.ToolChoice ??= new() { Type = choice switch { "auto" or "none" => choice, "required" => "any", _ => "tool" },
                    Name = choice is "auto" or "required" or "none" ? null : choice };
            var tools = MessagesMcp.WireTools(native).ToArray();
            var history = native.Messages;
            var system = native.System;
            var round = HostedChatRound();
            await call.WriteVercelStreamAsync(async write =>
            {
                var started = false;
                IReadOnlyList<MessageStreamPart> events = [];
                List<IReadOnlyList<MessageStreamPart>> earlierMcp = [];
                async Task Observe(HostedToolActivity activity)
                {
                    if (!started) { await write(new StartStepUIPart()); started = true; }
                    await write(HostedActivityPart(activity));
                }
                started = await WriteApprovedActivity(write);
                var handled = await GatewayHostedTools.RunAsync(call, round, hosted, hostedApproval, Observe, readRound: async next =>
                {
                    if (McpActivityEvents(events) is { Length: > 0 } activity) earlierMcp.Add(activity);
                    ProtocolAdapters.PrepareHostedChatRound(native, history, system, tools, next);
                    return await ProtocolAdapters.ReadHostedMessagesRoundAsync(call, anthropic, native, headers, messagesMcp,
                        captureChat: parts => events = parts);
                });
                return WithApprovals(HostedMessageParts(call, events, earlierMcp, handled.Approval, started));
            });
            return;
        }
        else if (hosted is { Length: > 0 })
        {
            var native = await ResponseRequestFromAsync(call, request, hosted, providerMcp, hostedReplay);
            if (native.AdditionalProperties?.ContainsKey(GatewayContainerArtifacts.ReplayKey) == true)
                throw new ContractException("Hosted tools cannot be combined with container artifact continuation.", 422);
            await call.WriteVercelStreamAsync(async write =>
            {
                var started = false;
                async Task Observe(HostedToolActivity activity)
                {
                    if (!started) { await write(new StartStepUIPart()); started = true; }
                    await write(HostedActivityPart(activity));
                }
                started = await WriteApprovedActivity(write);
                var handled = await GatewayHostedTools.RunAsync(call, native, hosted, hostedApproval, Observe);
                return WithApprovals(ResponseParts(call, GatewayHostedTools.Events(handled.Events, call.Ct), new GatewayContainerArtifacts.Turn(call),
                    native.Include?.Contains(EncryptedReasoningInclude, StringComparer.Ordinal) == true, ConvertReview(native), handled.Approval, started));
            });
            return;
        }
        else if (nativeResponses && call.Get<IResponsesProvider>() is { } responses)
        {
            var native = await ResponseRequestFromAsync(call, request, providerMcp: providerMcp, hostedReplay: hostedReplay);
            await GatewayContainerArtifacts.PrepareAsync(call, native);
            var artifacts = new GatewayContainerArtifacts.Turn(call);
            if (hostedApproval.CompletedParts.Count > 0)
            {
                await call.WriteVercelStreamAsync(async write =>
                {
                    await WriteApprovedActivity(write);
                    return WithApprovals(ResponseParts(call, responses.StreamResponseAsync(native, call.Headers, call.Ct), artifacts,
                        native.Include?.Contains(EncryptedReasoningInclude, StringComparer.Ordinal) == true, ConvertReview(native), stepStarted: true));
                });
                return;
            }
            stream = ResponseParts(call, responses.StreamResponseAsync(native, call.Headers, call.Ct), artifacts,
                native.Include?.Contains(EncryptedReasoningInclude, StringComparer.Ordinal) == true, ConvertReview(native));
        }
        else if (!call.PreferChatBridge && call.Get<IMessagesProvider>() is { } messages)
        {
            var native = MessagesRequestFrom(call, request);
            var headers = MessagesHeaders(call, request);
            if (messagesMcp)
            {
                headers = MessagesMcp.Add(providerMcp!, native, headers);
                native.ToolChoice ??= request.ToolChoice switch
                {
                    null => null, "auto" => new() { Type = "auto" }, "required" => new() { Type = "any" },
                    "none" => new() { Type = "none" }, var name => new() { Type = "tool", Name = name }
                };
            }
            stream = MessageParts(call, messages.StreamMessageAsync(native, headers, call.Ct));
        }
        else if (call.GetChatBridge() is { } chat)
        {
            var nativeOptions = call.Get<INativeChatOptionsProvider>() is not null;
            if ((!nativeOptions && request.ProviderMetadata?.ContainsKey(call.ProviderId) == true)
                || request.Messages.SelectMany(message => message.Parts).OfType<ToolInvocationPart>().Any(tool => tool.ProviderExecuted == true))
                throw new ContractException("Provider-specific Vercel options require a native adapter.");
            var translatePresentation = call.ProviderId is "cohere" or "mistral";
            var native = ChatCompletionsBridge.Request(MessagesRequestFrom(call, request, viaChat: true), request.MaxOutputTokens,
                translatePresentation, documentUrls: mistralChat);
            if (request.Reasoning is { } effort) native.ReasoningEffort = new ChatCompletionReasoningEffort(effort);
            if (nativeOptions) native.AdditionalProperties = ApplyNativeOptions(call, request, native, Web);
            stream = MessageParts(call, ChatCompletionsBridge.Updates(chat.StreamChatAsync(native, call.Headers, call.Ct), nativeOptions,
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
        await call.WriteVercelStreamAsync(WithApprovals(stream));
    }

    private static UIMessagePart HostedActivityPart(HostedToolActivity activity) => activity switch
    {
        { Error: { } error } => new ToolOutputErrorPart { ToolCallId = activity.CallId, ErrorText = error, ProviderExecuted = true, Dynamic = true },
        { Output: { } output } => new ToolOutputAvailablePart { ToolCallId = activity.CallId, Output = output, ProviderExecuted = true, Dynamic = true },
        _ => new ToolCallPart { ToolCallId = activity.CallId, ToolName = activity.Name, Input = activity.Input, ProviderExecuted = true, Dynamic = true,
            ToolMetadata = new() { [HostedToolActivity.UiMetadataKey] = JsonSerializer.SerializeToElement(true) } }
    };

    private static bool HasNativeClientTools(GatewayCall call, ChatRequest request)
    {
        if (call.PreferChatBridge || Option(request, call.ProviderId, "tools") is not { ValueKind: JsonValueKind.Array } tools)
            return false;
        var responses = call.Get<IResponsesProvider>() is not null;
        if (!responses && call.Get<IMessagesProvider>() is null) return false;
        foreach (var entry in tools.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            var type = entry.TryGetProperty("type", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            if (responses ? type is "function" or "namespace" : type is null or "custom") return true;
            if (responses && type == "tool_search" && entry.TryGetProperty("execution", out var mode)
                && mode.ValueKind == JsonValueKind.String && mode.ValueEquals("client")) return true;
        }
        return false;
    }

    private static async IAsyncEnumerable<UIMessagePart> RequestClientToolApprovals(IAsyncEnumerable<UIMessagePart> stream,
        bool signingUnavailable, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var reviewed = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var part in stream.WithCancellation(cancellationToken))
        {
            if (part is ToolCallPart { ProviderExecuted: false } && signingUnavailable)
                throw new ContractException("Tool approval signing is unavailable for this request.");
            yield return part;
            if (part is ToolCallPart { ProviderExecuted: false } tool && reviewed.Add(tool.ToolCallId))
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

    private static JsonElement? Option(ChatRequest request, string provider, string key, bool ignoreCase = false)
    {
        if (request.ProviderMetadata?.TryGetValue(provider, out var value) != true || value.ValueKind != JsonValueKind.Object)
            return null;
        if (!ignoreCase) return value.TryGetProperty(key, out var setting) ? setting : null;
        return value.EnumerateObject().LastOrDefault(property => property.Name.Equals(key, StringComparison.OrdinalIgnoreCase)).Value
            is { ValueKind: not JsonValueKind.Undefined } option ? option : null;
    }

    private static T? ReadNativeOption<T>(JsonElement? option, JsonSerializerOptions json, string name) where T : class
    {
        try { return option?.Deserialize<T>(json); }
        catch (JsonException) { throw new ContractException($"Invalid provider option '{name}'."); }
    }

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
        const string required = "Client function calls require execution approval and a result before continuing.";
        if (tool.Approval is not { } approval || string.IsNullOrWhiteSpace(approval.Id))
            throw new ContractException(required);
        if (approval.Approved == false && tool.State is "approval-responded" or "output-denied")
            return ("The user declined to run this tool.", true);
        if (approval.Approved != true
            || tool.State is not ("output-available" or "output-error" or "approval-responded")
            || (tool.Output is null && tool.ErrorText is null))
            throw new ContractException(required);
        return (tool.ErrorText ?? ToolOutput(tool.Output), tool.State == "output-error");
    }

    private static FinishUIPart Finish(GatewayCall call, string reason, int? input, int? output,
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
            Usage = new Usage { PromptTokens = input, CompletionTokens = output,
                TotalTokens = input.HasValue && output.HasValue ? input.Value + output.Value : null },
            ProviderMetadata = GatewayPricing.Attach(metadata.Count == 0 ? null : metadata, cost)
        } };
    }
}
