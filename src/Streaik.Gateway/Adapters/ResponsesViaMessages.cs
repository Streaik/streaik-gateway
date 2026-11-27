using System.Text;
using System.Text.Json;
using Streaik.Messages;
using Streaik.Providers;
using Streaik.Responses;
using Streaik.Responses.Output;
using Streaik.Responses.Streaming;

namespace Streaik.Gateway;

internal static partial class ProtocolAdapters
{
    public static async Task ResponsesViaMessagesAsync(GatewayCall call, ResponseRequest request, IMessagesProvider provider)
    {
        StreaikGateway.ValidateAnthropicMessageInputs(call, request.MaxOutputTokens);
        var native = ToMessages(request);
        if (request.Stream == true)
            await call.WriteResponsesStreamAsync(ResponseUpdates(provider.StreamMessageAsync(native, call.Headers, call.Ct)));
        else
        {
            var result = await provider.CreateMessageAsync(native, call.Headers, call.Ct);
            var response = ResponseResultFrom(result.Id!, DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                result.Content.Select((block, index) => OutputItem(result.Id!, index, block)).ToList(), result.Usage, result.StopReason);
            response.Model = result.Model ?? call.NativeModel;
            GatewayPricing.Attach(response, GatewayPricing.Cost(result.Metadata));
            await call.WriteAsync(response);
        }
    }

    private static MessagesRequest ToMessages(ResponseRequest request)
    {
        if (request.Reasoning is not null || request.Text is not null || request.Prompt is not null || request.Include?.Count > 0
            || request.ContextManagement?.Length > 0 || request.AdditionalProperties?.Count > 0 || request.TopLogprobs is not null
            || request.Truncation is not null || request.MaxToolCalls is not null || request.ServiceTier is not null || request.Metadata?.Count > 0
            || request.User is not null || request.SafetyIdentifier is not null || request.PromptCacheKey is not null || request.PromptCacheRetention is not null)
            throw new ContractException("This Responses option has no Messages translation.");
        var history = new MessagesHistory();
        history.Text("system", request.Instructions);
        history.Text("user", request.Input?.Text);
        foreach (var item in request.Input?.Items ?? [])
        {
            switch (item)
            {
                case ResponseInputMessage message when message.AdditionalProperties?.Count is null or 0:
                    var role = message.Role.ToString().ToLowerInvariant();
                    history.Text(role, message.Content.Text);
                    foreach (var part in message.Content.Parts ?? [])
                    {
                        if (part.AdditionalProperties?.Any(p => p.Key != "annotations" || p.Value.ValueKind != JsonValueKind.Array || p.Value.GetArrayLength() != 0) == true)
                            throw new ContractException("This Responses content has no Messages translation.");
                        history.Text(role, part switch { InputTextPart text => text.Text, OutputTextPart text => text.Text,
                            _ => throw new ContractException("This Responses adapter supports only text messages.") });
                    }
                    break;
                case ResponseFunctionCallItem function when function.Namespace is null:
                    history.Tool(function.CallId, function.Name, MessagesHistory.ParseArguments(function.Arguments)); break;
                case ResponseFunctionCallOutputItem { StructuredOutput: null } result:
                    history.Result(result.CallId ?? throw new ContractException("Missing function call ID."), result.Output); break;
                default: throw new ContractException("This Responses input item has no Messages translation.");
            }
        }
        var native = history.Build(request.Model!, request.MaxOutputTokens, request.Temperature, request.TopP, request.Stream);
        if (request.Tools is { Count: > 0 })
            native.Tools = request.Tools.Select(tool =>
            {
                if (tool.Type != "function" || tool.Extra is null || tool.Extra.Keys.Any(k => k is not ("name" or "description" or "parameters" or "strict")))
                    throw new ContractException("Only function tools can be translated to Messages.");
                return new MessageToolDefinition { Name = String(tool.Extra, "name") ?? throw new ContractException("Missing function tool name."),
                    Description = String(tool.Extra, "description"), InputSchema = Element(tool.Extra, "parameters"),
                    Strict = Element(tool.Extra, "strict") is { ValueKind: JsonValueKind.True or JsonValueKind.False } strict ? strict.GetBoolean() : null };
            }).ToList();
        if (request.ToolChoice is JsonElement choice)
        {
            native.ToolChoice = choice.ValueKind switch
            {
                JsonValueKind.String => choice.GetString() switch
                {
                    "none" => null, "auto" => new MessageToolChoice(), "required" => new MessageToolChoice { Type = "any" },
                    _ => throw new ContractException("Unsupported Responses tool_choice.")
                },
                JsonValueKind.Object when choice.TryGetProperty("type", out var type) && type.GetString() == "function"
                    && choice.TryGetProperty("name", out var name) => new MessageToolChoice { Type = "tool", Name = name.GetString() },
                _ => throw new ContractException("Unsupported Responses tool_choice.")
            };
            if (choice.ValueKind == JsonValueKind.String && choice.GetString() == "none") native.Tools = null;
        }
        if (native.Tools is { Count: > 0 } && request.ParallelToolCalls.HasValue)
            (native.ToolChoice ??= new MessageToolChoice()).DisableParallelToolUse = !request.ParallelToolCalls.Value;
        return native;
    }

