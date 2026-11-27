using System.Text.Json;
using Streaik.ChatCompletions.Models;
using Streaik.Messages;
using Streaik.Providers;

namespace Streaik.Gateway;

// Use provider-reported output counts for performance even when the Chat client did not request a usage frame.
internal sealed class BridgeChatFinish : ChatCompletionUpdate
{
    [System.Text.Json.Serialization.JsonIgnore]
    public int? OutputTokens { get; init; }
}

internal static partial class ProtocolAdapters
{
    public static async Task ChatViaMessagesAsync(GatewayCall call, ChatCompletionOptions request, IMessagesProvider provider)
    {
        StreaikGateway.ValidateAnthropicMessageInputs(call, request.MaxCompletionTokens ?? request.MaxTokens);
        var native = ToMessages(request);
        if (request.Stream == true)
            await call.WriteChatStreamAsync(ChatUpdates(call, provider.StreamMessageAsync(native, call.Headers, call.Ct), request.StreamOptions?.IncludeUsage == true));
        else await call.WriteAsync(ChatResult(await provider.CreateMessageAsync(native, call.Headers, call.Ct)));
    }

    public static async Task ChatViaResponsesAsync(GatewayCall call, ChatCompletionOptions request, IResponsesProvider provider)
    {
        var metadataProvider = call.Get<INativeChatOptionsProvider>() is not null ? call.ProviderId : null;
        var nativeOptions = metadataProvider is not null && request.AdditionalProperties?.Remove(metadataProvider, out var options) == true
            ? new Dictionary<string, JsonElement> { [metadataProvider] = options } : null;
        var native = ToResponses(ToMessages(request));
        native.AdditionalProperties = nativeOptions;
        native.MaxOutputTokens = request.MaxCompletionTokens ?? request.MaxTokens;
        if (request.Stream == true)
            await call.WriteChatStreamAsync(ChatUpdates(call, MessageUpdates(provider.StreamResponseAsync(native, call.Headers, call.Ct), metadataProvider),
                request.StreamOptions?.IncludeUsage == true, metadataProvider));
        else
        {
            var response = await provider.CreateResponseAsync(native, call.Headers, call.Ct);
            var result = ChatResult(ToMessages(response));
            if (metadataProvider is not null) result.MetadataExtensions = ResponseBridgeMetadata(response, metadataProvider);
            await call.WriteAsync(result);
        }
    }

