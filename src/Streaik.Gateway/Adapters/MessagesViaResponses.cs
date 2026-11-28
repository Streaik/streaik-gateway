using System.Text.Json;
using Streaik.Messages;
using Streaik.Providers;
using Streaik.Responses;
using Streaik.Responses.Streaming;

namespace Streaik.Gateway;

internal static partial class ProtocolAdapters
{
    public static async Task MessagesViaResponsesAsync(GatewayCall call, MessagesRequest request, IResponsesProvider provider)
    {
        var native = ToResponses(request);
        if (request.Stream == true)
            await call.WriteMessagesStreamAsync(MessageUpdates(provider.StreamResponseAsync(native, call.Headers, call.Ct)));
        else await call.WriteAsync(ToMessages(await provider.CreateResponseAsync(native, call.Headers, call.Ct)));
    }

    private static ResponseRequest ToResponses(MessagesRequest request)
    {
        if (request.StopSequences?.Count > 0 || request.TopK is not null
            || request.Container is not null || request.CacheControl is not null || request.InferenceGeo is not null || request.Speed is not null
            || request.ContextManagement is not null || request.Compaction is not null || request.Diagnostics is not null
            || request.ServiceTier is not null || request.Metadata is not null || request.AdditionalProperties?.Count > 0)
            throw new ContractException("This Messages option has no Responses translation.");
        var input = new List<ResponseInputItem>();
        foreach (var message in request.Messages)
        {
            var role = message.Role switch { "user" => ResponseRole.User, "assistant" => ResponseRole.Assistant,
                _ => throw new ContractException("Unsupported Messages role.") };
            if (message.AdditionalProperties?.Count > 0) throw new ContractException("This Messages turn has no Responses translation.");
            if (message.Content.Text is { } text) input.Add(ResponseTextMessage(role, text));
            else if (message.Content.Blocks is { } blocks)
            {
                foreach (var block in blocks)
                {
                    if (role == ResponseRole.Assistant && (block.Type is "thinking" or "redacted_thinking")) continue;
                    if (block.CacheControl is not null || block.AdditionalProperties?.Count > 0)
                        throw new ContractException("This Messages block has no Responses translation.");
                    input.Add(block switch
                    {
                        { Type: "text", Citations: null or { Count: 0 } } => ResponseTextMessage(role, block.Text ?? ""),
                        { Type: "tool_use", Id: { } id, Name: { } name } when role == ResponseRole.Assistant => new ResponseFunctionCallItem
                            { CallId = id, Name = name, Arguments = block.Input?.GetRawText() ?? "{}" },
                        { Type: "tool_result", ToolUseId: { } id, Content.Text: { } output, IsError: not true } when role == ResponseRole.User =>
                            new ResponseFunctionCallOutputItem { CallId = id, Output = output },
                        _ => throw new ContractException("Only text and client function history can be translated to Responses.")
                    });
                }
            }
            else throw new ContractException("Unsupported Messages content.");
        }
        var instructions = request.System?.Text;
        if (request.System?.Blocks is { } system)
            instructions = string.Join('\n', system.Select(b => b is { Type: "text", CacheControl: null, AdditionalProperties: null }
                ? b.Text : throw new ContractException("Unsupported Messages system content.")));
        var native = new ResponseRequest { Model = request.Model, Store = false, Input = new ResponseInput(input), Instructions = instructions,
            Stream = request.Stream, MaxOutputTokens = request.MaxTokens, Temperature = request.Temperature, TopP = request.TopP };
        if (ReasoningBridge.FromMessages(request) is { } effort) native.Reasoning = new Reasoning { Effort = effort };
        if (request.ToolChoice?.Type != "none") native.Tools = request.Tools?.Select(tool =>
        {
            if (tool.Type is not (null or "custom") || tool.Name is null || tool.InputSchema is null || tool.CacheControl is not null
                || tool.AllowedCallers is not null || tool.DeferLoading == true || tool.InputExamples is not null || tool.AdditionalProperties?.Count > 0)
                throw new ContractException("Only client function tools can be translated to Responses.");
            return ResponseFunction(tool.Name, tool.Description, tool.InputSchema, tool.Strict);
        }).ToList();
        if (request.ToolChoice is { } choice)
        {
            native.ToolChoice = choice.Type switch { "none" => "none", "auto" => "auto", "any" => "required",
                "tool" => new { type = "function", name = choice.Name }, _ => throw new ContractException("Unsupported Messages tool_choice.") };
            if (choice.DisableParallelToolUse.HasValue) native.ParallelToolCalls = !choice.DisableParallelToolUse.Value;
        }
        return native;
    }

    internal static ResponseInputMessage ResponseTextMessage(ResponseRole role, string text) => new()
    { Role = role, Content = new ResponseMessageContent([role == ResponseRole.Assistant ? new OutputTextPart(text) : new InputTextPart(text)]) };

