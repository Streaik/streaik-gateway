using System.Text.Json;
using Streaik.Messages;
using Streaik.Responses;

namespace Streaik.Gateway;

// Translate the shared effort subset only. Native-only controls remain valid on
// native routes, but must not be silently discarded when crossing protocols.
internal static class ReasoningBridge
{
    internal static string? FromResponses(Reasoning? reasoning)
    {
        if (reasoning is null) return null;
        if (reasoning.Summary is null && (reasoning.AdditionalProperties?.Count is null or 0) && IsPortable(reasoning.Effort))
            return reasoning.Effort;
        throw new ContractException("This Responses reasoning option has no translation.");
    }

    internal static string? FromMessages(MessagesRequest request)
    {
        if (request.Thinking is null && request.OutputConfig is null) return null;
        if ((request.Thinking is null or { Type: "adaptive", BudgetTokens: null, Display: null })
            && (request.Thinking?.AdditionalProperties?.Count is null or 0)
            && request.OutputConfig is { Format: null } output && (output.AdditionalProperties?.Count is null or 0)
            && IsPortable(output.Effort)) return output.Effort;
        throw new ContractException("This Messages reasoning or output option has no translation.");
    }

    internal static void SetMessagesEffort(MessagesRequest request, string? effort)
    {
        if (effort is null) return;
        if (!IsPortable(effort)) throw new ContractException("This reasoning effort has no Messages translation.");
        request.Thinking = new() { Type = "adaptive" };
        request.OutputConfig = new() { Effort = effort };
    }

    internal static string? ChatText(Dictionary<string, JsonElement>? fields)
        => Text(fields, "reasoning_content") ?? Text(fields, "reasoning");

    internal static Dictionary<string, JsonElement> ChatFields(string text)
        => new() { ["reasoning_content"] = JsonSerializer.SerializeToElement(text) };

    private static string? Text(Dictionary<string, JsonElement>? fields, string key)
        => fields?.GetValueOrDefault(key) is { ValueKind: JsonValueKind.String } value
            && value.GetString() is { Length: > 0 } text ? text : null;

    private static bool IsPortable(string? effort) => effort is "low" or "medium" or "high";
}
