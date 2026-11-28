using System.Text.Json;
using Streaik.Responses;
using Streaik.Responses.Streaming;
using Streaik.Vercel.Models;

namespace Streaik.Gateway;

internal static partial class VercelAdapter
{
    private const string OpenAIReplayKey = "responsesReplay";
    private const string OpenAIHostedKey = "responsesHosted";

    private static bool OpenAIMultiAgent(GatewayCall call, ChatRequest request) => call.ProviderId == "openai"
        && Option(request, "openai", "multi_agent") is { ValueKind: JsonValueKind.Object } option
        && option.TryGetProperty("enabled", out var value) && value.ValueKind == JsonValueKind.True;

    private static string? StringField(JsonElement value, string field) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(field, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    private static bool IsSubagent(ResponseAgent? agent) => agent?.AgentName is { Length: > 0 } name && name != "/root";

    private static bool IsSubagent(JsonElement raw) => raw.ValueKind == JsonValueKind.Object && raw.TryGetProperty("agent", out var agent)
        && StringField(agent, "agent_name") is { Length: > 0 } name && name != "/root";

    private static IEnumerable<List<UIMessagePart>> ResponseSteps(IEnumerable<UIMessagePart> parts)
    {
        List<UIMessagePart> step = [];
        foreach (var part in parts)
        {
            if (step.Count != 0 && part is (StepStartUIPart or StartStepUIPart))
            {
                yield return step;
                step = [];
            }
            step.Add(part);
        }
        if (step.Count != 0) yield return step;
    }

    private static JsonElement[] ReadOpenAIHistory(List<UIMessagePart> step, string model)
    {
        foreach (var part in step)
        {
            var data = part switch
            {
                ToolInvocationPart tool when IsOpenAIHostedTool(tool) => tool.ResultProviderMetadata?.GetValueOrDefault("openai")?.GetValueOrDefault(OpenAIReplayKey),
                TextUIPart text when text.ProviderMetadata?.GetValueOrDefault("openai") is JsonElement { ValueKind: JsonValueKind.Object } raw
                    && raw.TryGetProperty(OpenAIReplayKey, out var replay) => replay,
                ReasoningUIPart thought when thought.ProviderMetadata?.GetValueOrDefault("openai") is JsonElement { ValueKind: JsonValueKind.Object } raw
                    && raw.TryGetProperty(OpenAIReplayKey, out var replay) => replay,
                _ => null
            };
            if (data is not JsonElement { ValueKind: JsonValueKind.Object } snapshot || StringField(snapshot, "model") != model
                || !snapshot.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) continue;
            return items.EnumerateArray().ToArray();
        }
        return [];
    }

    private static bool IsOpenAIHostedTool(ToolInvocationPart tool) => tool.ProviderExecuted == true && tool.Approval is null
        && tool.CallProviderMetadata?.GetValueOrDefault("openai")?.GetValueOrDefault(OpenAIHostedKey) is JsonElement { ValueKind: JsonValueKind.True };

    private static HashSet<string> AppendOpenAIHistory(JsonElement[] snapshot, List<UIMessagePart> step, List<ResponseInputItem> input, HashSet<string> seen)
    {
        HashSet<string> functions = new(StringComparer.Ordinal);
        foreach (var raw in snapshot)
        {
            if (StringField(raw, "id") is not { Length: > 0 } id || seen.Contains(id)) continue;
            var type = StringField(raw, "type");
            // MCP approvals/results, gateway-hosted functions, container state and file references must use their existing trust checks.
            if (type is not ("message" or "reasoning" or "web_search_call" or "file_search_call" or "multi_agent_call"
                or "multi_agent_call_output" or "agent_message" or "compaction" or "function_call")) continue;
            if (type == "message" && !ReplayOpenAIMessage(raw)) continue;
            ResponseInputItem? native;
            try { native = raw.Deserialize<ResponseInputItem>(ResponseJson.Default); }
            catch (JsonException) { continue; }
            if (native is null) continue;
            if (native is ResponseFunctionCallItem function)
            {
                if (string.IsNullOrEmpty(function.CallId) || GatewayHostedTools.IsReserved(function.Name) || functions.Contains(function.CallId)) continue;
                var tool = step.OfType<ToolInvocationPart>().FirstOrDefault(tool => tool.ProviderExecuted != true && tool.ToolCallId == function.CallId
                    && ToolName(tool) == function.Name && !HostedMcpChatApproval.IsContinuation(tool) && !UpstreamMcpReview.IsReview(tool));
                if (tool is null) continue;
                var space = tool.ToolMetadata?.GetValueOrDefault("namespace");
                if (function.Namespace != (space is { ValueKind: JsonValueKind.String } name ? name.GetString() : null)) continue;
                // The validated UI tool part owns its arguments. Keep the wire form only when the JSON is equivalent.
                var args = ToolInput(tool.Input);
                try
                {
                    using var parsed = JsonDocument.Parse(function.Arguments);
                    if (!JsonElement.DeepEquals(parsed.RootElement, args)) continue;
                }
                catch (JsonException) { continue; }
                functions.Add(function.CallId);
            }
            seen.Add(id);
            input.Add(native);
        }
        return functions;
    }

    private static bool ReplayOpenAIMessage(JsonElement raw)
    {
        if (!raw.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return false;
        return StringField(raw, "role") switch
        {
            "assistant" => content.EnumerateArray().All(part => StringField(part, "type") is "output_text" or "refusal"),
            // Subagents may echo the user prompt; remote attachments still go through the normal attachment path.
            "user" when IsSubagent(raw) => content.EnumerateArray().All(part => StringField(part, "type") == "input_text"),
            _ => false
        };
    }

    private sealed class OpenAIAgentOutput(string model)
    {
        private sealed class Invocation
        {
            public string? InputKey { get; set; }
            public string? OutputKey { get; set; }
            public Dictionary<string, object>? Output { get; set; }
        }
        private readonly Dictionary<string, Invocation> _tools = new(StringComparer.Ordinal);
        private static readonly JsonSerializerOptions Json = ResponseJson.Default;
        private object? _fallbackReplay;

        private static bool Projected(string? type) => type is "multi_agent_call" or "multi_agent_call_output" or "web_search_call";

        public IEnumerable<UIMessagePart> Observe(ResponseStreamPart frame)
        {
            if (frame is not ResponseStreamItemEvent { Item: { } item } || !Projected(item.Type)
                || frame is ResponseOutputItemAdded && item.Type == "multi_agent_call_output") return [];
            return Project(JsonSerializer.SerializeToElement(item, Json), frame is ResponseOutputItemDone, fallbackAgent: frame.Agent?.AgentName);
        }

        private static void ReadFields(Dictionary<string, object> target, string? json)
        {
            if (string.IsNullOrEmpty(json)) return;
            try
            {
                using var data = JsonDocument.Parse(json);
                if (data.RootElement.ValueKind != JsonValueKind.Object) return;
                foreach (var field in data.RootElement.EnumerateObject())
                    if (field.Name is ("task_name" or "target" or "timeout_ms" or "timed_out" or "fork_turns")
                        && field.Value.ValueKind is (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False))
                        target[field.Name] = field.Value.Clone();
            }
            catch (JsonException) { }
        }

        private IEnumerable<UIMessagePart> Project(JsonElement item, bool finished, bool terminal = false, string? fallbackAgent = null)
        {
            var type = StringField(item, "type");
            var search = type == "web_search_call";
            var result = type == "multi_agent_call_output";
            var id = StringField(item, search ? "id" : "call_id");
            var name = search ? "web_search" : StringField(item, "action");
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name)) yield break;
            var agent = (item.TryGetProperty("agent", out var owner) ? StringField(owner, "agent_name") : null) ?? fallbackAgent ?? "/root";
            Dictionary<string, Dictionary<string, object>?> metadata = new() { ["openai"] = new() { [OpenAIHostedKey] = true, ["agent"] = agent } };
            if (!_tools.TryGetValue(id, out var tool))
            {
                _tools[id] = tool = new();
                if (!finished) yield return new ToolCallStreamingStartPart { ToolCallId = id, ToolName = name,
                    ProviderExecuted = true, Dynamic = true, ProviderMetadata = metadata };
            }
            if (!finished) yield break;
            var inputChanged = false;
            if (tool.InputKey is null || !result)
            {
                Dictionary<string, object> input = new() { ["agent"] = agent };
                if (search && item.TryGetProperty("action", out var action) && action.ValueKind == JsonValueKind.Object)
                {
                    foreach (var field in action.EnumerateObject())
                        if (field.Name is "type" or "query" or "queries" or "url" or "pattern") input[field.Name] = field.Value;
                }
                else ReadFields(input, StringField(item, "arguments"));
                var key = JsonSerializer.Serialize(input, Json);
                if (tool.InputKey != key)
                {
                    inputChanged = true;
                    tool.InputKey = key;
                    yield return new ToolCallPart { ToolCallId = id, ToolName = name, Input = input, ProviderExecuted = true, Dynamic = true,
                        ProviderMetadata = metadata };
                }
            }
            if (result || search || terminal && tool.Output is null)
            {
                var status = search ? StringField(item, "status") : result ? "completed" : "no_result";
                Dictionary<string, object> output = new() { ["status"] = status is "searching" or "in_progress" ? "stopped" : status ?? "completed" };
                if (status == "failed") output["isError"] = true;
                if (result && item.TryGetProperty("output", out var parts) && parts.ValueKind == JsonValueKind.Array)
                    ReadFields(output, StringField(parts.EnumerateArray().FirstOrDefault(part => StringField(part, "type") == "output_text"), "text"));
                var key = JsonSerializer.Serialize(output, Json);
                if (tool.OutputKey != key)
                {
                    tool.OutputKey = key;
                    tool.Output = output;
                    inputChanged = true;
                }
            }
            if (inputChanged && tool.Output is { } current) yield return Result(id, current);
        }

        private static ToolOutputAvailablePart Result(string id, object output, object? replay = null) => new()
        {
            ToolCallId = id, Output = output, Dynamic = true, ProviderExecuted = true,
            ProviderMetadata = replay is null ? null : new() { ["openai"] = new() { [OpenAIReplayKey] = replay } }
        };

        public IEnumerable<UIMessagePart> Complete(ResponseResult result, bool complete)
        {
            var items = result.Output.Select(item => item is JsonElement element ? element
                : JsonSerializer.SerializeToElement(item, item.GetType(), Json)).ToArray();
            var results = items.Where(item => StringField(item, "type") == "multi_agent_call_output")
                .Select(item => StringField(item, "call_id")).ToHashSet(StringComparer.Ordinal);
            foreach (var item in items.Where(item => Projected(StringField(item, "type"))))
                foreach (var part in Project(item, true, !results.Contains(StringField(item, "call_id")))) yield return part;
            if (!complete || items.Length == 0) yield break;
            var replay = new { model, items };
            var anchor = _tools.LastOrDefault(pair => pair.Value.Output is not null);
            if (anchor.Value?.Output is { } output) yield return Result(anchor.Key, output, replay);
            else _fallbackReplay = replay;
        }

        public Dictionary<string, object>? WithFallback(Dictionary<string, object>? metadata)
        {
            if (_fallbackReplay is not { } replay) return metadata;
            metadata ??= new();
            metadata[OpenAIReplayKey] = replay;
            _fallbackReplay = null;
            return metadata;
        }
    }
}
