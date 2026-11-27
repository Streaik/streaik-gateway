using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Streaik.Responses;
using Streaik.Responses.Streaming;
using Streaik.Tools;

namespace Streaik.Gateway;

internal sealed class NativeMcpApprovals(GatewayCall call, ResponseRequest request, HostedToolDefinition[] hosted)
{
    internal const string ReplayKey = "streaik_mcp_approvals";
    private const string HistoryKey = "streaik_mcp_history";
    private const string HostedCallPrefix = "mcp_streaik_";
    private readonly string binding = Hash(new { call.NativeModel, request.Tools,
        hosted = hosted.Select(tool => JsonSerializer.SerializeToElement(tool, HostedToolsJson.Options)).ToArray() });

    internal sealed record Pending(string Id, string Server, string Name, string ArgumentsHash, string Binding,
        string? Function = null, string? CallId = null, string? EndpointBinding = null);
    internal sealed record Decision(Pending Proof, ResponseMcpApprovalRequestItem Item, bool Approve);
    private sealed record Completed(string Model, string ItemHash, string Function, string CallId);

    public List<Decision> Validate(ResponseRequest request)
    {
        JsonElement proofs = default;
        request.AdditionalProperties?.Remove(ReplayKey, out proofs);
        var items = request.Input?.Items ?? [];
        var originals = items.OfType<ResponseMcpApprovalRequestItem>().ToArray();
        var replies = items.OfType<ResponseMcpApprovalResponseItem>().ToArray();
        if (originals.Length == 0 && replies.Length == 0 && proofs.ValueKind == JsonValueKind.Undefined) return [];
        if (proofs.ValueKind != JsonValueKind.Object || originals.Length != replies.Length || replies.Length is 0 or > 32)
            throw Invalid();

        var references = new GatewayReferences(call, "mcp-execution");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        List<Decision> local = [];
        foreach (var reply in replies)
        {
            if (reply.ApprovalRequestId is not { Length: > 0 and <= 256 } id || reply.Approve is not { } approved
                || !seen.Add(id) || !proofs.TryGetProperty(id, out var token) || token.ValueKind != JsonValueKind.String
                || originals.Where(original => original.Id == id).ToArray() is not [var original]) throw Invalid();
            Pending pending;
            try { pending = references.Read<Pending>(token.GetString()!); }
            catch (ContractException) { throw Invalid(); }
            if (pending.Binding != binding || pending.Id != original.Id || pending.Server != original.ServerLabel
                || pending.Name != original.Name || pending.ArgumentsHash != Hash(original.Arguments)) throw Invalid();
            if (pending.Function is not null) local.Add(new(pending, original, approved));
        }

        var localIds = local.Select(decision => decision.Item.Id).ToHashSet(StringComparer.Ordinal);
        request.Input = new ResponseInput(items.Where(item => item switch
        {
            ResponseMcpApprovalRequestItem prior => !localIds.Contains(prior.Id),
            ResponseMcpApprovalResponseItem reply => !localIds.Contains(reply.ApprovalRequestId),
            _ => true
        }).ToArray());
        return local;
    }

    public ResponseResult Observe(ResponseResult result, McpExecutionApproval? local = null, string? function = null)
    {
        if (result.Status is not "completed") return result;
        Dictionary<string, string> proofs = new(StringComparer.Ordinal);
        foreach (var item in result.OutputItems.OfType<ResponseMcpApprovalRequestItem>())
        {
            if (item.Id is not { Length: > 0 and <= 256 } id || item.ServerLabel is not { Length: > 0 and <= 256 } server
                || item.Name is not { Length: > 0 and <= 256 } name || item.Arguments is { Length: > 65536 } || proofs.ContainsKey(id))
                throw new ContractException("The provider returned invalid MCP approval data.", 502);
            var isLocal = local is not null && id == $"mcpr_{local.ApprovalId}";
            proofs[id] = new GatewayReferences(call, "mcp-execution").Protect(new Pending(id, server, name, Hash(item.Arguments), binding,
                isLocal ? function : null, isLocal ? local!.CallId : null, isLocal ? local!.EndpointBinding : null));
        }
        if (proofs.Count > 0) (result.Metadata ??= [])[ReplayKey] = proofs;
        return result;
    }

    public ResponseMcpCallItem Complete(ResponseMcpCallItem item, Decision decision, Dictionary<string, string> receipts)
    {
        item.Id = $"{HostedCallPrefix}{Guid.NewGuid():N}";
        receipts[item.Id] = new GatewayReferences(call, "mcp-history").Protect(
            new Completed(call.NativeModel, ItemHash(item), decision.Proof.Function!, decision.Proof.CallId!), historical: true);
        return item;
    }

    public static void AttachHistory(ResponseResult result, Dictionary<string, string> receipts)
    {
        if (receipts.Count != 0) (result.Metadata ??= [])[HistoryKey] = receipts;
    }

    public ResponseInput RestoreHistory(ResponseRequest request, List<ResponseInputItem> input)
    {
        JsonElement receipts = default;
        request.AdditionalProperties?.Remove(HistoryKey, out receipts);
        List<ResponseInputItem> restored = [];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in input)
        {
            if (item is not ResponseMcpCallItem { Id: { } id } mcp || !id.StartsWith(HostedCallPrefix, StringComparison.Ordinal))
            {
                restored.Add(item);
                continue;
            }
            if (!seen.Add(id)) continue;
            if (receipts.ValueKind != JsonValueKind.Object || !receipts.TryGetProperty(id, out var token) || token.ValueKind != JsonValueKind.String)
                throw Invalid();
            Completed receipt;
            try { receipt = new GatewayReferences(call, "mcp-history").Read<Completed>(token.GetString()!, historical: true); }
            catch (ContractException) { throw Invalid(); }
            if (receipt.Model != call.NativeModel || receipt.ItemHash != ItemHash(mcp)) throw Invalid();
            restored.Add(new ResponseFunctionCallItem { CallId = receipt.CallId, Name = receipt.Function, Arguments = mcp.Arguments! });
            restored.Add(new ResponseFunctionCallOutputItem { CallId = receipt.CallId, Output = Text(mcp.Output) ?? Text(mcp.Error) ?? "" });
        }
        return new ResponseInput(restored);
    }

    private static string? Text(JsonElement? value) => value is { ValueKind: JsonValueKind.String } text ? text.GetString() : value?.GetRawText();

    private static string ItemHash(ResponseMcpCallItem item) => Hash(new { item.Id, item.ServerLabel, item.Name, item.Arguments,
        item.Status, Output = Text(item.Output), Error = Text(item.Error) });

    public async IAsyncEnumerable<ResponseStreamPart> Observe(IAsyncEnumerable<ResponseStreamPart> stream,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var part in stream.WithCancellation(ct))
        {
            if (part is ResponseCompleted completed) Observe(completed.Response);
            yield return part;
        }
    }

    internal static string Hash<T>(T value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, ResponseJson.Default)));

    private static ContractException Invalid() => new("Missing, invalid, or expired MCP approval continuation.", 403);
}
