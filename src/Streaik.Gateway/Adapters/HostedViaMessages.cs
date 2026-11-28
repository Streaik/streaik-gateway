using System.Text;
using System.Text.Json;
using Streaik.Messages;
using Streaik.Providers;
using Streaik.Responses;
using Streaik.Responses.Streaming;
using Streaik.Tools;

namespace Streaik.Gateway;

internal static partial class ProtocolAdapters
{
    // Internal only: a lossy Responses projection cannot be used to replay a Messages turn.
    private sealed class NativeAssistantTurn(List<MessageContentBlock> blocks) : ResponseInputItem
    {
        internal List<MessageContentBlock> Blocks { get; } = blocks;
    }

    private static void AddHostedMessageHistory(MessagesHistory history, IEnumerable<ResponseInputItem> items)
    {
        foreach (var item in items)
            switch (item)
            {
                case NativeAssistantTurn turn:
                    foreach (var block in turn.Blocks) history.Add("assistant", block);
                    break;
                case ResponseFunctionCallOutputItem { CallId: { } id, StructuredOutput: null } result:
                    history.Result(id, result.Output);
                    break;
                default: throw new InvalidOperationException("Unexpected hosted Messages continuation item.");
            }
    }

    private static MessagesRequest HostedChatHistory(MessagesRequest native, IReadOnlyList<MessageParam> initial,
        MessagesContent? system, ResponseRequest round)
    {
        var history = new MessagesHistory();
        foreach (var message in initial)
        {
            history.Text(message.Role, message.Content.Text);
            foreach (var block in message.Content.Blocks ?? []) history.Add(message.Role, block);
        }
        history.Text("system", system?.Text);
        foreach (var block in system?.Blocks ?? []) history.Add("system", block);
        history.Text("system", round.Instructions);
        AddHostedMessageHistory(history, round.Input?.Items ?? []);
        return history.Build(native.Model, native.MaxTokens, native.Temperature, native.TopP, stream: true);
    }

    internal static MessagesRequest PrepareHostedCompletionsRound(MessagesRequest initial, ResponseRequest round,
        MessageToolChoice? choice)
    {
        var request = HostedChatHistory(initial, initial.Messages, initial.System, round);
        if (choice?.Type != "none")
        {
            request.Tools = [.. initial.Tools ?? [], .. (round.Tools ?? []).Select(MessageFunction)];
            if (request.Tools.Count > 0) request.ToolChoice = new()
            {
                Type = choice?.Type ?? "auto", Name = choice?.Name, DisableParallelToolUse = true
            };
        }
        return request;
    }

    internal static void PrepareHostedChatRound(MessagesRequest native, IReadOnlyList<MessageParam> initial,
        MessagesContent? system, IEnumerable<JsonElement> tools, ResponseRequest round)
    {
        var request = HostedChatHistory(native, initial, system, round);
        native.Messages = request.Messages;
        native.System = request.System;
        if (native.ToolChoice?.Type != "none")
        {
            var functions = (round.Tools ?? []).Select(MessageFunction)
                .Select(tool => JsonSerializer.SerializeToElement(tool, MessagesJson.Default));
            var all = tools.Concat(functions).ToArray();
            if (all.Length == 0)
            {
                native.AdditionalProperties?.Remove("tools");
                native.Tools = null;
                native.ToolChoice = null;
                return;
            }
            (native.AdditionalProperties ??= new(StringComparer.Ordinal))["tools"] = JsonSerializer.SerializeToElement(all);
            native.Tools = null;
            (native.ToolChoice ??= new MessageToolChoice { Type = "auto" }).DisableParallelToolUse = true;
        }
    }

    internal static Func<ResponseRequest, Task<GatewayHostedTools.Round>> HostedMessagesReader(GatewayCall call,
        IMessagesProvider provider, ResponseRequest initial, McpToolDefinition[] mcp)
    {
        StreaikGateway.ValidateAnthropicMessageInputs(call, initial.MaxOutputTokens);
        var listings = MessagesMcp.CanReplayListings(call.Headers);
        // Validate connector options before the gateway opens any hosted sessions.
        if (mcp.Length != 0)
            MessagesMcp.Add(mcp, new MessagesRequest { Model = call.NativeModel, MaxTokens = initial.MaxOutputTokens!.Value, Messages = [] }, call.Headers);
        IReadOnlyList<ResponseInputItem> previousMcp = [];
        return async round =>
        {
            var request = ToMessages(round, mcp, listings);
            var headers = mcp.Length == 0 ? call.Headers : MessagesMcp.Add(mcp, request, call.Headers);
            if (mcp.Length != 0 && round.ToolChoice is JsonElement { ValueKind: JsonValueKind.String } choice && choice.ValueEquals("none"))
                request.ToolChoice = new() { Type = "none" };
            var result = await ReadHostedMessagesRoundAsync(call, provider, request, headers, nativeMcp: mcp.Length != 0, earlierMcp: previousMcp);
            // Intermediate rounds are not streamed to the client. Keep their provider MCP activity in the final response.
            previousMcp = result.Result.OutputItems.Where(item => item is ResponseMcpListToolsItem
                or ResponseMcpCallItem { Status: "completed" or "failed" }).ToArray();
            return result;
        };
    }

