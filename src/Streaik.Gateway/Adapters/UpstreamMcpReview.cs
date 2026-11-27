using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Streaik.Responses;
using Streaik.Tools;
using Streaik.Vercel.Models;

namespace Streaik.Gateway;

internal static class UpstreamMcpReview
{
    internal sealed record Result(string Id, string Server, string Name, JsonElement Input, JsonElement Output);
    private sealed record Receipt(string Chat, string Model, string Server, string Name, string Binding, string InputHash,
        string CallId, string? Function);

    internal static bool IsReview(ToolInvocationPart tool) => tool.ToolName == McpResultReviews.ToolName
        || tool.Type == $"tool-{McpResultReviews.ToolName}";

    internal static async Task<IReadOnlyList<UIMessagePart>> CreateAsync(GatewayCall call,
        ChatRequest chat, List<ResponseToolDefinition>? tools, ResponseMcpApprovalRequestItem approval)
    {
        if (approval.Id is not { Length: > 0 and <= 256 } id || approval.ServerLabel is not { Length: > 0 and <= 256 } server
            || approval.Name is not { Length: > 0 and <= 256 } name || approval.Arguments is not { Length: > 0 and <= 65536 } arguments)
            throw new ContractException("The provider returned an invalid MCP approval request.", 502);
        var access = call.Context.RequestServices.GetService<HostedToolAccess>()
            ?? throw new ContractException("Local MCP execution is not authorized on this gateway.", 403);
        try
        {
            var descriptor = Descriptor(tools, server, name);
            using var document = JsonDocument.Parse(arguments, new JsonDocumentOptions { AllowDuplicateProperties = false });
            var references = new GatewayReferences(call, "mcp-result-review");
            var input = document.RootElement;
            var receipt = Issue(references, call, chat, server, name, descriptor.ServerUrl.AbsoluteUri, input, id);
            if (access.AuthorizeMcp is not { } authorize) throw new UnauthorizedAccessException();
            await using var session = new McpToolSession([descriptor], ToolCaller.Direct, authorize, access.CreateMcpHandler);
            if (!(await session.DiscoverAsync(call.Ct)).Any(tool => tool.Name == name)) throw new UnauthorizedAccessException();
            var output = ReviewOutput(await session.InvokeAsync(server, name, id, input, call.Ct));
            return Parts(new(receipt, server, name, input.Clone(), output));
        }
        catch (UnauthorizedAccessException) { throw new ContractException("The gateway did not authorize this MCP call.", 403); }
        catch (JsonException) { throw new ContractException("Invalid upstream MCP approval data.", 502); }
    }

    internal static async Task<Result> ExecuteHostedAsync(GatewayCall call, ChatRequest chat, McpToolDefinition descriptor,
        McpExecutionApproval approval, string function, Func<Task<McpCallOutcome>> execute)
    {
        var references = new GatewayReferences(call, "mcp-result-review");
        var receipt = Issue(references, call, chat, approval.ServerLabel, approval.ToolName, HostedBinding(descriptor), approval.Input,
            approval.CallId, function);
        var output = ReviewOutput(await execute());
        return new(receipt, approval.ServerLabel, approval.ToolName, approval.Input, output);
    }

    private static string Issue(GatewayReferences references, GatewayCall call, ChatRequest chat, string server, string name,
        string binding, JsonElement input, string callId, string? function = null)
    {
        if (chat.Id is not { Length: > 0 and <= 256 } chatId || callId.Length is 0 or > 256 || input.ValueKind != JsonValueKind.Object
            || input.GetRawText().Length > 65536) throw new ContractException("Invalid MCP result review context.", 403);
        return "mrv_" + references.Protect(new Receipt(chatId, call.NativeModel, server, name, binding, InputHash(input), callId, function), historical: true);
    }

    private static JsonElement ReviewOutput(McpCallOutcome outcome)
    {
        var output = outcome switch
        {
            McpCallOutcome.Completed done => done.Output,
            McpCallOutcome.ReviewRequired review => review.Review.Output,
            _ => default
        };
        if (output.ValueKind != JsonValueKind.Object || output.GetRawText().Length > 512 * 1024)
            throw new ContractException("The MCP tool did not return a reviewable result.", 422);
        return output;
    }