    private static string? String(Dictionary<string, JsonElement>? fields, string key)
        => Element(fields, key) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
    private static JsonElement? Element(Dictionary<string, JsonElement>? fields, string key)
        => fields?.TryGetValue(key, out var value) == true ? value : null;

    private static object OutputItem(string id, int index, MessageContentBlock block) => block switch
    {
        { Type: "text", Citations: null or { Count: 0 } } => new ResponseOutputMessage { Id = $"{id}_{index}",
            Role = ResponseRole.Assistant, Status = "completed", Content = [new ResponseOutputText { Text = block.Text ?? "", Annotations = [] }] },
        { Type: "tool_use" } => new ResponseFunctionCallItem { Id = $"{id}_{index}", CallId = block.Id!, Name = block.Name!,
            Status = "completed", Arguments = block.Input?.GetRawText() ?? "{}" },
        _ => throw new IOException("This Messages block cannot be represented as Responses output.")
    };

    private static ResponseResult ResponseResultFrom(string id, long created, List<object> items, MessagesUsage? usage, string? stop, bool running = false)
    {
        var reason = stop switch { "max_tokens" or "model_context_window_exceeded" => "max_output_tokens", "refusal" => "content_filter", _ => null };
        var input = MessagesHistory.TotalInput(usage);
        var output = usage?.OutputTokens;
        return new ResponseResult { Id = id, CreatedAt = created, Output = items,
            Status = running ? "in_progress" : reason is null ? "completed" : "incomplete",
            IncompleteDetails = reason is null ? null : JsonSerializer.SerializeToElement(new { reason }),
            Usage = usage is null ? null : new ResponseUsage { InputTokens = input, OutputTokens = output, TotalTokens = input + output,
                InputTokensDetails = new ResponseInputTokensDetails { CachedTokens = usage.CacheReadInputTokens, CacheWriteTokens = usage.CacheCreationInputTokens },
                OutputTokensDetails = new ResponseOutputTokensDetails { ReasoningTokens = usage.OutputTokensDetails?.ThinkingTokens } } };
    }