    internal static async Task<GatewayHostedTools.Round> ReadHostedMessagesRoundAsync(GatewayCall call, IMessagesProvider provider,
        MessagesRequest request, IReadOnlyDictionary<string, string> headers, bool nativeMcp,
        Action<IReadOnlyList<MessageStreamPart>>? captureChat = null, IReadOnlyList<ResponseInputItem>? earlierMcp = null)
    {
        if (request.Stream != true)
        {
            var message = await provider.CreateMessageAsync(request, headers, call.Ct);
            return new(MessagesRoundResult(call, message, nativeMcp, hosted: true, earlierMcp: earlierMcp), [],
                NativeAssistantTurn: new NativeAssistantTurn(message.Content.ToList()));
        }

        return await ReadHostedMessageUpdatesAsync(call, provider.StreamMessageAsync(request, headers, call.Ct), nativeMcp, captureChat, earlierMcp);
    }

    internal static async Task<GatewayHostedTools.Round> ReadHostedMessageUpdatesAsync(GatewayCall call, IAsyncEnumerable<MessageStreamPart> source,
        bool nativeMcp = false, Action<IReadOnlyList<MessageStreamPart>>? captureChat = null, IReadOnlyList<ResponseInputItem>? earlierMcp = null)
    {
        var events = new List<MessageStreamPart>();
        var blocks = new SortedDictionary<int, (MessageContentBlock Block, StringBuilder Arguments)>();
        var id = "";
        string? stop = null;
        async IAsyncEnumerable<MessageStreamPart> Read()
        {
            var count = 0;
            await foreach (var part in source.WithCancellation(call.Ct))
            {
                if (++count > 16384) throw new ContractException("The hosted response exceeded the stream event limit.", 502);
                if (captureChat is not null) events.Add(part);
                var index = part.Index ?? -1;
                switch (part)
                {
                    case { Type: "message_start", Message: { Id: { Length: > 0 } messageId } }: id = messageId; break;
                    case { Type: "content_block_start", ContentBlock: { } block }:
                        // Response translation also assembles deltas; keep that projection separate from replay history.
                        blocks.Add(index, (JsonSerializer.SerializeToElement(block, MessagesJson.Default)
                            .Deserialize<MessageContentBlock>(MessagesJson.Default)!, new StringBuilder()));
                        break;
                    case { Type: "content_block_delta", Delta: { } delta } when blocks.TryGetValue(index, out var current):
                        if (delta.Text is { } text) current.Block.Text += text;
                        if (delta.Thinking is { } thought) current.Block.Thinking += thought;
                        if (delta.Signature is { } signature) current.Block.Signature += signature;
                        if (delta.PartialJson is { } args) current.Arguments.Append(args);
                        if (delta.Citation is { } citation) (current.Block.Citations ??= []).Add(citation);
                        break;
                    case { Type: "content_block_stop" } when blocks.TryGetValue(index, out var current):
                        if (current.Arguments.Length != 0) current.Block.Input = MessagesHistory.ParseArguments(current.Arguments.ToString());
                        break;
                    case { Type: "message_delta" }: stop = part.Delta?.StopReason ?? stop; break;
                    case { Type: "message_stop" }:
                        if (id.Length == 0) throw new IOException("Missing upstream message ID.");
                        yield return part;
                        yield break;
                    case { Type: "error" }: throw new UpstreamStreamException(part.Error?.Message);
                }
                yield return part;
            }
            throw new IOException("Messages ended without message_stop.");
        }

        List<ResponseStreamPart> responseEvents = [];
        ResponseResult? result = null;
        if (captureChat is not null)
        {
            await foreach (var _ in Read()) { }
            captureChat(events);
            var calls = blocks.Where(entry => entry.Value.Block.Type == "tool_use")
                .Select(entry => OutputItem(id, entry.Key, entry.Value.Block)).ToList();
            result = ResponseResultFrom(id, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), calls, null, stop);
        }
        else
        {
            await foreach (var part in ResponseUpdates(Read(), nativeMcp: nativeMcp, hosted: true, earlierMcp: earlierMcp))
            {
                responseEvents.Add(part);
                if (part is ResponseCompleted completed) result = completed.Response;
                else if (part is ResponseIncompleteEvent incomplete) result = incomplete.Response;
            }
        }
        return new(result ?? throw new IOException("Missing hosted Messages result."), responseEvents,
            NativeAssistantTurn: new NativeAssistantTurn(blocks.Values.Select(value => value.Block).ToList()));
    }
}
