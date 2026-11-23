using System.Text.Json;
using Streaik.ChatCompletions.Models;
using Streaik.Messages;
using Streaik.Responses;
using Streaik.Responses.Streaming;
using Streaik.Vercel.Models;

namespace Streaik.Gateway;

// Observed at the outgoing protocol boundary, once per stream, never on usage-only events.
internal sealed class GatewayPerformance(TimeProvider clock)
{
    private readonly long started = clock.GetTimestamp();
    private long? first;
    private long last;
    private int? outputTokens;

    private void Content(bool hasOutput)
    {
        if (!hasOutput) return;
        last = clock.GetTimestamp();
        first ??= last;
    }

    private void Usage(int? tokens)
    {
        if (tokens >= 0) outputTokens = tokens;
    }

    private Dictionary<string, JsonElement> Merge(Dictionary<string, JsonElement>? metadata)
    {
        var result = metadata is null ? [] : new Dictionary<string, JsonElement>(metadata);
        var gateway = Object(result.GetValueOrDefault("gateway"));
        gateway["ttft"] = JsonSerializer.SerializeToElement(clock.GetElapsedTime(started, first!.Value).TotalSeconds);
        var duration = clock.GetElapsedTime(first.Value, last).TotalSeconds;
        if (outputTokens is { } tokens && duration > 0)
            gateway["tps"] = JsonSerializer.SerializeToElement(tokens / duration);
        result["gateway"] = JsonSerializer.SerializeToElement(gateway);
        return result;
    }

    public void Observe(ChatCompletionUpdate part)
    {
        var finish = false;
        var content = false;
        foreach (var choice in part.Choices ?? [])
        {
            finish |= choice.FinishReason is not null;
            if (choice.Delta is not { } delta) continue;
            var extra = delta.AdditionalProperties;
            content |= Has(delta.Content) || Has(delta.Refusal) || Has(delta.FunctionCall?.Arguments)
                || Has(extra?.GetValueOrDefault("reasoning_content") ?? default)
                || Has(extra?.GetValueOrDefault("reasoning") ?? default)
                || Has(extra?.GetValueOrDefault("thinking") ?? default)
                || (delta.ToolCalls?.Any(call => Has(call.Function?.Arguments)
                    || Has(Property(call.AdditionalProperties?.GetValueOrDefault("custom") ?? default, "input"))) == true);
        }
        Content(content);
        // The Chat DTO defaults missing counters to zero; a cost-only usage object isn't a token count.
        var usage = part.Usage;
        if (part is BridgeChatFinish { OutputTokens: { } output }) Usage(output);
        else if (usage is not null && (usage.TotalTokens > 0 || usage.PromptTokens > 0 || usage.CompletionTokens > 0))
            Usage(usage.CompletionTokens);
        var metadata = part.AdditionalProperties?.GetValueOrDefault("metadata") ?? default;
        if (first is null || !(finish || usage is not null || Property(metadata, "gateway").ValueKind == JsonValueKind.Object)) return;
        if (metadata.ValueKind is not (JsonValueKind.Object or JsonValueKind.Undefined or JsonValueKind.Null)) return;
        (part.AdditionalProperties ??= [])["metadata"] = JsonSerializer.SerializeToElement(Merge(Object(metadata)));
    }

    public void Observe(MessageStreamPart part)
    {
        Content(part.Type == "content_block_delta" && part.Delta is { } delta && (delta.Type switch
        {
            "text_delta" => Has(delta.Text),
            "thinking_delta" => Has(delta.Thinking),
            "input_json_delta" => Has(delta.PartialJson),
            _ => false
        }));
        if (part.Type == "message_delta") Usage(part.Usage?.OutputTokens);
        if (first is not null && part.Type is "message_delta" or "message_stop") part.Metadata = Merge(part.Metadata);
    }

    public void Observe(ResponseStreamPart part, ResponseResult? response)
    {
        Content(part switch
        {
            ResponseOutputTextDelta text => Has(text.Delta),
            ResponseReasoningTextDelta text => Has(text.Delta),
            ResponseReasoningSummaryTextDelta text => Has(text.Delta),
            ResponseRefusalDelta text => Has(text.Delta),
            ResponseFunctionCallArgumentsDelta tool => Has(tool.Delta),
            ResponseMcpCallArgumentsDelta tool => Has(tool.Delta),
            ResponseCustomToolCallInputDelta tool => Has(tool.Delta),
            ResponseCodeInterpreterCallCodeDelta tool => Has(tool.Delta),
            ResponseShellCallCommandDelta tool => Has(tool.Delta),
            _ => false
        });
        if (first is null || response is null || part is not (ResponseCompleted or ResponseIncompleteEvent)) return;
        Usage(response.NormalizedUsage?.OutputTokens);
        var merged = Merge(response.Metadata?.ToDictionary(x => x.Key, x => JsonSerializer.SerializeToElement(x.Value)));
        (response.Metadata ??= [])["gateway"] = merged["gateway"];
    }

    public UIMessagePart Observe(UIMessagePart part)
    {
        Content(part switch
        {
            TextDeltaUIMessageStreamPart text => Has(text.Delta),
            ReasoningDeltaUIPart text => Has(text.Delta),
            ToolCallDeltaPart tool => Has(tool.InputTextDelta),
            _ => false
        });
        if (first is null || part is not FinishUIPart { MessageMetadata: { } metadata } finish) return part;
        Usage(metadata.Usage.CompletionTokens);
        return new FinishUIPart { Type = finish.Type, FinishReason = finish.FinishReason,
            MessageMetadata = metadata with { ProviderMetadata = Merge(metadata.ProviderMetadata) } };
    }

    private static bool Has(string? value) => !string.IsNullOrEmpty(value);
    private static bool Has(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => Has(value.GetString()),
        JsonValueKind.Array => value.EnumerateArray().Any(part =>
            Property(part, "type") is { ValueKind: JsonValueKind.String } type && type.GetString() is "text" or "output_text" or "reasoning_text"
            && Has(Property(part, "text"))),
        _ => false
    };

    private static JsonElement Property(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) ? property : default;

    private static Dictionary<string, JsonElement> Object(JsonElement value) => value.ValueKind == JsonValueKind.Object
        ? value.Deserialize<Dictionary<string, JsonElement>>() ?? [] : [];
}
