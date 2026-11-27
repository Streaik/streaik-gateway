using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Streaik.Responses;
using Streaik.Responses.Streaming;
using Streaik.Tools;

namespace Streaik.Gateway;

internal sealed class NativeMcpApprovals(GatewayCall call, ResponseRequest request, McpApprovalDecisions decisions)
{
    // Snapshot before the local hosted loop appends its private function declarations.
    private readonly string? scope = McpResultReviews.Subject(call.Context.User) is { } subject
        ? Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { subject, call.ProviderId, call.NativeModel, request.Tools }, ResponseJson.Default)))
        : null;

    public void Validate(ResponseRequest request)
    {
        if (request.Input?.Items is not { } items) return;
        if (items.OfType<ResponseMcpApprovalRequestItem>().Any())
            throw new ContractException("Submit MCP approval decisions, not MCP approval requests, as input.");
        var replies = items.OfType<ResponseMcpApprovalResponseItem>().ToArray();
        if (replies.Length == 0) return;
        if (scope is null || replies.Any(reply => reply.ApprovalRequestId is not { Length: > 0 and <= 256 } || reply.Approve is null)
            || !decisions.Consume(scope, replies.Select(reply => reply.ApprovalRequestId!).ToArray()))
            throw new ContractException("Unknown, unauthorized, or already used MCP approval.", 403);
    }

    public ResponseResult Observe(ResponseResult result)
    {
        if (scope is null || result.Status is not "completed") return result;
        var ids = result.OutputItems.OfType<ResponseMcpApprovalRequestItem>().Select(item => item.Id).ToArray();
        if (ids.Any(id => id is not { Length: > 0 and <= 256 }))
            throw new ContractException("The provider returned an invalid MCP approval ID.", 502);
        try { decisions.Observe(scope, ids.Select(id => id!)); }
        catch (InvalidOperationException) { throw new ContractException("The MCP approval store is full.", 503); }
        return result;
    }

    public async IAsyncEnumerable<ResponseStreamPart> Observe(IAsyncEnumerable<ResponseStreamPart> stream,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var part in stream.WithCancellation(ct))
        {
            if (part is ResponseCompleted completed) Observe(completed.Response);
            yield return part;
        }
    }
}
