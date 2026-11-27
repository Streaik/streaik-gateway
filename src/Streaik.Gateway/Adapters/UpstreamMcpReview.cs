using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Streaik.Providers;
using Streaik.Responses;
using Streaik.Tools;
using Streaik.Vercel.Models;

namespace Streaik.Gateway;

internal static class UpstreamMcpReview
{
    internal static bool IsReview(ToolInvocationPart tool) => tool.ToolName == McpResultReviews.ToolName
        || tool.Type == $"tool-{McpResultReviews.ToolName}";

    internal static async Task<IReadOnlyList<UIMessagePart>> CreateAsync(McpResultReviews reviews, GatewayCall call,
        ChatRequest chat, List<ResponseToolDefinition>? tools, ResponseMcpApprovalRequestItem approval)
    {
        var scope = Binding(call, chat);
        if (approval.Id is not { Length: > 0 } id || approval.ServerLabel is not { Length: > 0 } server
            || approval.Name is not { Length: > 0 } name || approval.Arguments is not { Length: > 0 and <= 65536 } arguments)
            throw new ContractException("The provider returned an invalid MCP approval request.", 502);
        var access = call.Context.RequestServices.GetService<HostedToolAccess>()
            ?? throw new ContractException("Local MCP execution is not authorized on this gateway.", 403);
        try
        {
            var descriptor = Descriptor(tools, server, name);
            using var document = JsonDocument.Parse(arguments, new JsonDocumentOptions { AllowDuplicateProperties = false });
            var dispatchScope = JsonSerializer.Serialize(new[] { Caller(call), call.ProviderId });
            var result = await reviews.ExecuteAsync(scope, id, descriptor, name, document.RootElement, access, call.Ct, dispatchScope);
            return Parts(result);
        }
        catch (UnauthorizedAccessException) { throw new ContractException("The gateway did not authorize this MCP call.", 403); }
        catch (JsonException) { throw new ContractException("Invalid upstream MCP approval data.", 502); }
        catch (InvalidOperationException error) when (error.GetType() == typeof(InvalidOperationException))
        { throw new ContractException("The MCP call could not be completed for review or was already handled.", 409); }
    }

    internal static IReadOnlyList<UIMessagePart> Parts(McpResultReviews.Result result)
    {
        var output = result.Output!.Value;
        return [new ToolCallPart { ToolCallId = result.Id, ToolName = McpResultReviews.ToolName, Title = $"Review MCP result: {result.Name}",
            Input = result.Input, ProviderExecuted = true, Dynamic = true, ToolMetadata = new() { ["mcpReviewOutput"] = output } },
            new ToolApprovalRequestUIPart { ToolCallId = result.Id, ApprovalId = result.Id,
                Reason = "Review the completed MCP result before sending it to the model.",
                ApprovalDescriptor = new { server = result.Server, name = result.Name, input = result.Input, output } }];
    }

    internal static ResponseInputItem[] Resolve(McpResultReviews reviews, GatewayCall call, ChatRequest chat,
        List<ResponseToolDefinition> tools, ToolInvocationPart part, HostedToolDefinition[]? hosted)
    {
        if (part.ProviderExecuted != true || part.Approval?.Approved is not { } approved
            || part.Approval.Id != part.ToolCallId || part.Input is not JsonElement input
            || (approved ? part.State is not ("approval-responded" or "output-available") : part.State is not ("approval-responded" or "output-denied")))
            throw new ContractException("Invalid MCP result review decision.", 403);
        try
        {
            var result = reviews.Resolve(Binding(call, chat), part.ToolCallId, input, approved, approved ? part.Output as JsonElement? : null,
                (server, name, isHosted) => isHosted ? HostedBinding(HostedDescriptor(hosted, server)) : Descriptor(tools, server, name).ServerUrl.AbsoluteUri);
            const string denied = "The user chose not to disclose the tool result.";
            if (result.Hosted is { } function)
                return [new ResponseFunctionCallItem { CallId = function.CallId, Name = function.FunctionName, Arguments = result.Input.GetRawText() },
                    new ResponseFunctionCallOutputItem { CallId = function.CallId, Output = result.Output is { } client ? McpResultReviews.OutputText(client) : denied }];
            return [new ResponseMcpCallItem { Id = $"mcp_{result.Id}", ServerLabel = result.Server, Name = result.Name,
                Arguments = result.Input.GetRawText(), Status = approved ? "completed" : "failed",
                Output = result.Output is { } output ? JsonSerializer.SerializeToElement(McpResultReviews.OutputText(output)) : null,
                Error = approved ? null : JsonSerializer.SerializeToElement(denied) }];
        }
        catch (UnauthorizedAccessException) { throw new ContractException("Unknown or invalid MCP result review.", 403); }
    }

    internal static string HostedBinding(McpToolDefinition tool) => Convert.ToHexString(
        SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes<HostedToolDefinition>(tool, HostedToolsJson.Options)));

    private static McpToolDefinition HostedDescriptor(HostedToolDefinition[]? tools, string server) =>
        (tools ?? []).OfType<McpToolDefinition>().Where(tool => tool.ServerLabel == server).ToArray() is [var match]
            ? match : throw new UnauthorizedAccessException("Unknown hosted MCP server.");

    internal static string Binding(GatewayCall call, ChatRequest request) => Caller(call) is { } user
        && request.Id is { Length: > 0 and <= 256 } id ? JsonSerializer.Serialize(new[] { user, id, call.ProviderId, call.NativeModel })
        : throw new ContractException("MCP result review requires an authenticated caller or provider credential, and a conversation ID.", 403);

    private static readonly byte[] HeaderBindingKey = RandomNumberGenerator.GetBytes(32);

    private static string? Caller(GatewayCall call)
    {
        if (call.Context.User.Identity?.IsAuthenticated == true) return McpResultReviews.Subject(call.Context.User);
        if (call.Context.RequestServices.GetService<IProviderCredentials>() is not IHeaderProviderCredentials headers
            || headers.GetApiKey(call.ProviderId) is not { Length: > 0 } key) return null;
        // In the BYOK sample the same provider credential must be presented on continuation, without retaining the key.
        return "byok:" + Convert.ToHexString(HMACSHA256.HashData(HeaderBindingKey, Encoding.UTF8.GetBytes(key)));
    }

    private static McpToolDefinition Descriptor(List<ResponseToolDefinition>? tools, string server, string name) => McpResultReviews.FromNative(
        (tools ?? []).Where(tool => tool.Type == "mcp").Select(tool => tool.Extra).OfType<Dictionary<string, JsonElement>>(), server, name);
}
