using System.Text.Json;
using Streaik.Providers;
using Streaik.Responses;
using Streaik.Responses.Streaming;
using Streaik.Vercel.Models;

namespace Streaik.Gateway;

internal static partial class VercelAdapter
{
    private static async Task<ResponseRequest> ResponseRequestFromAsync(GatewayCall call, ChatRequest request)
    {
        var input = new List<ResponseInputItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var protectedContainers = GatewayContainerArtifacts.Enabled(call);
        string? containerToken = null, containerId = null;
        foreach (var message in request.Messages)
        {
            var results = new List<ResponseFunctionCallOutputItem>();
            var userParts = new List<ResponseContentPart>();
            foreach (var part in message.Parts)
            {
                switch (part)
                {
                    case TextUIPart text when message.Role == Role.user:
                        userParts.Add(new InputTextPart(text.Text));
                        break;
                    case TextUIPart text:
                        if (message.Role == Role.assistant && ReplayPart<ResponseInputItem>(text.ProviderMetadata,
                            call.ProviderId, NativeItem, ResponseJson.Default) is ResponseInputMessage { Role: ResponseRole.Assistant, Id: { } id } native)
                        {
                            if (seen.Add(id)) input.Add(native);
                        }
                        else input.Add(ProtocolAdapters.ResponseTextMessage(message.Role switch
                            { Role.assistant => ResponseRole.Assistant, Role.system => ResponseRole.System, _ => ResponseRole.User }, text.Text));
                        break;
                    case ReasoningUIPart thought when message.Role == Role.assistant:
                        if (ReplayPart<ResponseInputItem>(thought.ProviderMetadata, call.ProviderId, NativeItem, ResponseJson.Default)
                            is ResponseReasoningItem { Id: { } thoughtId, EncryptedContent: not null } nativeReasoning && seen.Add(thoughtId)) input.Add(nativeReasoning);
                        break;
                    case ToolInvocationPart { ProviderExecuted: true } tool when message.Role == Role.assistant:
                        var item = Replay<ResponseInputItem>(tool.ResultProviderMetadata, call.ProviderId, NativeItem, ResponseJson.Default)
                            ?? Replay<ResponseInputItem>(tool.CallProviderMetadata, call.ProviderId, NativeItem, ResponseJson.Default);
                        if (protectedContainers && item is ResponseCodeInterpreterCallItem interpreter)
                        {
                            if (interpreter.Id != tool.ToolCallId || interpreter.Status != "completed") break;
                            var token = tool.ResultProviderMetadata?.GetValueOrDefault(call.ProviderId)?.GetValueOrDefault(GatewayContainerArtifacts.ReplayKey)
                                ?? tool.CallProviderMetadata?.GetValueOrDefault(call.ProviderId)?.GetValueOrDefault(GatewayContainerArtifacts.ReplayKey);
                            if (token is not JsonElement { ValueKind: JsonValueKind.String } signed) break;
                            containerToken = signed.GetString();
                            containerId = interpreter.ContainerId;
                        }
                        if (item is not null && ProviderTool(item) is ({ } nativeId, var _, true) && nativeId == tool.ToolCallId && seen.Add(nativeId))
                            input.Add(item);
                        break;
                    case FileUIPart file when message.Role == Role.user:
                        ResponseContentPart attachment;
                        if (file.ProviderReference is { Count: > 0 })
                        {
                            if (!protectedContainers || !file.ProviderReference.TryGetValue(call.ProviderId, out var reference))
                                throw new ContractException("This file reference belongs to a different provider.", 422);
                            attachment = await GatewayContainerArtifacts.ReplayFileAsync(call, reference);
                        }
                        else if (ChatAttachments.IsLink(file.Url))
                            attachment = ChatAttachments.IsImage(file.MediaType) ? new InputImagePart { ImageUrl = file.Url }
                                : file.MediaType == "application/pdf" ? new InputFilePart { FileUrl = file.Url }
                                : new InputTextPart(ChatAttachments.LinkText(file.Url));
                        else if (ChatAttachments.InlineData(file.Url, file.MediaType) is not null)
                            attachment = ChatAttachments.IsImage(file.MediaType) ? new InputImagePart { ImageUrl = file.Url }
                                : new InputFilePart { FileData = file.Url, Filename = file.Filename ?? "attachment" };
                        else throw ChatAttachments.Unsupported();
                        userParts.Add(attachment);
                        break;
                    case FileUIPart when message.Role == Role.assistant: break;
                    case ToolInvocationPart tool when message.Role == Role.assistant:
                        var (output, _) = ClientToolResult(tool);
                        input.Add(new ResponseFunctionCallItem { CallId = tool.ToolCallId, Name = ToolName(tool), Arguments = ToolInput(tool.Input).GetRawText() });
                        results.Add(new ResponseFunctionCallOutputItem { CallId = tool.ToolCallId, Output = output });
                        break;
                    case StepStartUIPart or StartStepUIPart or SourceUIPart or SourceDocumentPart or MessageMetadataUIPart: break;
                    default: throw new ContractException($"Vercel part '{part.Type}' has no Responses translation.");
                }
            }
            if (userParts.Count > 0) input.Add(new ResponseInputMessage { Role = ResponseRole.User, Content = new ResponseMessageContent(userParts) });
            input.AddRange(results);
        }

        if (input.Count == 0) throw new ContractException("At least one message is required.");
        var tools = (request.Tools ?? []).DistinctBy(tool => tool.Name).Select(tool => ProtocolAdapters.ResponseFunction(tool.Name,
            tool.Description, tool.InputSchema is null ? null : ToolInput(tool.InputSchema))).ToList();
        if (Option(request, call.ProviderId, "tools") is { ValueKind: JsonValueKind.Array } nativeTools)
            tools.AddRange(nativeTools.Deserialize<List<ResponseToolDefinition>>(ResponseJson.Default) ?? []);
        if (protectedContainers)
        {
            var codeTool = tools.FirstOrDefault(tool => tool.Type == "code_interpreter");
            if (codeTool is not null && containerToken is not null && codeTool.Extra?.ContainsKey(GatewayContainerArtifacts.ReplayKey) != true
                && (codeTool.Extra?.TryGetValue("container", out var containerOption) != true || containerOption.ValueKind == JsonValueKind.Object))
                (codeTool.Extra ??= [])[GatewayContainerArtifacts.ReplayKey] = JsonSerializer.SerializeToElement(containerToken);
            input.RemoveAll(item => item is ResponseCodeInterpreterCallItem code && (codeTool is null || code.ContainerId != containerId));
        }
        var include = Option(request, call.ProviderId, "include") is { ValueKind: JsonValueKind.Array } value
            ? value.Deserialize<List<string>>() ?? [] : new List<string>();
        if (!include.Contains("reasoning.encrypted_content", StringComparer.Ordinal)) include.Add("reasoning.encrypted_content");
        object? textOptions = request.Verbosity is null ? null : new { verbosity = request.Verbosity };
        if (request.ResponseFormat is not null)
            throw new ContractException("This Vercel adapter does not translate response_format.");
        var nativeOptions = call.Get<INativeChatOptionsProvider>() is not null;
        var upstream = new ResponseRequest
        {
            Model = call.NativeModel, Store = false, Stream = true, Input = new ResponseInput(input), Temperature = request.Temperature,
            TopP = request.TopP, MaxOutputTokens = request.MaxOutputTokens, MaxToolCalls = request.MaxToolCalls, Include = include, Text = textOptions,
            Tools = tools.Count == 0 ? null : tools, ToolChoice = request.ToolChoice switch
                { null => null, "auto" or "required" or "none" => request.ToolChoice, { } name => new { type = "function", name } },
            Reasoning = !nativeOptions && Option(request, call.ProviderId, "reasoning") is { ValueKind: JsonValueKind.Object } reasoning
                ? reasoning.Deserialize<Reasoning>(ResponseJson.Default) : null
        };
        if (nativeOptions)
            upstream.AdditionalProperties = ApplyNativeOptions(call, request, upstream, ResponseJson.Default);
        return upstream;
    }

