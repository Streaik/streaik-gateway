using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Streaik.Providers;
using Streaik.Responses;
using Streaik.Responses.Streaming;
using Streaik.Tools;
using Streaik.Vercel.Models;

namespace Streaik.Gateway;

internal static partial class VercelAdapter
{
    private const string EncryptedReasoningInclude = "reasoning.encrypted_content";

    private static async Task<ResponseRequest> ResponseRequestFromAsync(GatewayCall call, ChatRequest request, HostedToolDefinition[]? hosted = null)
    {
        // An explicit OpenAI include array is authoritative, including an empty opt-out.
        var include = Option(request, call.ProviderId, "include") is { ValueKind: JsonValueKind.Array } value
            ? value.Deserialize<List<string>>() ?? [] : [EncryptedReasoningInclude];
        var includeEncryptedReasoning = include.Contains(EncryptedReasoningInclude, StringComparer.Ordinal);
        if (call.ProviderId != "openai" && !includeEncryptedReasoning)
        {
            include.Add(EncryptedReasoningInclude);
            includeEncryptedReasoning = true;
        }
        var tools = ResponseTools(request, call.ProviderId);
        var clientSearch = call.ProviderId == "openai" && tools.Any(tool => tool.Type == "tool_search" && ToolField(tool, "execution") == "client");
        var input = new List<ResponseInputItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var protectedContainers = GatewayContainerArtifacts.Enabled(call);
        string? containerToken = null, containerId = null;
        // CAMBAI translates the last user turn rather than sending conversation history to the translation API.
        var translation = call.ProviderId == "cambai";
        var messages = translation ? request.Messages.Where(message => message.Role == Role.user).TakeLast(1) : request.Messages;
        foreach (var message in messages)
        {
            var results = new List<ResponseFunctionCallOutputItem>();
            var userParts = new List<ResponseContentPart>();
            foreach (var part in message.Parts)
            {
                if (translation && part is not TextUIPart) continue;
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
                        if (includeEncryptedReasoning && ReplayPart<ResponseInputItem>(thought.ProviderMetadata, call.ProviderId, NativeItem, ResponseJson.Default)
                            is ResponseReasoningItem { Id: { } thoughtId, EncryptedContent: not null } nativeReasoning && seen.Add(thoughtId)) input.Add(nativeReasoning);
                        break;
                    case ToolInvocationPart tool when UpstreamMcpReview.IsReview(tool):
                        if (message.Role != Role.assistant || !seen.Add(tool.ToolCallId)
                            || call.Context.RequestServices.GetService<HostedToolsEnabled>() is null)
                            throw new ContractException("Invalid converted MCP result review.", 403);
                        input.AddRange(UpstreamMcpReview.Resolve(call, request, tools, tool, hosted));
                        break;
                    case ToolInvocationPart { ProviderExecuted: true } tool when message.Role == Role.assistant:
                        if (call.ProviderId == "openai" && (tool.ToolName == "tool_search" || tool.Type == "tool-tool_search"))
                        {
                            var searchCall = Replay<ResponseInputItem>(tool.CallProviderMetadata, call.ProviderId, NativeItem, ResponseJson.Default);
                            var searchOutput = Replay<ResponseInputItem>(tool.ResultProviderMetadata, call.ProviderId, NativeItem, ResponseJson.Default);
                            if (tool.State == "output-available"
                                && searchCall is ResponseToolSearchCallItem { Id: { Length: > 0 } callItemId, Execution: "server", Status: "completed" } search
                                && searchOutput is ResponseToolSearchOutputItem { Id: { Length: > 0 } outputItemId, Execution: "server", Status: "completed" } outputItem
                                && tool.ToolCallId == (search.CallId ?? callItemId) && search.CallId == outputItem.CallId
                                && callItemId != outputItemId && !seen.Contains(callItemId) && !seen.Contains(outputItemId))
                            {
                                seen.Add(callItemId);
                                seen.Add(outputItemId);
                                input.Add(search);
                                input.Add(outputItem);
                            }
                            break;
                        }
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
                        if (hosted is { Length: > 0 } && GatewayHostedTools.IsReserved(ToolName(tool)))
                            throw new ContractException("Hosted function history must be generated by the gateway.");
                        var (output, _) = ClientToolResult(tool);
                        if (call.ProviderId == "openai" && ToolName(tool) == "tool_search"
                            && Replay<ResponseInputItem>(tool.CallProviderMetadata, call.ProviderId, NativeItem, ResponseJson.Default)
                                is ResponseToolSearchCallItem { Id: { Length: > 0 } searchId, Execution: "client", Status: "completed", CallId: { Length: > 0 } searchHandle } clientCall
                            && searchHandle == tool.ToolCallId)
                        {
                            if (seen.Add(searchId))
                            {
                                input.Add(clientCall);
                                input.Add(new ResponseToolSearchOutputItem { Execution = "client", Status = "completed", CallId = searchHandle,
                                    Tools = tool.Approval?.Approved == true && tool.State != "output-error" ? SelectedSearchTools(tools, output) : [] });
                            }
                            break;
                        }
                        input.Add(new ResponseFunctionCallItem { CallId = tool.ToolCallId, Name = ToolName(tool),
                            Namespace = tool.ToolMetadata?.TryGetValue("namespace", out var space) == true && space.ValueKind == JsonValueKind.String
                                ? space.GetString() : null, Arguments = ToolInput(tool.Input).GetRawText() });
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
        if (clientSearch) tools = FilterSearchCatalog(tools, function => !DeferredFunction(function));
        if (protectedContainers)
        {
            var codeTool = tools.FirstOrDefault(tool => tool.Type == "code_interpreter");
            if (codeTool is not null && containerToken is not null && codeTool.Extra?.ContainsKey(GatewayContainerArtifacts.ReplayKey) != true
                && (codeTool.Extra?.TryGetValue("container", out var containerOption) != true || containerOption.ValueKind == JsonValueKind.Object))
                (codeTool.Extra ??= [])[GatewayContainerArtifacts.ReplayKey] = JsonSerializer.SerializeToElement(containerToken);
            input.RemoveAll(item => item is ResponseCodeInterpreterCallItem code && (codeTool is null || code.ContainerId != containerId));
        }
        object? textOptions = request.Verbosity is null ? null : new { verbosity = request.Verbosity };
        if (request.ResponseFormat is not null)
            throw new ContractException("This Vercel adapter does not translate response_format.");
        var nativeOptions = call.Get<INativeChatOptionsProvider>() is not null;
        var rawReasoning = Option(request, call.ProviderId, "reasoning", ignoreCase: nativeOptions);
        var upstream = new ResponseRequest
        {
            Model = call.NativeModel, Store = false, Stream = true, Input = new ResponseInput(input),
            MaxOutputTokens = request.MaxOutputTokens, MaxToolCalls = request.MaxToolCalls, Include = include, Text = textOptions,
            Tools = tools.Count == 0 ? null : tools, ToolChoice = request.ToolChoice switch
                { null => null, "auto" or "required" or "none" => request.ToolChoice, { } name => new { type = "function", name } },
            Reasoning = !nativeOptions && rawReasoning is { ValueKind: JsonValueKind.Object }
                ? ReadNativeOption<Reasoning>(rawReasoning, ResponseJson.Default, "reasoning") : null
        };
        if (nativeOptions)
            upstream.AdditionalProperties = ApplyNativeOptions(call, request, upstream, ResponseJson.Default);
        if (request.Reasoning is { } effort && (rawReasoning is null || (rawReasoning is { ValueKind: JsonValueKind.Object } config
            && !config.EnumerateObject().Any(property => property.Name.Equals("effort", StringComparison.OrdinalIgnoreCase)))))
            (upstream.Reasoning ??= new Reasoning()).Effort = effort;
        return upstream;
    }

    private static List<ResponseToolDefinition> ResponseTools(ChatRequest request, string provider)
    {
        var tools = (request.Tools ?? []).DistinctBy(tool => tool.Name).Select(tool =>
        {
            var function = ProtocolAdapters.ResponseFunction(tool.Name, tool.Description, tool.InputSchema is null ? null : ToolInput(tool.InputSchema));
            var callers = tool.AllowedCallers?.Where(caller => caller is "direct" or "programmatic").Distinct().ToArray();
            if (callers is { Length: > 0 }) (function.Extra ??= [])["allowed_callers"] = JsonSerializer.SerializeToElement(callers);
            if (tool.DeferLoading == true) (function.Extra ??= [])["defer_loading"] = JsonSerializer.SerializeToElement(true);
            return function;
        }).ToList();
        if (Option(request, provider, "tools") is { ValueKind: JsonValueKind.Array } nativeTools)
            tools.AddRange(nativeTools.Deserialize<List<ResponseToolDefinition>>(ResponseJson.Default) ?? []);
        return tools;
    }

    private static string? ToolField(ResponseToolDefinition tool, string key) =>
        tool.Extra?.GetValueOrDefault(key) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    private static bool DeferredFunction(ResponseToolDefinition tool) => tool.Type == "function"
        && tool.Extra?.GetValueOrDefault("defer_loading").ValueKind == JsonValueKind.True;

    private static List<ResponseToolDefinition> FilterSearchCatalog(List<ResponseToolDefinition> tools, Func<ResponseToolDefinition, bool> keep)
    {
        var result = new List<ResponseToolDefinition>();
        foreach (var tool in tools)
        {
            if (tool.Type != "namespace" || tool.Extra?.GetValueOrDefault("tools") is not { ValueKind: JsonValueKind.Array } members)
            {
                if (keep(tool)) result.Add(tool);
                continue;
            }
            var selected = (members.Deserialize<List<ResponseToolDefinition>>(ResponseJson.Default) ?? []).Where(keep).ToList();
            if (selected.Count > 0) result.Add(new ResponseToolDefinition { Type = tool.Type,
                Extra = new(tool.Extra) { ["tools"] = JsonSerializer.SerializeToElement(selected, ResponseJson.Default) } });
        }
        return result;
    }

    private static List<ResponseToolDefinition> SelectedSearchTools(List<ResponseToolDefinition> catalog, string output)
    {
        // Use the approved result as a selection only. The request catalog owns the tool definitions.
        try
        {
            using var json = JsonDocument.Parse(output);
            if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("tools", out var selected)
                || selected.ValueKind != JsonValueKind.Array) return [];
            var names = new HashSet<string>(StringComparer.Ordinal);
            void Add(JsonElement tool)
            {
                if (names.Count < 10 && tool.ValueKind == JsonValueKind.Object && tool.TryGetProperty("name", out var name)
                    && name.ValueKind == JsonValueKind.String && name.GetString() is { } value) names.Add(value);
            }
            foreach (var tool in selected.EnumerateArray())
            {
                if (tool.ValueKind == JsonValueKind.Object && tool.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.ValueEquals("namespace")
                    && tool.TryGetProperty("tools", out var children) && children.ValueKind == JsonValueKind.Array)
                    foreach (var child in children.EnumerateArray()) Add(child);
                else Add(tool);
            }
            return FilterSearchCatalog(catalog, function => DeferredFunction(function) && ToolField(function, "name") is { } name && names.Contains(name));
        }
        catch (JsonException) { return []; }
    }

    private static async IAsyncEnumerable<UIMessagePart> ResponseParts(GatewayCall call,
        IAsyncEnumerable<ResponseStreamPart> source, GatewayContainerArtifacts.Turn artifacts, bool includeEncryptedReasoning,
        Func<ResponseMcpApprovalRequestItem, Task<IReadOnlyList<UIMessagePart>>>? convertReview = null,
         UpstreamMcpReview.Result? hostedReview = null, bool stepStarted = false)
    {
        var text = new HashSet<(string Item, int Index)>();
        var reasoning = new HashSet<string>(StringComparer.Ordinal);
        var calls = new Dictionary<string, string>(StringComparer.Ordinal);
        var sentCalls = new HashSet<string>(StringComparer.Ordinal);
        var searchItems = new HashSet<string>(StringComparer.Ordinal);
        var hostedSearches = new Queue<string>();
        var hostedCallIds = new HashSet<string>(StringComparer.Ordinal);
        var refusal = false;
        var step = stepStarted;
        string TextId((string Item, int Index) key) => $"{key.Item}/{key.Index}";
        JsonElement Serialize(ResponseInputItem item) => JsonSerializer.SerializeToElement(item, ResponseJson.Default);

        IEnumerable<UIMessagePart> SearchParts(ResponseInputItem item)
        {
            if (call.ProviderId != "openai") yield break;
            if (item is ResponseToolSearchCallItem { Id: { Length: > 0 } callId, Status: "completed" } search && searchItems.Add(callId))
            {
                var hosted = search.Execution == "server";
                var handle = search.CallId ?? callId;
                if (hosted && search.CallId is null) hostedSearches.Enqueue(handle);
                else if (hosted) hostedCallIds.Add(handle);
                else if (search.Execution != "client" || string.IsNullOrEmpty(search.CallId) || !sentCalls.Add(handle)) yield break;
                yield return new ToolCallPart { ToolCallId = handle, ToolName = "tool_search", Dynamic = true, ProviderExecuted = hosted,
                    Input = new { arguments = search.Arguments, call_id = search.CallId }, ProviderMetadata = SearchMetadata(callId, search) };
            }
            else if (item is ResponseToolSearchOutputItem { Id: { Length: > 0 } outputId, Execution: "server", Status: "completed" } output
                && !searchItems.Contains(outputId))
            {
                string? handle;
                if (output.CallId is { } explicitId) handle = hostedCallIds.Remove(explicitId) ? explicitId : null;
                else handle = hostedSearches.TryDequeue(out var queued) ? queued : null;
                if (handle is null) yield break;
                searchItems.Add(outputId);
                yield return new ToolOutputAvailablePart { ToolCallId = handle, Output = new { tools = output.Tools },
                    ProviderExecuted = true, Dynamic = true, ProviderMetadata = SearchMetadata(outputId, output) };
            }
        }

        Dictionary<string, Dictionary<string, object>?> SearchMetadata(string id, ResponseInputItem item) => new()
            { [call.ProviderId] = new() { [NativeItem] = Serialize(item), ["itemId"] = id } };

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
                case ResponseOutputItemDone { Item: { Type: "tool_search_call" or "tool_search_output" } item }:
                    var nativeSearch = SearchItem(item);
                    if (nativeSearch is not null)
                        foreach (var part in SearchParts(nativeSearch)) yield return part;
                    break;
                case ResponseOutputItemDone { Item: { Type: "function_call", CallId: { } handle } item } when sentCalls.Add(handle):
                    yield return new ToolCallPart { ToolCallId = handle, ToolName = item.Name!, Dynamic = true, ProviderExecuted = false,
                        ToolMetadata = item.Namespace is { } space ? new() { ["namespace"] = JsonSerializer.SerializeToElement(space) } : null,
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
                        yield return new ReasoningEndUIPart { Id = id, ProviderMetadata = includeEncryptedReasoning
                            ? new() { [call.ProviderId] = new() { [NativeItem] = Serialize(thought) } } : null };
                        break;
                    case ResponseFunctionCallItem function when sentCalls.Add(function.CallId):
                        yield return new ToolCallPart { ToolCallId = function.CallId, ToolName = function.Name, Dynamic = true,
                            ProviderExecuted = false, Input = MessagesHistory.ParseArguments(function.Arguments),
                            ToolMetadata = function.Namespace is { } functionSpace ? new() { ["namespace"] = JsonSerializer.SerializeToElement(functionSpace) } : null }; break;
                    case ResponseMcpApprovalRequestItem approval:
                        if (approval.Id is { } approvalId && sentCalls.Contains(approvalId)) break;
                        if (convertReview is null || result.Status != "completed")
                        {
                            yield return new ErrorUIPart { ErrorText = "This provider tool requires execution approval, which API Chat does not support." };
                            yield break;
                        }
                        IReadOnlyList<UIMessagePart> review;
                        try
                        {
                            if (result.OutputItems.OfType<ResponseMcpCallItem>().Any(executed => executed.ApprovalRequestId == approval.Id))
                                throw new ContractException("The provider returned an MCP call alongside its execution approval.", 502);
                            review = await convertReview(approval);
                        }
                        catch (ContractException error) { review = [new ErrorUIPart { ErrorText = error.Message }]; }
                        foreach (var part in review) yield return part;
                        if (review.Any(part => part is ErrorUIPart)) yield break;
                        sentCalls.Add(approval.Id!);
                        break;
                    case ResponseToolSearchCallItem or ResponseToolSearchOutputItem:
                        foreach (var part in SearchParts(item)) yield return part;
                        break;
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
            if (hostedReview is not null)
                foreach (var reviewPart in UpstreamMcpReview.Parts(hostedReview)) yield return reviewPart;
            var usage = result.NormalizedUsage;
            var filtered = result.IncompleteDetails is { ValueKind: JsonValueKind.Object } details
                && details.TryGetProperty("reason", out var reason) && reason.GetString() == "content_filter";
            yield return Finish(call, refusal || filtered ? "content-filter" : result.Status == "incomplete" ? "length"
                : sentCalls.Count > 0 || hostedReview is not null ? "tool-calls" : "stop", usage?.InputTokens, usage?.OutputTokens, call.ProviderId,
                result.Usage, result.AdditionalProperties, result.CreatedAt,
                GatewayPricing.Cost(result) ?? await GatewayPricing.EstimateAsync(call, result.Model, result.Usage));
            yield break;
        }
        throw new IOException("Responses ended without a terminal event.");
    }

    private static ResponseInputItem? SearchItem(ResponseStreamItem item) => item.Type switch
    {
        "tool_search_call" => new ResponseToolSearchCallItem { Id = item.Id, CallId = item.CallId, Status = item.Status,
            Execution = item.Execution ?? "server", Arguments = item.Arguments ?? ToolInput(null) },
        "tool_search_output" => new ResponseToolSearchOutputItem { Id = item.Id, CallId = item.CallId, Status = item.Status,
            Execution = item.Execution ?? "server", Tools = item.Tools?.ToList() ?? [] },
        _ => null
    };

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
