using System.Text;
using System.Text.Json;
using Streaik.ChatCompletions.Models;
using Streaik.Messages;

namespace Streaik.Gateway;

// Use the existing Messages content/event vocabulary as the bridge to Responses and Vercel.
// This is an in-process mapping: no Messages endpoint or provider is called.
internal static class ChatCompletionsBridge
{
    public static ChatCompletionOptions Request(MessagesRequest request, int? maxTokens, bool translatePresentation = false)
    {
        if (request.CacheControl is not null || request.Container is not null || request.InferenceGeo is not null
            || request.Metadata is not null || request.OutputConfig is not null || request.ServiceTier is not null
            || request.Speed is not null || request.Thinking is not null || request.ContextManagement is not null
            || request.Diagnostics is not null || request.Compaction is not null || request.TopK is not null
            || request.AdditionalProperties?.Count > 0)
            throw Unsupported("option");

        var messages = new List<ChatMessage>();
        if (request.System is { } system) messages.Add(new() { Role = "system", Content = Text(system) });
        if (request.Messages is not { Count: > 0 }) throw new ContractException("At least one message is required.");
        foreach (var message in request.Messages)
        {
            if (message.AdditionalProperties?.Count > 0 || message.Role is not ("user" or "assistant")) throw Unsupported("message");
            if (message.Content.Text is { } plain)
            {
                messages.Add(new() { Role = message.Role, Content = plain });
                continue;
            }
            if (message.Content.Blocks is not { Count: > 0 } blocks) throw Unsupported("message content");
            var text = new StringBuilder();
            var calls = new List<ChatCompletionToolCall>();
            var parts = new List<ChatMessageContentPart>();
            void FlushText()
            {
                if (text.Length > 0) parts.Add(new ChatMessageTextPart { Text = text.ToString() });
                text.Clear();
            }
            void Flush()
            {
                if (text.Length == 0 && calls.Count == 0 && parts.Count == 0) return;
                ChatMessageContent content;
                if (parts.Count > 0) { FlushText(); content = new ChatMessageContent(parts); }
                else content = text.Length == 0 ? ChatMessageContent.Null : text.ToString();
                messages.Add(new() { Role = message.Role, Content = content,
                    ToolCalls = calls.Count == 0 ? null : calls.ToArray() });
                text.Clear();
                calls.Clear();
                parts.Clear();
            }
            foreach (var block in blocks)
            {
                if (block.CacheControl is not null || block.Citations?.Count > 0 || block.AdditionalProperties?.Count > 0)
                    throw Unsupported("content block");
                switch (block)
                {
                    case { Type: "text", Text: { } value }: text.Append(value); break;
                    case { Type: "image", Source: { Type: "url", Url: { } url, AdditionalProperties: null } }
                        when message.Role == "user" && ChatAttachments.IsLink(url):
                        FlushText(); parts.Add(new ChatMessageImagePart { ImageUrl = new() { Url = url } }); break;
                    case { Type: "image", Source: { Type: "base64", MediaType: { } media, Data: { } data, AdditionalProperties: null } }
                        when message.Role == "user" && ChatAttachments.IsImage(media):
                        FlushText(); parts.Add(new ChatMessageImagePart { ImageUrl = new() { Url = $"data:{media};base64,{data}" } }); break;
                    case { Type: "document", Source: { Type: "base64", MediaType: "application/pdf", Data: { } pdf, AdditionalProperties: null } }
                        when message.Role == "user":
                        FlushText(); parts.Add(new ChatMessageFilePart { File = new() { Filename = block.Title ?? "attachment.pdf",
                            FileData = $"data:application/pdf;base64,{pdf}" } }); break;
                    case { Type: "thinking", Thinking: { Length: > 0 } thought } when translatePresentation && message.Role == "assistant":
                        FlushText(); parts.Add(new ChatMessageUnknownContentPart { AdditionalProperties = new()
                        { ["type"] = JsonSerializer.SerializeToElement("thinking"), ["thinking"] = JsonSerializer.SerializeToElement(thought) } }); break;
                    case { Type: "tool_use", Id: { Length: > 0 } id, Name: { Length: > 0 } name, Caller: null,
                        Input: { ValueKind: JsonValueKind.Object } input } when message.Role == "assistant":
                        calls.Add(new ChatCompletionMessageToolCall { Id = id, Function = new() { Name = name, Arguments = input.GetRawText() } });
                        break;
                    case { Type: "tool_result", ToolUseId: { Length: > 0 } id } when message.Role == "user":
                        Flush();
                        messages.Add(new() { Role = "tool", ToolCallId = id, Content = block.Content is { } result ? Text(result) : "" });
                        break;
                    default: throw Unsupported("content block");
                }
            }
            Flush();
        }
        if (!messages.Any(message => message.Role != "system")) throw new ContractException("At least one message is required.");

        var tools = (request.Tools ?? []).Select(Tool).ToArray();
        var choice = request.ToolChoice;
        if (choice?.AdditionalProperties?.Count > 0) throw Unsupported("tool choice");
        var native = new ChatCompletionOptions { Model = request.Model, Messages = messages, MaxTokens = maxTokens,
            Temperature = request.Temperature, TopP = request.TopP, Stream = request.Stream, Tools = tools,
            Stop = request.StopSequences is { Count: > 0 } stop ? (ChatCompletionStop)stop.ToArray() : null };
        if (choice is not null)
        {
            if (choice.Type == "tool" && choice.Name is { Length: > 0 } name)
                native.ToolChoiceOption = new ChatCompletionNamedToolChoice { Function = new() { Name = name } };
            else native.ToolChoice = choice.Type switch { "auto" => "auto", "any" => "required", "none" => "none", _ => throw Unsupported("tool choice") };
            if (choice.DisableParallelToolUse.HasValue) native.ParallelToolCalls = !choice.DisableParallelToolUse.Value;
        }
        return native;
    }