    internal static ResponseToolDefinition ResponseFunction(string name, string? description, JsonElement? schema, bool? strict = null)
    {
        var fields = new Dictionary<string, JsonElement> { ["name"] = JsonSerializer.SerializeToElement(name),
            ["parameters"] = schema ?? JsonSerializer.SerializeToElement(new { type = "object" }), ["strict"] = JsonSerializer.SerializeToElement(strict ?? false) };
        if (description is not null) fields["description"] = JsonSerializer.SerializeToElement(description);
        return new ResponseToolDefinition { Type = "function", Extra = fields };
    }

    private static MessagesResponse ToMessages(ResponseResult result)
    {
        if (result.Status is "failed" or "cancelled" || result.Error is not null)
            throw new UpstreamStreamException(result.Error?.Message);
        var content = new List<MessageContentBlock>();
        foreach (var item in result.OutputItems)
        {
            switch (item)
            {
                case ResponseInputMessage message:
                    if (message.Content.Text is { } value) content.Add(new MessageContentBlock { Type = "text", Text = value });
                    foreach (var part in message.Content.Parts ?? [])
                        content.Add(part is OutputTextPart text ? new MessageContentBlock { Type = "text", Text = text.Text }
                            : throw new IOException("This Responses content cannot be represented as Messages."));
                    break;
                case ResponseFunctionCallItem function:
                    content.Add(new MessageContentBlock { Type = "tool_use", Id = function.CallId, Name = function.Name,
                        Input = MessagesHistory.ParseArguments(function.Arguments) }); break;
                case ResponseReasoningItem reasoning:
                    foreach (var part in reasoning.Summary.Where(part => part.Type == "summary_text" && !string.IsNullOrEmpty(part.Text)))
                        content.Add(new() { Type = "thinking", Thinking = part.Text, Signature = "" });
                    break;
                default: throw new IOException("This Responses output cannot be represented as Messages.");
            }
        }
        return new MessagesResponse { Id = result.Id, Model = result.Model, Type = "message", Role = "assistant", Content = content,
            StopReason = MessageStop(result, content.Any(x => x.Type == "tool_use")), Usage = MessageUsage(result.NormalizedUsage),
            Metadata = GatewayPricing.CostMetadata(GatewayPricing.Cost(result)) };
    }

    private static string MessageStop(ResponseResult result, bool tools)
        => result.Status == "incomplete" ? (result.IncompleteDetails is { ValueKind: JsonValueKind.Object } details
            && details.TryGetProperty("reason", out var reason) && reason.GetString() == "content_filter" ? "refusal" : "max_tokens")
            : tools ? "tool_use" : "end_turn";

    private static MessagesUsage MessageUsage(ResponseUsage? usage) => new()
    { InputTokens = usage?.InputTokens is { } input ? input - (usage.InputTokensDetails?.CachedTokens ?? 0) - (usage.InputTokensDetails?.CacheWriteTokens ?? 0) : null,
      OutputTokens = usage?.OutputTokens, CacheReadInputTokens = usage?.InputTokensDetails?.CachedTokens,
      CacheCreationInputTokens = usage?.InputTokensDetails?.CacheWriteTokens };

    private static Dictionary<string, JsonElement>? ResponseBridgeMetadata(ResponseResult response, string? provider)
    {
        var metadata = GatewayPricing.CostMetadata(GatewayPricing.Cost(response));
        if (provider is not null && (response.AdditionalProperties?.Count > 0 || response.Metadata?.Count > 0))
            (metadata ??= [])[provider] = JsonSerializer.SerializeToElement(new { extensions = response.AdditionalProperties, metadata = response.Metadata });
        return metadata;
    }