    private static async IAsyncEnumerable<UIMessagePart> ResponseParts(GatewayCall call, ChatRequest request,
        IAsyncEnumerable<ResponseStreamPart> source, GatewayContainerArtifacts.Turn artifacts)
    {
        var text = new HashSet<(string Item, int Index)>();
        var reasoning = new HashSet<string>(StringComparer.Ordinal);
        var calls = new Dictionary<string, string>(StringComparer.Ordinal);
        var sentCalls = new HashSet<string>(StringComparer.Ordinal);
        var refusal = false;
        var step = false;
        string TextId((string Item, int Index) key) => $"{key.Item}/{key.Index}";
        JsonElement Serialize(ResponseInputItem item) => JsonSerializer.SerializeToElement(item, ResponseJson.Default);

        await foreach (var ev in source.WithCancellation(call.Ct))
        {
            artifacts.Observe(ev);
            if (!step && ev is not (ResponseError or ResponseFailed)) { step = true; yield return new StartStepUIPart(); }
            switch (ev)
            {
                case ResponseOutputTextDelta delta:
                    var key = (delta.ItemId, delta.ContentIndex);
                    if (text.Add(key)) yield return new TextStartUIMessageStreamPart { Id = TextId(key) };
                    yield return new TextDeltaUIMessageStreamPart { Id = TextId(key), Delta = delta.Delta }; break;
                case ResponseRefusalDelta delta:
                    refusal = true;
                    var refusalKey = (delta.ItemId, delta.ContentIndex);
                    if (text.Add(refusalKey)) yield return new TextStartUIMessageStreamPart { Id = TextId(refusalKey) };
                    yield return new TextDeltaUIMessageStreamPart { Id = TextId(refusalKey), Delta = delta.Delta }; break;
                case ResponseReasoningSummaryTextDelta delta:
                    if (reasoning.Add(delta.ItemId)) yield return new ReasoningStartUIPart { Id = delta.ItemId };
                    yield return new ReasoningDeltaUIPart { Id = delta.ItemId, Delta = delta.Delta }; break;
                case ResponseOutputItemAdded { Item: { Type: "reasoning", Id: { } thoughtId } } when reasoning.Add(thoughtId):
                    yield return new ReasoningStartUIPart { Id = thoughtId }; break;
                case ResponseOutputItemAdded { Item: { Type: "function_call", Id: { } itemId, CallId: { } callId } item }:
                    calls[itemId] = callId;
                    yield return new ToolCallStreamingStartPart { ToolCallId = callId, ToolName = item.Name!, ProviderExecuted = false, Dynamic = true }; break;
                case ResponseFunctionCallArgumentsDelta delta when calls.TryGetValue(delta.ItemId, out var handle):
                    yield return new ToolCallDeltaPart { ToolCallId = handle, InputTextDelta = delta.Delta }; break;
                case ResponseOutputItemDone { Item: { Type: "function_call", CallId: { } handle } item } when sentCalls.Add(handle):
                    yield return new ToolCallPart { ToolCallId = handle, ToolName = item.Name!, Dynamic = true, ProviderExecuted = false,
                        Input = item.Arguments is { ValueKind: JsonValueKind.String } args ? MessagesHistory.ParseArguments(args.GetString()!) : ToolInput(item.Arguments) };
                    break;
                case ResponseError or ResponseFailed: throw new IOException("Responses returned a stream error.");
            }

            var result = ev switch { ResponseCompleted completed => completed.Response, ResponseIncompleteEvent incomplete => incomplete.Response, _ => null };
            if (result is null) continue;
            foreach (var item in result.OutputItems)
            {
                switch (item)
                {
                    case ResponseInputMessage { Id: { } id } message:
                        var native = Serialize(message);
                        var parts = message.Content.Parts ?? (message.Content.Text is { } plain ? [new OutputTextPart(plain)] : []);
                        for (var index = 0; index < parts.Count; index++)
                        {
                            if (parts[index] is not OutputTextPart part) continue;
                            var key = (id, index);
                            if (!text.Remove(key))
                            {
                                yield return new TextStartUIMessageStreamPart { Id = TextId(key) };
                                yield return new TextDeltaUIMessageStreamPart { Id = TextId(key), Delta = part.Text };
                            }
                            yield return new TextEndUIMessageStreamPart { Id = TextId(key), ProviderMetadata = new()
                                { [call.ProviderId] = new Dictionary<string, object> { [NativeItem] = native } } };
                        }
                        break;
                    case ResponseImageGenerationCallItem { Result.Length: > 0 } picture:
                        var mime = picture.AdditionalProperties?.GetValueOrDefault("output_format") is JsonElement { ValueKind: JsonValueKind.String } format
                            ? format.GetString() switch { "jpeg" => "image/jpeg", "webp" => "image/webp", _ => "image/png" } : "image/png";
                        yield return new FileUIPart { MediaType = mime, Url = $"data:{mime};base64,{picture.Result}" };
                        break;
                    case ResponseReasoningItem { Id: { } id } thought:
                        if (!reasoning.Remove(id))
                        {
                            yield return new ReasoningStartUIPart { Id = id };
                            var summary = string.Join('\n', thought.Summary.Select(part => part.Text));
                            if (summary.Length > 0) yield return new ReasoningDeltaUIPart { Id = id, Delta = summary };
                        }
                        yield return new ReasoningEndUIPart { Id = id, ProviderMetadata = new() { [call.ProviderId] = new() { [NativeItem] = Serialize(thought) } } };
                        break;
                    case ResponseFunctionCallItem function when sentCalls.Add(function.CallId):
                        yield return new ToolCallPart { ToolCallId = function.CallId, ToolName = function.Name, Dynamic = true,
                            ProviderExecuted = false, Input = MessagesHistory.ParseArguments(function.Arguments) }; break;
                    case ResponseMcpApprovalRequestItem:
                        yield return new ErrorUIPart { ErrorText = "This provider tool requires execution approval, which API Chat does not support." };
                        yield break;
                    default:
                        if (ProviderTool(item) is ({ } handle, { } toolName, var _))
                        {
                            var raw = Serialize(item);
                            var metadata = ToolMetadata(call.ProviderId, NativeItem, raw);
                            if (item is ResponseCodeInterpreterCallItem { Status: "completed", ContainerId: { } container }
                                && GatewayContainerArtifacts.Enabled(call))
                                metadata[call.ProviderId]![GatewayContainerArtifacts.ReplayKey] = GatewayContainerArtifacts.SignContainer(call, container);
                            yield return new ToolCallPart { ToolCallId = handle, ToolName = toolName, ProviderExecuted = true, Dynamic = true,
                                Input = item is ResponseMcpCallItem mcp ? MessagesHistory.ParseArguments(mcp.Arguments ?? "{}") : ToolInput(null), ProviderMetadata = metadata };
                            yield return new ToolOutputAvailablePart { ToolCallId = handle, Output = raw, ProviderExecuted = true, Dynamic = true,
                                ProviderMetadata = metadata };
                        }
                        break;
                }
            }
            foreach (var key in text) yield return new TextEndUIMessageStreamPart { Id = TextId(key) };
            foreach (var id in reasoning) yield return new ReasoningEndUIPart { Id = id };
            await foreach (var artifact in artifacts.DownloadAsync(result))
            {
                yield return new ToolCallPart { ToolCallId = artifact.Id, ToolName = GatewayContainerArtifacts.ToolName,
                    Input = artifact.Input, ProviderExecuted = true, Dynamic = true };
                yield return new ToolOutputAvailablePart { ToolCallId = artifact.Id, Output = artifact.Output,
                    ProviderExecuted = true, Dynamic = true };
            }
            var usage = result.NormalizedUsage;
            var filtered = result.IncompleteDetails is { ValueKind: JsonValueKind.Object } details
                && details.TryGetProperty("reason", out var reason) && reason.GetString() == "content_filter";
            yield return Finish(call, request, refusal || filtered ? "content-filter" : result.Status == "incomplete" ? "length"
                : sentCalls.Count > 0 ? "tool-calls" : "stop", usage?.InputTokens, usage?.OutputTokens, call.ProviderId,
                result.Usage, result.AdditionalProperties, result.CreatedAt,
                GatewayPricing.Cost(result) ?? await GatewayPricing.EstimateAsync(call, result.Model, result.Usage));
            yield break;
        }
        throw new IOException("Responses ended without a terminal event.");
    }

    // MCP calls are shown as provider-executed but are not submitted again on the next turn.
    private static (string? Id, string? Name, bool Replayable) ProviderTool(ResponseInputItem item) => item switch
    {
        ResponseWebSearchCallItem web => (web.Id, "web_search", web.Status == "completed"),
        ResponseFileSearchCallItem file => (file.Id, "file_search", file.Status == "completed"),
        ResponseCodeInterpreterCallItem code => (code.Id, "code_interpreter", code.Status == "completed"),
        ResponseMcpCallItem mcp => (mcp.Id, mcp.Name, false),
        _ => (null, null, false)
    };
}