    private static string Text(MessagesContent content) => content.Text ?? (content.Blocks is { } blocks
        ? string.Concat(blocks.Select(block => block is { Type: "text", CacheControl: null, Citations: null or { Count: 0 }, AdditionalProperties: null or { Count: 0 } }
            ? block.Text : throw Unsupported("content block"))) : throw Unsupported("message content"));

    private static ChatCompletionToolDefinition Tool(MessageToolDefinition tool)
    {
        if (tool.Type is not (null or "custom") || string.IsNullOrWhiteSpace(tool.Name) || tool.AdditionalProperties?.Count > 0
            || tool.AllowedCallers?.Any(caller => caller != "direct") == true || tool.CacheControl is not null
            || tool.DeferLoading == true || tool.EagerInputStreaming == true || tool.InputExamples?.Count > 0
            || tool.AllowedDomains is not null || tool.BlockedDomains is not null || tool.MaxUses is not null
            || tool.MaxContentTokens is not null || tool.MaxCharacters is not null || tool.UseCache is not null
            || tool.Citations is not null || tool.UserLocation is not null)
            throw Unsupported("tool definition");
        return new ChatCompletionTool { Function = new() { Name = tool.Name, Description = tool.Description,
            Parameters = tool.InputSchema, Strict = tool.Strict } };
    }

    public static MessagesResponse Result(ChatCompletion result, string? metadataProvider = null)
    {
        if (result.AdditionalProperties?.ContainsKey("error") == true || result.Choices?.FirstOrDefault(choice => choice.Index == 0) is not { } choice
            || choice.Message is not { } message || message.FunctionCall is not null || message.Audio is not null)
            throw new IOException("Invalid Chat Completions result.");
        List<MessageContentBlock> blocks = [];
        if (message.Content is { Length: > 0 } text) blocks.Add(new() { Type = "text", Text = text });
        foreach (var tool in message.ToolCalls ?? [])
        {
            if (tool is not ChatCompletionMessageToolCall { Id.Length: > 0, Function.Name.Length: > 0 } function)
                throw new IOException("Invalid Chat Completions tool call.");
            blocks.Add(new() { Type = "tool_use", Id = function.Id, Name = function.Function.Name,
                Input = Arguments(function.Function.Arguments) });
        }
        var metadata = result.MetadataExtensions;
        if (metadataProvider is not null)
        {
            var raw = result.AdditionalProperties is { } fields ? new Dictionary<string, JsonElement>(fields) : new();
            AddRaw(raw, "choice_extensions", choice.AdditionalProperties);
            AddRaw(raw, "message_extensions", message.AdditionalProperties);
            AddRaw(raw, "annotations", message.Annotations?.ToArray());
            metadata = ProviderMetadata(metadataProvider, raw, result.Usage, metadata);
        }
        return new MessagesResponse { Id = result.Id, Model = result.Model, Role = "assistant", Type = "message", Content = blocks,
            StopReason = StopReason(choice.FinishReason, blocks.Any(block => block.Type == "tool_use"), message.Refusal is { Length: > 0 }),
            Usage = Usage(result.Usage), Metadata = metadata };
    }

