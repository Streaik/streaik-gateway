using System.Text.Json;
using Streaik.Responses;
using Streaik.Tools;
using Streaik.Vercel.Models;

namespace Streaik.Gateway;

// Unlike UpstreamMcpReview, this path never opens an MCP connection. The protected
// approval ID lets the gateway pass the decision upstream without retaining state.
internal static class ProviderMcpApproval
{
    internal const string ToolName = "streaik_mcp_execution_approval";
    private const string Prefix = "mpe_";
    private const string ArgumentsKey = "streaikMcpArguments";
    private sealed record Receipt(string Chat, string Model, string Binding, string Id, string Server, string Name, string ArgumentsHash);

    internal static bool IsApproval(ToolInvocationPart tool) => tool.ToolName == ToolName || tool.Type == $"tool-{ToolName}";

    internal static IReadOnlyList<UIMessagePart>? Create(GatewayCall call, ChatRequest chat, McpToolDefinition[]? servers,
        ResponseMcpApprovalRequestItem item)
    {
        var server = servers?.SingleOrDefault(descriptor => descriptor.ServerLabel == item.ServerLabel);
        if (server is null) return null;
        if (chat.Id is not { Length: > 0 and <= 256 } chatId || item.Id is not { Length: > 0 and <= 256 } id
            || item.Name is not { Length: > 0 and <= 256 } name || item.Arguments is { Length: > 65536 })
            throw new ContractException("Invalid provider MCP approval context.", 422);
        var arguments = item.Arguments ?? "{}";
        JsonElement input;
        try { input = JsonSerializer.Deserialize<JsonElement>(arguments, HostedToolsJson.Options); }
        catch (JsonException) { throw new ContractException("The provider returned invalid MCP approval arguments.", 502); }
        if (input.ValueKind != JsonValueKind.Object) throw new ContractException("The provider returned invalid MCP approval arguments.", 502);
        var proof = Prefix + new GatewayReferences(call, "vercel-mcp-execution").Protect(
            new Receipt(chatId, call.NativeModel, Binding(server), id, server.ServerLabel, name, NativeMcpApprovals.Hash(arguments)));
        return [new ToolCallPart { ToolCallId = id, ToolName = ToolName, Title = $"Run MCP tool: {name}",
                    Input = input, ProviderExecuted = true, Dynamic = true,
                    ToolMetadata = new() { [ArgumentsKey] = JsonSerializer.SerializeToElement(arguments) } },
                new ToolApprovalRequestUIPart { ToolCallId = id, ApprovalId = proof,
                    Reason = "Approve this MCP tool call before the provider runs it.",
                    ApprovalDescriptor = new { server = server.ServerLabel, name, input } }];
    }

    internal static ResponseInputItem[] Resolve(GatewayCall call, ChatRequest chat, McpToolDefinition[]? servers, ToolInvocationPart part)
    {
        if (part.ProviderExecuted != true || part.Approval?.Approved is not { } approved
            || !part.Approval.Id.StartsWith(Prefix, StringComparison.Ordinal) || part.Input is not JsonElement input
            || (approved ? part.State is not ("approval-responded" or "output-available") : part.State is not ("approval-responded" or "output-denied")))
            throw Invalid();
        Receipt receipt;
        try { receipt = new GatewayReferences(call, "vercel-mcp-execution").Read<Receipt>(part.Approval.Id[Prefix.Length..]); }
        catch (ContractException) { throw Invalid(); }
        var server = servers?.SingleOrDefault(descriptor => descriptor.ServerLabel == receipt.Server);
        if (server is null || receipt.Chat != chat.Id || receipt.Model != call.NativeModel || receipt.Id != part.ToolCallId
            || receipt.Binding != Binding(server) || part.ToolMetadata?.GetValueOrDefault(ArgumentsKey) is not { ValueKind: JsonValueKind.String } raw
            || raw.GetString() is not { Length: <= 65536 } arguments || receipt.ArgumentsHash != NativeMcpApprovals.Hash(arguments)
            || !JsonElement.DeepEquals(input, JsonSerializer.Deserialize<JsonElement>(arguments)))
            throw Invalid();
        return [new ResponseMcpApprovalRequestItem { Id = receipt.Id, ServerLabel = receipt.Server, Name = receipt.Name, Arguments = arguments },
                new ResponseMcpApprovalResponseItem { ApprovalRequestId = receipt.Id, Approve = approved }];
    }

    private static string Binding(McpToolDefinition server) => NativeMcpApprovals.Hash(JsonSerializer.SerializeToElement<HostedToolDefinition>(server, HostedToolsJson.Options));
    private static ContractException Invalid() => new("Missing, invalid, or expired provider MCP approval.", 403);
}
