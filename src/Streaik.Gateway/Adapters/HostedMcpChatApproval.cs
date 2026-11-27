using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Streaik.Responses;
using Streaik.Tools;
using Streaik.Vercel.Models;
using Streaik.Vercel.Security;

namespace Streaik.Gateway;

internal sealed class HostedMcpChatApproval(GatewayCall call, ChatRequest chat, HostedToolDefinition[]? hosted,
    McpToolDefinition[]? providerMcp, JsonElement? nativeTools, ToolApprovalSigner? signer, bool enabled)
{
    internal sealed record Call(string Kind, string Server, string Name, string CallId, string? Arguments = null);
    internal sealed record Receipt(Call Call, bool Approved);
    internal sealed record Result(ResponseInputItem[] Items, bool Denied);
    internal List<UIMessagePart> CompletedParts { get; } = [];
    private readonly HostedToolContinuation wire = new(signer);
    private static readonly JsonElement Denied = JsonSerializer.SerializeToElement("The user declined to run the MCP tool.");
    internal static bool IsApproval(ToolInvocationPart tool) => HostedToolContinuation.IsApproval(tool);
    internal static bool IsContinuation(ToolInvocationPart tool) => IsApproval(tool) || HostedToolContinuation.IsResult(tool);

    private string Scope()
    {
        if (chat.Id is not { Length: > 0 and <= 256 }) throw new ContractException("MCP execution approval requires a chat ID.");
        return signer is null ? "" : NativeMcpApprovals.Hash(new { Route = "gateway-api-chat", Caller = GatewayReferences.CallerBinding(call),
            chat.Id, call.ProviderId, call.NativeModel, nativeTools,
            hosted = (hosted ?? []).Select(tool => JsonSerializer.SerializeToElement(tool, HostedToolsJson.Options)).ToArray(),
            provider = (providerMcp ?? []).Select(tool => JsonSerializer.SerializeToElement<HostedToolDefinition>(tool, HostedToolsJson.Options)).ToArray() });
    }

    internal IReadOnlyList<UIMessagePart> Issue(McpExecutionApproval approval) =>
        Issue(new("local", approval.ServerLabel, approval.ToolName, approval.CallId), approval.Input);

    internal IReadOnlyList<UIMessagePart> Issue(ResponseMcpApprovalRequestItem item)
    {
        var kind = providerMcp?.Any(server => server.ServerLabel == item.ServerLabel) == true ? "provider" : "upstream";
        if (item.Arguments is { Length: > 65536 }) throw Invalid();
        try
        {
            using var input = JsonDocument.Parse(item.Arguments ?? "{}", new JsonDocumentOptions { AllowDuplicateProperties = false });
            var pending = new Call(kind, item.ServerLabel!, item.Name!, item.Id!, kind == "provider" ? item.Arguments ?? "{}" : null);
            _ = Descriptor(pending);
            return Issue(pending, input.RootElement.Clone());
        }
        catch (JsonException) { throw Invalid(); }
    }

    private IReadOnlyList<UIMessagePart> Issue(Call pending, JsonElement input)
    {
        try { return wire.Request(Scope(), pending.Name, pending.Server, input, pending); }
        catch (InvalidDataException) { throw Invalid(); }
    }

    internal async Task<Dictionary<string, Result>> ResolveAsync()
    {
        Dictionary<string, Result> results = new(StringComparer.Ordinal);
        List<(string Id, Call Call, JsonElement Input, bool Approved, McpToolDefinition Descriptor)> pending = [];
        HashSet<string> historical = new(StringComparer.Ordinal);
        HashSet<string> seen = new(StringComparer.Ordinal);
        try
        {
            foreach (var message in chat.Messages)
            foreach (var part in message.Parts.OfType<ToolInvocationPart>().Where(IsContinuation))
            {
                if (!seen.Add(part.ToolCallId)) throw Invalid();
                if (HostedToolContinuation.IsResult(part))
                {
                    var (receipt, input, output) = wire.ReadResult<Receipt>(message.Role, part, Scope());
                    _ = Descriptor(receipt.Call);
                    if (receipt.Call.Kind == "provider") throw Invalid();
                    historical.Add(part.ToolCallId[4..]);
                    results.Add(part.ToolCallId, Project(receipt.Call, input, receipt.Approved, receipt.Approved ? output : Denied));
                }
                else if (ReferenceEquals(message, chat.Messages.LastOrDefault()))
                {
                    var (proposal, input, approved) = wire.ReadDecision<Call>(message.Role, part, Scope());
                    pending.Add((part.ToolCallId, proposal, input, approved, Descriptor(proposal)));
                }
            }
            foreach (var item in pending.Where(item => !historical.Contains(item.Id)))
                results.Add(item.Id, await ExecuteAsync(item.Id, item.Call, item.Input, item.Approved, item.Descriptor));
        }
        catch (Exception error) when (error is InvalidDataException or JsonException or UnauthorizedAccessException)
        { throw Invalid(); }
        return results;
    }

    private McpToolDefinition Descriptor(Call proposal)
    {
        if (proposal is not { Server.Length: > 0 and <= 256, Name.Length: > 0 and <= 256, CallId.Length: > 0 and <= 256 }) throw Invalid();
        var descriptor = proposal.Kind switch
        {
            "local" when enabled => (hosted ?? []).OfType<McpToolDefinition>().SingleOrDefault(server => server.ServerLabel == proposal.Server),
            "provider" => (providerMcp ?? []).SingleOrDefault(server => server.ServerLabel == proposal.Server),
            "upstream" when enabled && nativeTools is { ValueKind: JsonValueKind.Array } tools => McpResultReviews.FromNative(
                (tools.Deserialize<ResponseToolDefinition[]>(ResponseJson.Default) ?? []).Where(tool => tool.Type == "mcp")
                    .Select(tool => tool.Extra).OfType<Dictionary<string, JsonElement>>(), proposal.Server, proposal.Name),
            _ => null
        };
        if (descriptor is null || !descriptor.Allows(ToolCaller.Direct) || descriptor.RequireApproval is McpApprovalPolicy.Never) throw Invalid();
        return descriptor;
    }

    private async Task<Result> ExecuteAsync(string id, Call proposal, JsonElement input, bool approved, McpToolDefinition descriptor)
    {
        if (proposal.Kind == "provider")
        {
            var arguments = proposal.Arguments ?? "{}";
            if (arguments.Length > 65536 || !JsonElement.DeepEquals(input, JsonSerializer.Deserialize<JsonElement>(arguments))) throw Invalid();
            return new([new ResponseMcpApprovalRequestItem { Id = proposal.CallId, ServerLabel = proposal.Server, Name = proposal.Name, Arguments = arguments },
                        new ResponseMcpApprovalResponseItem { ApprovalRequestId = proposal.CallId, Approve = approved }], !approved);
        }

        var output = Denied;
        if (approved)
        {
            var access = call.Context.RequestServices.GetService<HostedToolAccess>() ?? throw new UnauthorizedAccessException();
            if (access.AuthorizeMcp is not { } authorize) throw new UnauthorizedAccessException();
            await using var session = new McpToolSession([descriptor], ToolCaller.Direct, authorize, access.CreateMcpHandler);
            var binding = McpToolSession.EndpointBinding(descriptor.ServerUrl);
            if (await session.ResumeApprovedAsync(new(id, proposal.CallId, proposal.Server, binding, proposal.Name, ToolCaller.Direct, input), call.Ct)
                is not McpCallOutcome.Completed done) throw new ContractException("The approved MCP call did not complete.", 502);
            output = done.Output;
            if (output.GetRawText().Length > 512 * 1024) throw new ContractException("The MCP result is too large.", 422);
        }
        CompletedParts.AddRange(wire.Complete(Scope(), id, proposal.Name, input, new Receipt(proposal, approved), output));
        return Project(proposal, input, approved, output);
    }

    private static Result Project(Call proposal, JsonElement input, bool approved, JsonElement output)
    {
        var arguments = input.GetRawText();
        var text = output.ValueKind == JsonValueKind.String ? output.GetString()! : output.GetRawText();
        if (proposal.Kind == "local")
            return new([new ResponseFunctionCallItem { Name = HostedToolTurn.McpFunctionName("streaik_", proposal.Server, proposal.Name),
                            CallId = proposal.CallId, Arguments = arguments },
                        new ResponseFunctionCallOutputItem { CallId = proposal.CallId, Output = text }], !approved);
        return new([new ResponseMcpCallItem { Id = $"mcp_{proposal.CallId}", ServerLabel = proposal.Server, Name = proposal.Name,
            Arguments = arguments, Status = approved ? "completed" : "failed", Output = approved ? JsonSerializer.SerializeToElement(text) : null,
            Error = approved ? null : output }], !approved);
    }

    private static ContractException Invalid() => new("Unknown or invalid hosted MCP execution approval.", 403);
}