    public static async IAsyncEnumerable<MessageStreamPart> Updates(IAsyncEnumerable<ChatCompletionUpdate> source,
        bool preserveNativeFields = false, string? metadataProvider = null, bool translatePresentation = false)
    {
        var tools = new Dictionary<int, ToolState>();
        var next = 0;
        int? textIndex = null;
        int? reasoningIndex = null;
        string? finish = null;
        ChatCompletionUsage? usage = null;
        GatewayPrice? cost = null;
        var started = false;
        var refusal = false;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        Dictionary<string, JsonElement>? nativeFields = preserveNativeFields ? new(StringComparer.Ordinal) : null;
        List<object>? nativeDeltas = preserveNativeFields ? [] : null;
        await foreach (var part in source)
        {
            if (part.AdditionalProperties?.ContainsKey("error") == true) throw new IOException("Chat Completions returned a stream error.");
            if (nativeFields is not null && part.AdditionalProperties is { } extra)
                foreach (var (name, value) in extra) nativeFields[name] = value;
            usage = part.Usage ?? usage;
            cost = GatewayPricing.Cost(GatewayPricing.ChatMetadata(part.AdditionalProperties)) ?? cost;
            if (part.Choices?.FirstOrDefault(choice => choice.Index == 0) is not { } choice) continue;
            if (nativeDeltas is not null && (choice.AdditionalProperties?.Count > 0 || choice.Delta?.AdditionalProperties?.Count > 0))
                nativeDeltas.Add(new { choice = choice.AdditionalProperties, delta = choice.Delta?.AdditionalProperties });
            if (!started)
            {
                started = true;
                yield return new MessageStreamPart { Type = "message_start", Message = new MessagesResponse
                    { Id = part.Id ?? $"chatcmpl_{Guid.NewGuid():N}", Model = part.Model, Role = "assistant", Type = "message", Content = [] } };
            }
            finish = choice.FinishReason ?? finish;
            if (choice.Delta is not { } delta) continue;
            if (delta.FunctionCall is not null || delta.Audio is not null) throw new IOException("Unsupported Chat Completions delta.");
            refusal |= delta.Refusal is { Length: > 0 };
            if (translatePresentation && delta.AdditionalProperties?.GetValueOrDefault("reasoning_content") is { ValueKind: JsonValueKind.String } thought
                && thought.GetString() is { Length: > 0 } reasoning)
            {
                if (!reasoningIndex.HasValue)
                {
                    reasoningIndex = next++;
                    yield return Start(reasoningIndex.Value, new() { Type = "thinking", Thinking = "" });
                }
                yield return Delta(reasoningIndex.Value, new() { Type = "thinking_delta", Thinking = reasoning });
            }
            if (delta.Content is { Length: > 0 } text)
            {
                if (!textIndex.HasValue)
                {
                    textIndex = next++;
                    yield return Start(textIndex.Value, new() { Type = "text", Text = "" });
                }
                yield return Delta(textIndex.Value, new() { Type = "text_delta", Text = text });
            }
            if (translatePresentation && delta.AdditionalProperties?.GetValueOrDefault("annotations") is { ValueKind: JsonValueKind.Array } annotations)
            {
                foreach (var annotation in annotations.EnumerateArray())
                {
                    if (annotation.ValueKind != JsonValueKind.Object || !annotation.TryGetProperty("url_citation", out var citation)
                        || citation.ValueKind != JsonValueKind.Object) continue;
                    var url = citation.Deserialize<ChatCompletionUrlCitation>(JsonSerializerOptions.Web);
                    if (url is null || !Uri.TryCreate(url.Url, UriKind.Absolute, out var link) || link.Scheme is not ("https" or "http")) continue;
                    if (!textIndex.HasValue)
                    {
                        textIndex = next++;
                        yield return Start(textIndex.Value, new() { Type = "text", Text = "" });
                    }
                    yield return Delta(textIndex.Value, new() { Type = "citations_delta", Citation = new()
                    { Type = "web_search_result_location", Url = url.Url, Title = url.Title, StartCharIndex = url.StartIndex, EndCharIndex = url.EndIndex } });
                }
            }
            foreach (var fragment in delta.ToolCalls ?? [])
            {
                if (fragment.Type is not (null or "function")) throw new IOException("Unsupported Chat Completions tool call.");
                if (!tools.TryGetValue(fragment.Index, out var tool)) tools.Add(fragment.Index, tool = new());
                tool.Id ??= fragment.Id is { Length: > 0 } id ? id : null;
                tool.Name ??= fragment.Function?.Name is { Length: > 0 } name ? name : null;
                var args = fragment.Function?.Arguments;
                tool.Arguments.Append(args);
                if (tool.Index is null && tool.Id is { } callId && tool.Name is { } toolName)
                {
                    if (!ids.Add(callId)) throw new IOException("Duplicate Chat Completions tool call ID.");
                    tool.Index = next++;
                    yield return Start(tool.Index.Value, new() { Type = "tool_use", Id = callId, Name = toolName,
                        Input = JsonSerializer.SerializeToElement(new { }) });
                    args = tool.Arguments.ToString();
                }
                if (tool.Index is { } index && args is { Length: > 0 })
                    yield return Delta(index, new() { Type = "input_json_delta", PartialJson = args });
            }
        }

        // Read through [DONE] before finalizing; some providers report usage after the finish reason.
        if (!started || finish is null) throw new IOException("Chat Completions ended without a finish reason.");
        foreach (var tool in tools.Values)
        {
            if (!tool.Index.HasValue) throw new IOException("Incomplete Chat Completions tool call.");
            _ = Arguments(tool.Arguments.ToString());
        }
        for (var index = 0; index < next; index++) yield return new MessageStreamPart { Type = "content_block_stop", Index = index };
        yield return new MessageStreamPart { Type = "message_delta", Delta = new() { StopReason = StopReason(finish, tools.Count != 0, refusal) }, Usage = Usage(usage) };
        if (nativeFields is not null && nativeDeltas is { Count: > 0 }) AddRaw(nativeFields, "choice_deltas", nativeDeltas);
        var metadata = GatewayPricing.CostMetadata(cost);
        if (metadataProvider is not null && nativeFields is not null)
            metadata = ProviderMetadata(metadataProvider, nativeFields, usage, metadata);
        else if (nativeFields is not null && usage?.AdditionalProperties?.Count > 0)
            AddRaw(nativeFields, "usage_extensions", usage.AdditionalProperties);
        yield return new MessageStreamPart { Type = "message_stop", Metadata = metadata, AdditionalProperties = metadataProvider is null ? nativeFields : null };
    }