    private static MessagesRequest ToMessages(ChatCompletionOptions request)
    {
        if (request.NumberOfChoices is not (null or 1) || request.ResponseFormat is not null || request.Functions is not null
            || request.FunctionCall is not null || request.Store == true || request.Logprobs == true || request.AdditionalProperties?.Count > 0
            || request.Modalities?.Any(m => m != "text") == true || request.WebSearchOptions is not null
            || request.Audio is not null || request.Prediction is not null || request.ReasoningEffort is not null
            || request.FrequencyPenalty is not null || request.PresencePenalty is not null || request.LogitBias is not null
            || request.Seed is not null || request.TopLogprobs is not null || request.PromptCacheOptions is not null
            || request.Moderation is not null || request.Verbosity is not null || request.StreamOptions?.IncludeObfuscation == true)
            throw new ContractException("This Chat Completions option has no Messages translation.");
        var history = new MessagesHistory();
        foreach (var message in request.Messages)
        {
            if (message.Audio is not null || message.Refusal is not null || message.FunctionCall is not null
                || message.AdditionalProperties?.Count > 0 || message.Name is not null)
                throw new ContractException("This Chat Completions message has no Messages translation.");
            var text = message.Content.Text ?? string.Concat((message.Content.Parts ?? []).Select(p =>
                p is ChatMessageTextPart { PromptCacheBreakpoint: null, AdditionalProperties: null } part ? part.Text
                    : throw new ContractException("This Chat Completions adapter supports only text messages.")));
            if (message.Role == "tool") history.Result(message.ToolCallId ?? throw new ContractException("Missing tool_call_id."), text);
            else
            {
                history.Text(message.Role, text);
                foreach (var tool in message.ToolCalls ?? [])
                    if (message.Role == "assistant" && tool is ChatCompletionMessageToolCall function)
                        history.Tool(function.Id, function.Function.Name, MessagesHistory.ParseArguments(function.Function.Arguments));
                    else throw new ContractException("Only assistant function calls can be translated to Messages.");
            }
        }
        var native = history.Build(request.Model, request.MaxCompletionTokens ?? request.MaxTokens, request.Temperature, request.TopP, request.Stream);
        native.StopSequences = request.Stop?.Sequences?.ToList() ?? (request.Stop?.Sequence is { } stop ? [stop] : null);
        var tools = (request.Tools ?? []).Select(tool => tool is ChatCompletionTool function
            ? new MessageToolDefinition { Name = function.Function.Name, Description = function.Function.Description,
                InputSchema = function.Function.Parameters, Strict = function.Function.Strict }
            : throw new ContractException("Only function tools can be translated to Messages.")).ToList();
        if (request.ToolChoice != "none" && tools.Count != 0) native.Tools = tools;
        if (native.Tools is not null)
            native.ToolChoice = request.ToolChoiceOption switch
            {
                ChatCompletionNamedToolChoice named => new MessageToolChoice { Type = "tool", Name = named.Function.Name },
                _ when request.ToolChoice is "auto" or "required" or null => new MessageToolChoice { Type = request.ToolChoice == "required" ? "any" : "auto" },
                _ => throw new ContractException("Unsupported tool_choice.")
            };
        if (native.ToolChoice is not null && request.ParallelToolCalls.HasValue)
            native.ToolChoice.DisableParallelToolUse = !request.ParallelToolCalls.Value;
        return native;
    }

    private static ChatCompletion ChatResult(MessagesResponse result)
    {
        if (result.Content.Any(b => b.Type is not ("text" or "tool_use") || b.Citations?.Count > 0))
            throw new IOException("This Messages result cannot be represented as Chat Completions.");
        var text = string.Concat(result.Content.Where(b => b.Type == "text").Select(b => b.Text));
        var tools = result.Content.Where(b => b.Type == "tool_use").Select(b => new ChatCompletionMessageToolCall
            { Id = b.Id!, Function = new ChatCompletionFunctionCall { Name = b.Name!, Arguments = b.Input?.GetRawText() ?? "{}" } }).ToList();
        return new ChatCompletion { Id = result.Id!, Model = result.Model!, Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Usage = ChatUsage(result.Usage),
            MetadataExtensions = GatewayPricing.CostMetadata(GatewayPricing.Cost(result.Metadata)),
            Choices = [new ChatCompletionChoice { FinishReason = ChatStop(result.StopReason),
                Message = new ChatCompletionResponseMessage { Content = text.Length == 0 && tools.Count != 0 ? null : text, ToolCalls = tools.Count == 0 ? null : tools } }] };
    }