    private static async IAsyncEnumerable<MessageStreamPart> MessageUpdates(IAsyncEnumerable<ResponseStreamPart> source, string? provider = null)
    {
        var blocks = new Dictionary<(string Item, int Content), int>();
        var closed = new HashSet<int>();
        var argumentDeltas = new HashSet<int>();
        var summaries = new Dictionary<(string Item, int Summary), int>();
        var toolCalls = false;
        var started = false;
        const int tool = -1;
        MessageStreamPart Start(int i, MessageContentBlock block) => new() { Type = "content_block_start", Index = i, ContentBlock = block };
        MessageStreamPart Delta(int i, MessageStreamDelta delta) => new() { Type = "content_block_delta", Index = i, Delta = delta };
        MessageStreamPart End(int i) => new() { Type = "content_block_stop", Index = i };
        await foreach (var ev in source)
        {
            switch (ev)
            {
                case ResponseCreated evt when !started:
                    started = true;
                    yield return new MessageStreamPart { Type = "message_start", Message = new MessagesResponse
                    { Id = evt.Response.Id, Role = "assistant", Type = "message", Usage = MessageUsage(evt.Response.NormalizedUsage) } };
                    break;
                case ResponseOutputItemAdded { Item: { Type: "function_call", Id: { } id } item }:
                    var functionIndex = blocks.Count + summaries.Count;
                    blocks.Add((id, tool), functionIndex);
                    toolCalls = true;
                    yield return Start(functionIndex, new MessageContentBlock { Type = "tool_use", Id = item.CallId, Name = item.Name,
                        Input = JsonSerializer.SerializeToElement(new { }) }); break;
                case ResponseOutputItemAdded { Item.Type: not ("message" or "reasoning") }:
                    throw new IOException("Unsupported Responses output item for Messages.");
                case ResponseReasoningSummaryTextDelta thought when !string.IsNullOrEmpty(thought.Delta):
                    var summaryKey = (thought.ItemId, thought.SummaryIndex);
                    if (!summaries.TryGetValue(summaryKey, out var summary))
                    {
                        summary = blocks.Count + summaries.Count;
                        summaries.Add(summaryKey, summary);
                        yield return Start(summary, new() { Type = "thinking", Thinking = "", Signature = "" });
                    }
                    yield return Delta(summary, new() { Type = "thinking_delta", Thinking = thought.Delta }); break;
                case ResponseReasoningSummaryTextDone thought:
                    var doneKey = (thought.ItemId, thought.SummaryIndex);
                    if (!summaries.TryGetValue(doneKey, out var doneSummary) && !string.IsNullOrEmpty(thought.Text))
                    {
                        doneSummary = blocks.Count + summaries.Count;
                        summaries.Add(doneKey, doneSummary);
                        yield return Start(doneSummary, new() { Type = "thinking", Thinking = thought.Text, Signature = "" });
                    }
                    if (summaries.ContainsKey(doneKey) && closed.Add(doneSummary)) yield return End(doneSummary);
                    break;
                case ResponseContentPartAdded { Part.Type: "output_text" } evt:
                    var textIndex = blocks.Count + summaries.Count;
                    if (blocks.TryAdd((evt.ItemId, evt.ContentIndex), textIndex))
                        yield return Start(textIndex, new MessageContentBlock { Type = "text", Text = evt.Part.Text ?? "" });
                    break;
                case ResponseOutputTextDelta evt:
                    var key = (evt.ItemId, evt.ContentIndex);
                    if (!blocks.TryGetValue(key, out var index))
                    {
                        index = blocks.Count + summaries.Count; blocks.Add(key, index);
                        yield return Start(index, new MessageContentBlock { Type = "text", Text = "" });
                    }
                    yield return Delta(index, new MessageStreamDelta { Type = "text_delta", Text = evt.Delta }); break;
                case ResponseFunctionCallArgumentsDelta evt when blocks.TryGetValue((evt.ItemId, tool), out var i):
                    argumentDeltas.Add(i);
                    yield return Delta(i, new MessageStreamDelta { Type = "input_json_delta", PartialJson = evt.Delta }); break;
                case ResponseFunctionCallArgumentsDone evt when blocks.TryGetValue((evt.ItemId, tool), out var i) && argumentDeltas.Add(i):
                    yield return Delta(i, new MessageStreamDelta { Type = "input_json_delta", PartialJson = evt.Arguments }); break;
                case ResponseContentPartDone evt when blocks.TryGetValue((evt.ItemId, evt.ContentIndex), out var i) && closed.Add(i):
                    yield return End(i); break;
                case ResponseOutputItemDone { Item: { Type: "function_call", Id: { } id } done } when blocks.TryGetValue((id, tool), out var i) && closed.Add(i):
                    if (argumentDeltas.Add(i) && done.Arguments is { } arguments)
                        yield return Delta(i, new MessageStreamDelta { Type = "input_json_delta", PartialJson = arguments.ValueKind == JsonValueKind.String
                            ? arguments.GetString() : arguments.GetRawText() });
                    yield return End(i); break;
                case ResponseError error: throw new UpstreamStreamException(GatewayCall.ResponseErrorMessage(error));
                case ResponseFailed failed: throw new UpstreamStreamException(failed.Response?.Error?.Message);
            }
            var result = ev switch { ResponseCompleted completed => completed.Response, ResponseIncompleteEvent incomplete => incomplete.Response, _ => null };
            if (result is null) continue;
            if (!started)
                yield return new MessageStreamPart { Type = "message_start", Message = new MessagesResponse { Id = result.Id, Type = "message", Role = "assistant" } };
            foreach (var i in blocks.Values.Concat(summaries.Values).Order()) if (closed.Add(i)) yield return End(i);
            yield return new MessageStreamPart { Type = "message_delta", Delta = new MessageStreamDelta { StopReason = MessageStop(result, toolCalls) },
                Usage = MessageUsage(result.NormalizedUsage) };
            yield return new MessageStreamPart { Type = "message_stop", Metadata = ResponseBridgeMetadata(result, provider) };
            yield break;
        }
        throw new IOException("Responses ended without a terminal event.");
    }
}