    private static void AddRaw<T>(Dictionary<string, JsonElement> raw, string key, T? value) where T : class
    {
        if (value is not null) raw.TryAdd(key, JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web));
    }

    private static Dictionary<string, JsonElement>? ProviderMetadata(string provider, Dictionary<string, JsonElement> raw,
        ChatCompletionUsage? usage, Dictionary<string, JsonElement>? metadata)
    {
        var scoped = new Dictionary<string, JsonElement>();
        if (raw.Remove("providerMetadata", out var supplied) && supplied.ValueKind == JsonValueKind.Object)
        {
            metadata = metadata is null ? new() : new(metadata);
            foreach (var property in supplied.EnumerateObject()) metadata.TryAdd(property.Name, property.Value);
        }
        raw.Remove("metadata");
        if (raw.Count == 0 && usage is null) return metadata;
        if (metadata?.GetValueOrDefault(provider) is { ValueKind: JsonValueKind.Object } existing)
            foreach (var property in existing.EnumerateObject()) scoped[property.Name] = property.Value;
        if (raw.Count != 0) AddRaw(scoped, "raw", raw);
        AddRaw(scoped, "usage", usage);
        var result = metadata is null ? new Dictionary<string, JsonElement>() : new(metadata);
        result[provider] = JsonSerializer.SerializeToElement(scoped, JsonSerializerOptions.Web);
        return result;
    }

    private static MessageStreamPart Start(int index, MessageContentBlock block)
        => new() { Type = "content_block_start", Index = index, ContentBlock = block };

    private static MessageStreamPart Delta(int index, MessageStreamDelta delta)
        => new() { Type = "content_block_delta", Index = index, Delta = delta };

    private static JsonElement Arguments(string? value)
    {
        try { return MessagesHistory.ParseArguments(string.IsNullOrEmpty(value) ? "{}" : value); }
        catch (ContractException error) { throw new IOException("Invalid upstream function arguments.", error); }
    }

    private static MessagesUsage? Usage(ChatCompletionUsage? usage)
    {
        if (usage is null) return null;
        var cached = usage.PromptTokensDetails?.CachedTokens;
        var written = usage.PromptTokensDetails?.CacheWriteTokens;
        return new MessagesUsage { InputTokens = Math.Max(0, usage.PromptTokens - (cached ?? 0) - (written ?? 0)),
            OutputTokens = usage.CompletionTokens, CacheReadInputTokens = cached, CacheCreationInputTokens = written,
            OutputTokensDetails = usage.CompletionTokensDetails?.ReasoningTokens is { } thinking ? new() { ThinkingTokens = thinking } : null };
    }

    private static string StopReason(string? reason, bool tools, bool refusal) => reason switch
    {
        "length" => "max_tokens", "content_filter" => "refusal", "tool_calls" => "tool_use",
        _ when refusal => "refusal", _ when tools => "tool_use", _ => "end_turn"
    };

    private static ContractException Unsupported(string feature) => new($"This {feature} has no Chat Completions translation.");

    private sealed class ToolState
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public int? Index { get; set; }
        public StringBuilder Arguments { get; } = new();
    }
}