    private static async IAsyncEnumerable<ChatCompletionUpdate> ChatUpdates(GatewayCall call, IAsyncEnumerable<MessageStreamPart> source, bool includeUsage,
        string? metadataProvider = null)
    {
        var id = "";
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var usage = new MessagesUsage();
        var tools = new Dictionary<int, (int Index, bool HasInput)>();
        string? stop = null;
        string? model = null;
        GatewayPrice? cost = null;
        ChatCompletionUpdate Chunk(ChatCompletionDelta delta, string? finish = null) => new()
        { Id = id, Created = created, Choices = [new ChatCompletionStreamChoice { Delta = delta, FinishReason = finish }] };
        ChatCompletionDelta ToolDelta(int index, string arguments, string? call = null, string? name = null) => new()
        { ToolCalls = [new ChatCompletionToolCallChunk { Index = index, Id = call, Type = call is null ? null : "function",
            Function = new ChatCompletionFunctionCallDelta { Name = name, Arguments = arguments } }] };

        await foreach (var part in source)
        {
            var index = part.Index ?? -1;
            switch (part)
            {
                case { Type: "message_start", Message: { } message }:
                    id = message.Id!;
                    model = message.Model;
                    cost = GatewayPricing.Cost(message.Metadata) ?? cost;
                    MessagesHistory.MergeUsage(usage, message.Usage);
                    yield return Chunk(new ChatCompletionDelta { Role = "assistant", Content = "" }); break;
                case { Type: "content_block_start", ContentBlock: { Type: "text" } block }:
                    if (!string.IsNullOrEmpty(block.Text)) yield return Chunk(new ChatCompletionDelta { Content = block.Text });
                    break;
                case { Type: "content_block_start", ContentBlock: { Type: "tool_use" } block }:
                    var input = block.Input?.GetRawText() ?? "{}";
                    var toolIndex = tools.Count;
                    tools.Add(index, (toolIndex, input != "{}"));
                    yield return Chunk(ToolDelta(toolIndex, input != "{}" ? input : "", block.Id, block.Name)); break;
                case { Type: "content_block_delta", Delta.Text: { } text }:
                    yield return Chunk(new ChatCompletionDelta { Content = text }); break;
                case { Type: "content_block_delta", Delta.PartialJson: { } arguments } when tools.TryGetValue(index, out var tool):
                    tools[index] = (tool.Index, true);
                    yield return Chunk(ToolDelta(tool.Index, arguments)); break;
                case { Type: "content_block_stop" } when tools.TryGetValue(index, out var empty) && !empty.HasInput:
                    yield return Chunk(ToolDelta(empty.Index, "{}")); break;
                case { Type: "message_delta" }:
                    cost = GatewayPricing.Cost(part.Metadata) ?? cost;
                    stop = part.Delta?.StopReason ?? stop;
                    MessagesHistory.MergeUsage(usage, part.Usage); break;
                case { Type: "message_stop" }:
                    var finish = new BridgeChatFinish { Id = id, Model = model!, Created = created, OutputTokens = usage.OutputTokens,
                        Choices = [new ChatCompletionStreamChoice { FinishReason = ChatStop(stop) }] };
                    cost = GatewayPricing.Cost(part.Metadata) ?? cost;
                    if (metadataProvider is not null && part.Metadata?.TryGetValue(metadataProvider, out var raw) == true)
                        finish.AdditionalProperties = new() { ["metadata"] = JsonSerializer.SerializeToElement(new Dictionary<string, JsonElement> { [metadataProvider] = raw }) };
                    if (!includeUsage) finish.AdditionalProperties = GatewayPricing.AttachChat(finish.AdditionalProperties, cost ?? await GatewayPricing.EstimateAsync(call, model, usage));
                    yield return finish;
                    if (includeUsage) yield return new ChatCompletionUpdate { Id = id, Model = model!, Created = created, Usage = ChatUsage(usage),
                        AdditionalProperties = GatewayPricing.AttachChat(null, cost) };
                    yield break;
                case { Type: "error" or "content_block_start" or "content_block_delta" }:
                    throw new IOException("This Messages event cannot be represented as Chat Completions.");
            }
        }
        throw new IOException("Messages stream ended without message_stop.");
    }

    private static string ChatStop(string? stop) => stop switch
    { "max_tokens" or "model_context_window_exceeded" => "length", "tool_use" => "tool_calls", "refusal" => "content_filter", _ => "stop" };

    private static ChatCompletionUsage ChatUsage(MessagesUsage? usage)
    {
        var input = MessagesHistory.TotalInput(usage) ?? 0;
        var output = usage?.OutputTokens ?? 0;
        return new ChatCompletionUsage { PromptTokens = input, CompletionTokens = output, TotalTokens = input + output,
            PromptTokensDetails = usage?.CacheReadInputTokens is not null || usage?.CacheCreationInputTokens is not null
                ? new() { CachedTokens = usage.CacheReadInputTokens, CacheWriteTokens = usage.CacheCreationInputTokens } : null };
    }
}
