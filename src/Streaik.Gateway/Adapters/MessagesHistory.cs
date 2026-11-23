using System.Text.Json;
using Streaik.Messages;

namespace Streaik.Gateway;

// Builds native Messages history and coalesces adjacent messages with the same role.
internal sealed class MessagesHistory
{
    private readonly List<MessageParam> _messages = [];
    private readonly List<MessageContentBlock> _instructions = [];

    public void Add(string role, MessageContentBlock block)
    {
        if (role is "system" or "developer") { _instructions.Add(block); return; }
        if (role is not ("assistant" or "user")) throw new ContractException($"Unsupported message role: '{role}'.");
        if (_messages.LastOrDefault() is not { } last || last.Role != role)
        {
            last = new MessageParam { Role = role, Content = new MessagesContent(new List<MessageContentBlock>()) };
            _messages.Add(last);
        }
        last.Content.Blocks!.Add(block);
    }

    public void Text(string role, string? text)
    { if (!string.IsNullOrEmpty(text)) Add(role, new MessageContentBlock { Type = "text", Text = text }); }

    public void Tool(string id, string name, JsonElement input)
        => Add("assistant", new MessageContentBlock { Type = "tool_use", Id = id, Name = name, Input = input });

    public void Result(string id, string text, bool? isError = null)
        => Add("user", new MessageContentBlock { Type = "tool_result", ToolUseId = id, Content = new MessagesContent(text), IsError = isError });

    public MessagesRequest Build(string model, int? limit, double? temperature, double? topP, bool? stream)
    {
        if (_messages.Count == 0) throw new ContractException("At least one user or assistant message is required.");
        return new MessagesRequest { Model = model, MaxTokens = limit ?? 4096, Messages = _messages,
            System = _instructions.Count == 0 ? null : new MessagesContent(_instructions), Temperature = temperature, TopP = topP, Stream = stream };
    }

    public static JsonElement ParseArguments(string value)
    {
        try
        {
            using var document = JsonDocument.Parse(value);
            if (document.RootElement.ValueKind == JsonValueKind.Object) return document.RootElement.Clone();
        }
        catch (JsonException) { }
        throw new ContractException("Tool arguments must be a JSON object.");
    }

    public static int? TotalInput(MessagesUsage? usage) => usage?.InputTokens is { } input
        ? input + (usage.CacheCreationInputTokens ?? 0) + (usage.CacheReadInputTokens ?? 0) : null;

    public static void MergeUsage(MessagesUsage total, MessagesUsage? update)
    {
        if (update is null) return;
        total.InputTokens = update.InputTokens ?? total.InputTokens;
        total.OutputTokens = update.OutputTokens ?? total.OutputTokens;
        total.CacheReadInputTokens = update.CacheReadInputTokens ?? total.CacheReadInputTokens;
        total.CacheCreationInputTokens = update.CacheCreationInputTokens ?? total.CacheCreationInputTokens;
        total.OutputTokensDetails = update.OutputTokensDetails ?? total.OutputTokensDetails;
        total.ServerToolUse = update.ServerToolUse ?? total.ServerToolUse;
        if (update.AdditionalProperties is { Count: > 0 } fields)
        {
            total.AdditionalProperties ??= [];
            foreach (var (key, value) in fields) total.AdditionalProperties[key] = value;
        }
    }
}