    private static async IAsyncEnumerable<ResponseStreamPart> ResponseUpdates(IAsyncEnumerable<MessageStreamPart> source, string? metadataProvider = null)
    {
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var usage = new MessagesUsage();
        var blocks = new SortedDictionary<int, (MessageContentBlock Block, StringBuilder Arguments)>();
        var id = "";
        string? upstreamModel = null;
        GatewayPrice? cost = null;
        var sequence = 0;
        string? stop = null;
        string ItemId(int i) => $"{id}_{i}";
        ResponseStreamContentPart Text(string? text) => new() { Type = "output_text", Text = text ?? "", Annotations = [] };
        ResponseStreamItem StreamItem(int i, MessageContentBlock block, bool done) => block.Type == "text"
            ? new ResponseStreamItem { Type = "message", Id = ItemId(i), Status = done ? "completed" : "in_progress", Role = "assistant",
                Content = JsonSerializer.SerializeToElement(done ? new[] { Text(block.Text) } : [], ResponseJson.Default) }
            : new ResponseStreamItem { Type = "function_call", Id = ItemId(i), Status = done ? "completed" : "in_progress",
                Name = block.Name, CallId = block.Id, Arguments = JsonSerializer.SerializeToElement(done ? block.Input?.GetRawText() ?? "{}" : "") };
        await foreach (var part in source)
        {
            var index = part.Index ?? -1;
            switch (part)
            {
                case { Type: "message_start", Message: { } message }:
                    id = message.Id!;
                    upstreamModel = message.Model;
                    MessagesHistory.MergeUsage(usage, message.Usage);
                    yield return new ResponseCreated { SequenceNumber = sequence++, Response = ResponseResultFrom(id, created, [], null, null, running: true) }; break;
                case { Type: "content_block_start", ContentBlock: { Type: "text" or "tool_use" } block }:
                    blocks.Add(index, (block, new StringBuilder()));
                    yield return new ResponseOutputItemAdded { SequenceNumber = sequence++, OutputIndex = index, Item = StreamItem(index, block, false) };
                    if (block.Type == "text")
                    {
                        yield return new ResponseContentPartAdded { SequenceNumber = sequence++, OutputIndex = index, ItemId = ItemId(index), Part = Text("") };
                        if (!string.IsNullOrEmpty(block.Text)) yield return new ResponseOutputTextDelta
                            { SequenceNumber = sequence++, Outputindex = index, ItemId = ItemId(index), Delta = block.Text };
                    }
                    break;
                case { Type: "content_block_delta", Delta.Text: { } text } when blocks.TryGetValue(index, out var current):
                    current.Block.Text += text;
                    yield return new ResponseOutputTextDelta { SequenceNumber = sequence++, Outputindex = index, ItemId = ItemId(index), Delta = text }; break;
                case { Type: "content_block_delta", Delta.PartialJson: { } args } when blocks.TryGetValue(index, out var current):
                    current.Arguments.Append(args);
                    yield return new ResponseFunctionCallArgumentsDelta { SequenceNumber = sequence++, OutputIndex = index, ItemId = ItemId(index), Delta = args }; break;
                case { Type: "content_block_stop" } when blocks.TryGetValue(index, out var current):
                    if (current.Block.Type == "text")
                    {
                        yield return new ResponseOutputTextDone { SequenceNumber = sequence++, Outputindex = index, ItemId = ItemId(index), Text = current.Block.Text ?? "" };
                        yield return new ResponseContentPartDone { SequenceNumber = sequence++, OutputIndex = index, ItemId = ItemId(index), Part = Text(current.Block.Text) };
                    }
                    else
                    {
                        if (current.Arguments.Length > 0) current.Block.Input = MessagesHistory.ParseArguments(current.Arguments.ToString());
                        yield return new ResponseFunctionCallArgumentsDone { SequenceNumber = sequence++, OutputIndex = index, ItemId = ItemId(index),
                            Arguments = current.Block.Input?.GetRawText() ?? "{}" };
                    }
                    yield return new ResponseOutputItemDone { SequenceNumber = sequence++, OutputIndex = index, Item = StreamItem(index, current.Block, true) }; break;
                case { Type: "message_delta" }:
                    cost = GatewayPricing.Cost(part.Metadata) ?? cost;
                    stop = part.Delta?.StopReason ?? stop;
                    MessagesHistory.MergeUsage(usage, part.Usage); break;
                case { Type: "message_stop" }:
                    var result = ResponseResultFrom(id, created, blocks.Select(x => OutputItem(id, x.Key, x.Value.Block)).ToList(), usage, stop);
                    if (upstreamModel is not null) result.Model = upstreamModel;
                    AttachProviderMetadata(result, part.Metadata, metadataProvider);
                    GatewayPricing.Attach(result, GatewayPricing.Cost(part.Metadata) ?? cost);
                    yield return result.Status == "incomplete" ? new ResponseIncompleteEvent { SequenceNumber = sequence++, Response = result }
                        : new ResponseCompleted { SequenceNumber = sequence++, Response = result };
                    yield break;
                case { Type: "error" or "content_block_start" or "content_block_delta" }:
                    throw new IOException("This Messages event cannot be represented as Responses output.");
            }
        }
        throw new IOException("Messages stream ended without message_stop.");
    }
}
