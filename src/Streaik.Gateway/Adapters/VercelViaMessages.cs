using System.Text;
using System.Text.Json;
using Streaik.Messages;
using Streaik.Vercel.Models;

namespace Streaik.Gateway;

internal static partial class VercelAdapter
{
    private const string ClientToolSearch = "client_tool_search";
    private const string AnthropicToolSearchBeta = "advanced-tool-use-2025-11-20";
    private static readonly HashSet<string> OwnedMessagesFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "model", "messages", "system", "stream", "max_tokens", "tools", "tool_choice", "additionalProperties",
        "authorization", "api_key", "apiKey", "x-api-key", "headers", "anthropic-version", "anthropic-beta", "anthropic"
    };

    private static MessagesRequest MessagesRequestFrom(GatewayCall call, ChatRequest request, bool viaChat = false)
    {
        if (request.ResponseFormat is not null || request.Verbosity is not null || request.MaxToolCalls is not null)
            throw new ContractException("This Vercel option has no Messages translation.");
        var history = new MessagesHistory();
        var deferred = !viaChat && call.ProviderId == "anthropic" && request.Tools?.Any(tool => tool.Name == ClientToolSearch) == true
            ? request.Tools.Where(tool => tool.DeferLoading == true && tool.Name != ClientToolSearch).Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal)
            : [];
        foreach (var message in request.Messages)
        {
            var results = new List<(string Id, MessagesContent Output, bool IsError)>();
            foreach (var part in message.Parts)
                switch (part)
                {
                    case TextUIPart text: history.Text(message.Role.ToString(), text.Text); break;
                    case FileUIPart file when message.Role == Role.user:
                        history.Add("user", ChatAttachments.ToMessages(file, viaChat)); break;
                    case FileUIPart when message.Role == Role.assistant: break;
                    case ReasoningUIPart reasoning when message.Role == Role.assistant:
                        if (ReplayPart<MessageContentBlock>(reasoning.ProviderMetadata, call.ProviderId, NativeBlock, MessagesJson.Default)
                            is { Type: "thinking" or "redacted_thinking" } block) history.Add("assistant", block);
                        break;
                    case ToolInvocationPart { ProviderExecuted: true } tool when message.Role == Role.assistant:
                        var native = Replay<MessageContentBlock>(tool.CallProviderMetadata, call.ProviderId, NativeBlock, MessagesJson.Default);
                        if (native is { Type: "server_tool_use" or "mcp_tool_use" } && native.Id == tool.ToolCallId)
                        {
                            history.Add("assistant", native);
                            var result = Replay<MessageContentBlock>(tool.ResultProviderMetadata, call.ProviderId, NativeBlock, MessagesJson.Default);
                            if (result?.ToolUseId == tool.ToolCallId && IsServerResult(result.Type)) history.Add("assistant", result);
                        }
                        break;
                    case ToolInvocationPart tool when message.Role == Role.assistant:
                        var (output, isError) = ClientToolResult(tool);
                        var name = ToolName(tool);
                        history.Tool(tool.ToolCallId, name, ToolInput(tool.Input));
                        var references = name == ClientToolSearch && !isError && deferred.Count > 0 ? ToolSearchReferences(output, deferred) : null;
                        results.Add((tool.ToolCallId, references ?? new MessagesContent(output), isError));
                        break;
                    case StepStartUIPart or StartStepUIPart or SourceUIPart or SourceDocumentPart or MessageMetadataUIPart: break;
                    default: throw new ContractException($"Vercel part '{part.Type}' has no Messages translation.");
                }
            foreach (var (id, output, isError) in results) history.Result(id, output, isError);
        }
        var anthropic = !viaChat && call.ProviderId == "anthropic";
        var maxTokens = request.MaxOutputTokens;
        JsonElement? options = null;
        if (anthropic)
        {
            if (request.ProviderMetadata?.TryGetValue(call.ProviderId, out var selected) == true)
            {
                if (selected.ValueKind != JsonValueKind.Object) throw new ContractException("Provider-specific chat options must be an object.");
                options = selected;
                if (selected.TryGetProperty("max_tokens", out var limit))
                {
                    if (limit.ValueKind != JsonValueKind.Number || !limit.TryGetInt32(out var value) || value <= 0)
                        throw new ContractException("Anthropic requires a positive max_tokens value.");
                    maxTokens = value;
                }
            }
            StreaikGateway.ValidateAnthropicMessageInputs(call, maxTokens);
        }
        var nativeRequest = history.Build(call.NativeModel, maxTokens, temperature: null, topP: null, stream: true);
        var tools = (request.Tools ?? []).DistinctBy(tool => tool.Name).Select(tool => new MessageToolDefinition
        { Name = tool.Name, Description = tool.Description, InputSchema = ToolInput(tool.InputSchema ?? new ToolSchema { Type = "object" }),
            DeferLoading = call.ProviderId == "anthropic" && tool.Name == ClientToolSearch ? null : tool.DeferLoading,
            AllowedCallers = tool.AllowedCallers?.ToList() }).ToList();
        if (!viaChat && !anthropic && Option(request, call.ProviderId, "tools") is { ValueKind: JsonValueKind.Array } nativeTools)
            tools.AddRange(nativeTools.Deserialize<List<MessageToolDefinition>>(MessagesJson.Default) ?? []);
        var toolsAllowed = request.ToolChoice != "none" || deferred.Count > 0;
        var hasTools = toolsAllowed && tools.Count != 0;
        if (options is { } supplied)
        {
            // Copy the selected provider's JSON without deserializing individual
            // options. Routing, translated history and tool policy still belong here.
            var extensions = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in supplied.EnumerateObject())
                if (!OwnedMessagesFields.Contains(property.Name)) extensions[property.Name] = property.Value;
            if (toolsAllowed && supplied.TryGetProperty("tools", out var rawTools))
            {
                if (rawTools.ValueKind != JsonValueKind.Array) throw new ContractException("Invalid provider option 'tools'.");
                var names = tools.Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);
                var merged = tools.Select(tool => JsonSerializer.SerializeToElement(tool, MessagesJson.Default)).ToList();
                merged.AddRange(rawTools.EnumerateArray().Where(tool => tool.ValueKind != JsonValueKind.Object
                    || !tool.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String || !names.Contains(name.GetString())));
                extensions["tools"] = JsonSerializer.SerializeToElement(merged);
                hasTools = merged.Count != 0;
            }
            nativeRequest.AdditionalProperties = extensions;
        }
        // Extension data adds properties; it does not replace typed ones.
        if (hasTools && nativeRequest.AdditionalProperties?.ContainsKey("tools") != true) nativeRequest.Tools = tools;
        if (hasTools && request.ToolChoice is { } choice)
            nativeRequest.ToolChoice = choice switch
            { "auto" => new MessageToolChoice(), "required" => new MessageToolChoice { Type = "any" },
                "none" => new MessageToolChoice { Type = "none" },
                _ => new MessageToolChoice { Type = "tool", Name = choice } };
        if (anthropic && request.Reasoning is { } anthropicEffort)
        {
            var thinking = Option(request, call.ProviderId, "thinking", ignoreCase: true);
            if (thinking is null) nativeRequest.Thinking = new MessagesThinkingConfig { Type = "adaptive" };
            var adaptive = thinking is null || (thinking is { ValueKind: JsonValueKind.Object } config
                && config.TryGetProperty("type", out var mode) && mode.ValueKind == JsonValueKind.String && mode.ValueEquals("adaptive"));
            if (adaptive && Option(request, call.ProviderId, "output_config", ignoreCase: true) is null)
                nativeRequest.OutputConfig = new MessagesOutputConfig { Effort = anthropicEffort };
        }
        else if (!viaChat && !anthropic)
        {
            var thinking = Option(request, call.ProviderId, "thinking");
            var config = Option(request, call.ProviderId, "output_config");
            nativeRequest.Thinking = ReadNativeOption<MessagesThinkingConfig>(thinking, MessagesJson.Default, "thinking");
            nativeRequest.OutputConfig = ReadNativeOption<MessagesOutputConfig>(config, MessagesJson.Default, "output_config");
            if (request.Reasoning is { } effort)
            {
                if (thinking is null) nativeRequest.Thinking = new MessagesThinkingConfig { Type = "adaptive" };
                // Explicit disabled or budget-based thinking owns the reasoning behavior.
                if (nativeRequest.Thinking?.Type == "adaptive" && (config is null || (config is { ValueKind: JsonValueKind.Object } outputOptions
                    && !outputOptions.EnumerateObject().Any(property => property.Name.Equals("effort", StringComparison.OrdinalIgnoreCase)))))
                    (nativeRequest.OutputConfig ??= new MessagesOutputConfig()).Effort = effort;
            }
        }
        return nativeRequest;
    }

    private static MessagesContent? ToolSearchReferences(string output, HashSet<string> deferred)
    {
        try
        {
            using var json = JsonDocument.Parse(output);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || root.TryGetProperty("isError", out var failed) && failed.ValueKind == JsonValueKind.True
                || !root.TryGetProperty("structuredContent", out var content) || content.ValueKind != JsonValueKind.Object
                || !content.TryGetProperty("selectedTools", out var tools) || tools.ValueKind != JsonValueKind.Array) return null;

            var names = new HashSet<string>(StringComparer.Ordinal);
            var references = new List<MessageContentBlock>();
            foreach (var tool in tools.EnumerateArray())
            {
                if (tool.ValueKind == JsonValueKind.Object && tool.TryGetProperty("name", out var name)
                    && name.ValueKind == JsonValueKind.String && name.GetString() is { } value && deferred.Contains(value) && names.Add(value))
                    references.Add(new MessageContentBlock { Type = "tool_reference", ToolName = value });
                if (references.Count == 10) break;
            }
            return references.Count > 0 ? new MessagesContent(references) : null;
        }
        catch (JsonException) { return null; }
    }

    private static IReadOnlyDictionary<string, string> MessagesHeaders(GatewayCall call, ChatRequest request)
    {
        if (call.ProviderId != "anthropic" || request.Tools?.Any(tool => tool.Name == ClientToolSearch) != true
            || !request.Tools.DistinctBy(tool => tool.Name).Any(tool => tool.Name != ClientToolSearch && tool.DeferLoading == true)) return call.Headers;
        const string header = "anthropic-beta";
        var existing = call.Headers.GetValueOrDefault(header);
        if (existing?.Split(',', StringSplitOptions.TrimEntries).Contains(AnthropicToolSearchBeta) == true) return call.Headers;
        return new Dictionary<string, string>(call.Headers, StringComparer.OrdinalIgnoreCase)
        { [header] = string.IsNullOrWhiteSpace(existing) ? AnthropicToolSearchBeta : $"{existing},{AnthropicToolSearchBeta}" };
    }

    private static async IAsyncEnumerable<UIMessagePart> MessageParts(GatewayCall call, IAsyncEnumerable<MessageStreamPart> source)
    {
        var blocks = new Dictionary<int, (MessageContentBlock Block, StringBuilder Arguments)>();
        var usage = new MessagesUsage();
        var id = "";
        string? upstreamModel = null;
        string? stop = null;
        var metadata = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        HashSet<string>? sourceUrls = call.ProviderId == "cohere" ? new(StringComparer.Ordinal) : null;
        var preserveNativeFields = call.Get<Streaik.Providers.INativeChatOptionsProvider>() is not null;
        string PartId(int index) => $"{id}/{index}";
        JsonElement WireBlock(MessageContentBlock block) => JsonSerializer.SerializeToElement(block, MessagesJson.Default);
        Dictionary<string, Dictionary<string, object>?> Meta(MessageContentBlock block) => ToolMetadata(call.ProviderId, NativeBlock, WireBlock(block));
        await foreach (var part in source.WithCancellation(call.Ct))
        {
            var index = part.Index ?? -1;
            var partId = PartId(index);
            if (part.Metadata is not null) foreach (var (key, value) in part.Metadata) metadata[key] = value;
            if (part.AdditionalProperties is { } additional)
                foreach (var (key, value) in additional)
                    if (key == "providerMetadata" || preserveNativeFields) metadata[key] = value;
            switch (part)
            {
                case { Type: "message_start", Message: { } message }:
                    id = message.Id ?? throw new IOException("Missing upstream message ID.");
                    upstreamModel = message.Model;
                    MessagesHistory.MergeUsage(usage, message.Usage);
                    if (message.AdditionalProperties is not null)
                        foreach (var (key, value) in message.AdditionalProperties) metadata[key] = value;
                    yield return new StartStepUIPart(); break;
                case { Type: "content_block_start", ContentBlock: { } block }:
                    blocks.Add(index, (block, new StringBuilder()));
                    if (block.Type == "text")
                    {
                        yield return new TextStartUIMessageStreamPart { Id = partId };
                        if (!string.IsNullOrEmpty(block.Text)) yield return new TextDeltaUIMessageStreamPart { Id = partId, Delta = block.Text };
                    }
                    else if (block.Type is "thinking" or "redacted_thinking")
                    {
                        yield return new ReasoningStartUIPart { Id = partId };
                        if (!string.IsNullOrEmpty(block.Thinking)) yield return new ReasoningDeltaUIPart { Id = partId, Delta = block.Thinking };
                    }
                    else if (IsTool(block.Type)) yield return new ToolCallStreamingStartPart
                    { ToolCallId = block.Id!, ToolName = block.Name!, ProviderExecuted = block.Type != "tool_use", Dynamic = true };
                    else if (!IsServerResult(block.Type)) throw new IOException("Unsupported Messages content block for Vercel chat.");
                    break;
                case { Type: "content_block_delta", Delta: { } delta } when blocks.TryGetValue(index, out var current):
                    if (delta.Text is { } text) yield return new TextDeltaUIMessageStreamPart { Id = partId, Delta = text };
                    if (delta.Thinking is { } thought)
                    { current.Block.Thinking += thought; yield return new ReasoningDeltaUIPart { Id = partId, Delta = thought }; }
                    if (delta.Signature is { } signature) current.Block.Signature += signature;
                    if (delta.PartialJson is { } args)
                    { current.Arguments.Append(args); yield return new ToolCallDeltaPart { ToolCallId = current.Block.Id!, InputTextDelta = args }; }
                    if (delta.Citation is { } citation)
                    {
                        (current.Block.Citations ??= []).Add(citation);
                        if (citation.Url is { } url && sourceUrls?.Add(url) == true)
                            yield return new SourceUIPart { SourceId = $"{id}/source/{sourceUrls.Count}", Url = url, Title = citation.Title };
                    }
                    break;
                case { Type: "content_block_stop" } when blocks.TryGetValue(index, out var completed):
                    var output = completed.Block;
                    if (output.Type == "text") yield return new TextEndUIMessageStreamPart { Id = partId,
                        Citations = output.Citations?.Select(citation => JsonSerializer.SerializeToElement(citation, MessagesJson.Default)).ToList() };
                    else if (output.Type is "thinking" or "redacted_thinking") yield return new ReasoningEndUIPart { Id = partId,
                        ProviderMetadata = new() { [call.ProviderId] = new() { [NativeBlock] = WireBlock(output) } } };
                    else if (IsTool(output.Type))
                    {
                        if (completed.Arguments.Length > 0) output.Input = MessagesHistory.ParseArguments(completed.Arguments.ToString());
                        yield return new ToolCallPart { ToolCallId = output.Id!, ToolName = output.Name!, Input = ToolInput(output.Input),
                            ProviderExecuted = output.Type != "tool_use", Dynamic = true, ProviderMetadata = output.Type == "tool_use" ? null : Meta(output) };
                    }
                    else if (IsServerResult(output.Type) && output.ToolUseId is { } toolId)
                        yield return new ToolOutputAvailablePart { ToolCallId = toolId, Output = WireBlock(output),
                            ProviderExecuted = true, Dynamic = true, ProviderMetadata = Meta(output) };
                    break;
                case { Type: "message_delta" }:
                    stop = part.Delta?.StopReason ?? stop;
                    MessagesHistory.MergeUsage(usage, part.Usage); break;
                case { Type: "message_stop" }:
                    yield return Finish(call, stop switch { "tool_use" => "tool-calls", "max_tokens" or "model_context_window_exceeded" => "length",
                        "refusal" => "content-filter", "pause_turn" => "other", _ => "stop" }, MessagesHistory.TotalInput(usage), usage.OutputTokens,
                        call.ProviderId, JsonSerializer.SerializeToElement(usage, MessagesJson.Default), metadata,
                        cost: await GatewayPricing.EstimateAsync(call, upstreamModel, usage));
                    yield break;
                case { Type: "error" }: throw new UpstreamStreamException(part.Error?.Message);
            }
        }
        throw new IOException("Messages ended without message_stop.");
    }

    private static bool IsTool(string type) => type is "tool_use" or "server_tool_use" or "mcp_tool_use";
    private static bool IsServerResult(string type) => type != "tool_result" && type.EndsWith("_tool_result", StringComparison.Ordinal);
}
