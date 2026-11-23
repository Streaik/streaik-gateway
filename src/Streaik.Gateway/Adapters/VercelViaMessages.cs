using System.Text;
using System.Text.Json;
using Streaik.Messages;
using Streaik.Vercel.Models;

namespace Streaik.Gateway;

internal static partial class VercelAdapter
{
    private static MessagesRequest MessagesRequestFrom(GatewayCall call, ChatRequest request, bool viaChat = false)
    {
        if (request.ResponseFormat is not null || request.Verbosity is not null || request.MaxToolCalls is not null)
            throw new ContractException("This Vercel option has no Messages translation.");
        var history = new MessagesHistory();
        foreach (var message in request.Messages)
        {
            var results = new List<(string Id, string Output, bool IsError)>();
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
                        history.Tool(tool.ToolCallId, ToolName(tool), ToolInput(tool.Input));
                        results.Add((tool.ToolCallId, output, isError));
                        break;
                    case StepStartUIPart or StartStepUIPart or SourceUIPart or SourceDocumentPart or MessageMetadataUIPart: break;
                    default: throw new ContractException($"Vercel part '{part.Type}' has no Messages translation.");
                }
            foreach (var (id, output, isError) in results) history.Result(id, output, isError);
        }
        var nativeRequest = history.Build(call.NativeModel, request.MaxOutputTokens, request.Temperature, request.TopP, true);
        var tools = (request.Tools ?? []).DistinctBy(tool => tool.Name).Select(tool => new MessageToolDefinition
        { Name = tool.Name, Description = tool.Description, InputSchema = ToolInput(tool.InputSchema ?? new ToolSchema { Type = "object" }),
            DeferLoading = tool.DeferLoading, AllowedCallers = tool.AllowedCallers?.ToList() }).ToList();
        if (!viaChat && Option(request, call.ProviderId, "tools") is { ValueKind: JsonValueKind.Array } nativeTools)
            tools.AddRange(nativeTools.Deserialize<List<MessageToolDefinition>>(MessagesJson.Default) ?? []);
        if (request.ToolChoice != "none" && tools.Count != 0) nativeRequest.Tools = tools;
        if (nativeRequest.Tools is not null && request.ToolChoice is { } choice)
            nativeRequest.ToolChoice = choice switch
            { "auto" => new MessageToolChoice(), "required" => new MessageToolChoice { Type = "any" },
                _ => new MessageToolChoice { Type = "tool", Name = choice } };
        if (!viaChat && Option(request, call.ProviderId, "thinking") is { ValueKind: JsonValueKind.Object } thinking)
            nativeRequest.Thinking = thinking.Deserialize<MessagesThinkingConfig>(MessagesJson.Default);
        return nativeRequest;
    }

    private static async IAsyncEnumerable<UIMessagePart> MessageParts(GatewayCall call, ChatRequest request, IAsyncEnumerable<MessageStreamPart> source)
    {
        var blocks = new Dictionary<int, (MessageContentBlock Block, StringBuilder Arguments)>();
        var usage = new MessagesUsage();
        var id = "";
        string? upstreamModel = null;
        string? stop = null;
        var metadata = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
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
                    if (delta.Citation is { } citation) (current.Block.Citations ??= []).Add(citation);
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
                    yield return Finish(call, request, stop switch { "tool_use" => "tool-calls", "max_tokens" or "model_context_window_exceeded" => "length",
                        "refusal" => "content-filter", "pause_turn" => "other", _ => "stop" }, MessagesHistory.TotalInput(usage), usage.OutputTokens,
                        call.ProviderId, JsonSerializer.SerializeToElement(usage, MessagesJson.Default), metadata,
                        cost: GatewayPricing.Estimate(call, upstreamModel, usage));
                    yield break;
                case { Type: "error" }: throw new IOException("Messages returned a stream error.");
            }
        }
        throw new IOException("Messages ended without message_stop.");
    }

    private static bool IsTool(string type) => type is "tool_use" or "server_tool_use" or "mcp_tool_use";
    private static bool IsServerResult(string type) => type != "tool_result" && type.EndsWith("_tool_result", StringComparison.Ordinal);
}