    internal static IReadOnlyList<UIMessagePart> Parts(Result result) =>
        [new ToolCallPart { ToolCallId = result.Id, ToolName = McpResultReviews.ToolName, Title = $"Review MCP result: {result.Name}",
            Input = result.Input, ProviderExecuted = true, Dynamic = true, ToolMetadata = new() { ["mcpReviewOutput"] = result.Output } },
         new ToolApprovalRequestUIPart { ToolCallId = result.Id, ApprovalId = result.Id,
            Reason = "Review the completed MCP result before sending it to the model.",
            ApprovalDescriptor = new { server = result.Server, name = result.Name, input = result.Input, output = result.Output } }];

    internal static ResponseInputItem[] Resolve(GatewayCall call, ChatRequest chat,
        List<ResponseToolDefinition> tools, ToolInvocationPart part, HostedToolDefinition[]? hosted)
    {
        if (part.ProviderExecuted != true || part.Approval?.Approved is not { } approved
            || part.Approval.Id != part.ToolCallId || !part.ToolCallId.StartsWith("mrv_", StringComparison.Ordinal) || part.Input is not JsonElement input
            || (approved ? part.State is not ("approval-responded" or "output-available") : part.State is not ("approval-responded" or "output-denied")))
            throw Invalid();
        JsonElement? output = approved ? part.Output as JsonElement? : null;
        if (approved && (output is not { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } value || value.GetRawText().Length > 512 * 1024))
            throw Invalid();
        try
        {
            var receipt = new GatewayReferences(call, "mcp-result-review").Read<Receipt>(part.ToolCallId[4..], historical: true);
            if (receipt.Chat != chat.Id || receipt.Model != call.NativeModel || receipt.InputHash != InputHash(input)) throw Invalid();
            const string denied = "The user chose not to disclose the tool result.";
            if (receipt.Function is { } function)
            {
                var descriptor = (hosted ?? []).OfType<McpToolDefinition>().Where(tool => tool.ServerLabel == receipt.Server).ToArray();
                if (descriptor is not [var server] || HostedBinding(server) != receipt.Binding) throw Invalid();
                return [new ResponseFunctionCallItem { CallId = receipt.CallId, Name = function, Arguments = input.GetRawText() },
                    new ResponseFunctionCallOutputItem { CallId = receipt.CallId, Output = output is { } client ? McpResultReviews.OutputText(client) : denied }];
            }
            if (Descriptor(tools, receipt.Server, receipt.Name).ServerUrl.AbsoluteUri != receipt.Binding) throw Invalid();
            return [new ResponseMcpCallItem { Id = $"mcp_{receipt.CallId}", ServerLabel = receipt.Server, Name = receipt.Name,
                Arguments = input.GetRawText(), Status = approved ? "completed" : "failed",
                Output = output is { } result ? JsonSerializer.SerializeToElement(McpResultReviews.OutputText(result)) : null,
                Error = approved ? null : JsonSerializer.SerializeToElement(denied) }];
        }
        catch (Exception error) when (error is UnauthorizedAccessException or ContractException) { throw Invalid(); }
    }

    private static ContractException Invalid() => new("Unknown or invalid MCP result review.", 403);

    private static string InputHash(JsonElement input)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) Write(input, writer);
        return Convert.ToHexString(SHA256.HashData(buffer.ToArray()));

        static void Write(JsonElement value, Utf8JsonWriter writer)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.Object:
                    writer.WriteStartObject();
                    foreach (var property in value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                    { writer.WritePropertyName(property.Name); Write(property.Value, writer); }
                    writer.WriteEndObject();
                    break;
                case JsonValueKind.Array:
                    writer.WriteStartArray();
                    foreach (var child in value.EnumerateArray()) Write(child, writer);
                    writer.WriteEndArray();
                    break;
                case JsonValueKind.Number: writer.WriteNumberValue(value.GetDouble() is 0 ? 0 : value.GetDouble()); break;
                case JsonValueKind.String: writer.WriteStringValue(value.GetString()); break;
                default: value.WriteTo(writer); break;
            }
        }
    }

    private static string HostedBinding(McpToolDefinition tool) => Convert.ToHexString(
        SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes<HostedToolDefinition>(tool, HostedToolsJson.Options)));

    private static McpToolDefinition Descriptor(List<ResponseToolDefinition>? tools, string server, string name) => McpResultReviews.FromNative(
        (tools ?? []).Where(tool => tool.Type == "mcp").Select(tool => tool.Extra).OfType<Dictionary<string, JsonElement>>(), server, name);
}
