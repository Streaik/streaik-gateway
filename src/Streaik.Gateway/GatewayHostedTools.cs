using System.Net;
using Microsoft.AspNetCore.DataProtection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Streaik.Responses;
using Streaik.Responses.Streaming;
using Streaik.Tools;
using Streaik.Vercel.Models;

namespace Streaik.Gateway;

public static class GatewayHostedToolHosting
{
    /// <summary>Enables named gateway tools. Register a scoped HostedToolAccess for MCP and skill reference resolution.</summary>
    public static IServiceCollection AddStreaikHostedTools(this IServiceCollection services)
    {
        services.AddDataProtection().SetApplicationName("Streaik.Gateway");
        services.TryAddSingleton(new HostedToolsEnabled());
        return services;
    }

    /// <summary>Enables credential-free public HTTPS MCP, and loopback MCP for same-machine development requests.</summary>
    public static IServiceCollection AddStreaikPublicHostedTools(this IServiceCollection services)
    {
        services.AddStreaikHostedTools().AddHttpContextAccessor();
        services.TryAddScoped<HostedToolAccess>(provider => PublicMcpAccess.Create(
            provider.GetRequiredService<IHostEnvironment>().IsDevelopment()
            && provider.GetRequiredService<IHttpContextAccessor>().HttpContext is { } context && IsLoopbackRequest(context)));
        return services;
    }

    private static bool IsLoopbackRequest(HttpContext context)
    {
        var address = context.Connection.RemoteIpAddress;
        if (address?.IsIPv4MappedToIPv6 == true) address = address.MapToIPv4();
        if (address is null || !IPAddress.IsLoopback(address)) return false;
        // A cross-origin web page can send a request from the local browser, but does not inherit local MCP access.
        var origin = context.Request.Headers.Origin;
        return origin.Count == 0 || origin.Count == 1 && Uri.TryCreate(origin[0], UriKind.Absolute, out var url) && url.IsLoopback;
    }
}

internal sealed class HostedToolsEnabled;

internal static class GatewayHostedTools
{
    private const string Prefix = "streaik_";
    private const int MaxRounds = 12;

    internal static HostedToolDefinition[] Extract(ResponseRequest request)
    {
        List<HostedToolDefinition> hosted = [];
        request.Tools?.RemoveAll(tool =>
        {
            if (!HostedToolsJson.IsHostedKind(tool.Type) || tool.Extra?.GetValueOrDefault("name") is not { ValueKind: JsonValueKind.String } name
                || !name.ValueEquals(tool.Type)) return false;
            hosted.Add(ParseNamed(JsonSerializer.SerializeToElement(tool, ResponseJson.Default), tool.Type));
            return true;
        });
        return hosted.ToArray();
    }

    internal static HostedToolDefinition[] Extract(ChatRequest request)
    {
        List<HostedToolDefinition> hosted = [];
        request.Tools?.RemoveAll(tool =>
        {
            var schema = tool.InputSchema;
            var keywords = schema?.AdditionalProperties;
            if (keywords?.ContainsKey("x-streaik") == true)
                throw new ContractException("Use the hosted tool name as the options key inside inputSchema.");
            var markers = keywords?.Keys.Where(HostedToolsJson.IsHostedKind).ToArray() ?? [];
            if (markers.Length == 0)
            {
                if (schema is null && HostedToolsJson.IsHostedKind(tool.Name))
                    throw new ContractException("Named hosted tools must include their options inside inputSchema.");
                return false;
            }
            if (markers.Length != 1 || markers[0] != tool.Name || schema?.Type != "object")
                throw new ContractException("The hosted options key must match the tool name in an object inputSchema.");
            if (tool.AllowedCallers is not null || tool.DeferLoading is not null || tool.OutputSchema is not null)
                throw new ContractException("Put hosted caller and deferred-loading settings in the named inputSchema options object.");
            try { hosted.Add(HostedToolsJson.ParseNamedOptions(tool.Name, keywords![tool.Name])); }
            catch (JsonException) { throw new ContractException($"Invalid named hosted '{tool.Name}' tool."); }
            return true;
        });
        return hosted.ToArray();
    }

    private static HostedToolDefinition ParseNamed(JsonElement value, string name)
    {
        try { return HostedToolsJson.ParseNamed(value, name); }
        catch (JsonException) { throw new ContractException($"Invalid named hosted '{name}' tool."); }
    }

    internal sealed record Round(ResponseResult Result, IReadOnlyList<ResponseStreamPart> Events, UpstreamMcpReview.Result? Review = null);

    internal static async Task<Round> RunAsync(GatewayCall call, ResponseRequest request, HostedToolDefinition[] definitions,
        ChatRequest? chat = null, Func<HostedToolActivity, Task>? observe = null, NativeMcpApprovals? nativeApprovals = null,
        IReadOnlyList<NativeMcpApprovals.Decision>? decisions = null)
    {
        var services = call.Context.RequestServices;
        if (services.GetService<HostedToolsEnabled>() is null) throw new ContractException("Streaik hosted tools are not enabled on this gateway.", 422);
        var access = services.GetService<HostedToolAccess>() ?? new HostedToolAccess();
        if (access.ResolveSkill is null)
            access = access with { ResolveSkill = (reference, ct) => StreaikGateway.DownloadHostedSkillAsync(call.Context, reference, ct) };
        var initial = InputItems(request.Input);
        if (chat is null && initial.OfType<ResponseFunctionCallItem>().Any(f => f.Namespace is null && IsReserved(f.Name)))
            throw new ContractException("Hosted function history must be generated by the gateway.");
        var providerTools = (request.Tools ?? []).ToArray();
        if (providerTools.Any(tool => tool.Type == "function" && tool.Extra?.GetValueOrDefault("name") is { ValueKind: JsonValueKind.String } name && IsReserved(name.GetString())))
            throw new ContractException("Client function names cannot use the Streaik hosted function prefix.");
        try
        {
            await using var turn = await HostedToolTurn.CreateAsync(definitions, ToolCaller.Direct, access, call.Ct, functionPrefix: Prefix);
            List<ResponseInputItem> history = nativeApprovals?.RestoreHistory(request, initial).Items?.ToList() ?? initial;
            List<ResponseMcpCallItem> completed = [];
            Dictionary<string, string> receipts = new(StringComparer.Ordinal);
            foreach (var decision in decisions ?? [])
            {
                var proof = decision.Proof;
                var item = decision.Item;
                var arguments = item.Arguments!;
                var denied = "The user declined to run the MCP tool.";
                JsonElement? output = null;
                if (decision.Approve)
                {
                    using var input = JsonDocument.Parse(arguments);
                    // The proof authenticates the exact call. The new turn still checks current access, including deferred tools.
                    var executed = await turn.ResumeApprovedAsync(new(item.Id!, proof.CallId!, proof.Server, proof.EndpointBinding!,
                        proof.Name, ToolCaller.Direct, input.RootElement), call.Ct);
                    if (executed is not McpCallOutcome.Completed execution) throw new ContractException("The approved MCP call did not complete.", 502);
                    output = execution.Output;
                }
                history.Add(new ResponseFunctionCallItem { CallId = proof.CallId!, Name = proof.Function!, Arguments = arguments });
                history.Add(Output(proof.CallId!, output is { } value ? value.GetRawText() : denied));
                completed.Add(nativeApprovals!.Complete(new ResponseMcpCallItem { ServerLabel = proof.Server, Name = proof.Name,
                    ApprovalRequestId = item.Id, Arguments = arguments, Status = decision.Approve ? "completed" : "failed",
                    Output = output is { } resultValue ? JsonSerializer.SerializeToElement(resultValue.GetRawText()) : null,
                    Error = decision.Approve ? null : JsonSerializer.SerializeToElement(denied) }, decision, receipts));
            }
            if (turn.Instructions is { } instructions)
                request.Instructions = string.IsNullOrWhiteSpace(request.Instructions) ? instructions : $"{request.Instructions}\n\n{instructions}";
            // Only the host dispatches its functions. Keep client and hosted batches separate.
            request.ParallelToolCalls = false;
            for (var round = 0; round <= MaxRounds; round++)
            {
                request.Tools = [.. providerTools, .. turn.Functions().Select(f => ProtocolAdapters.ResponseFunction(f.Name, f.Description, f.InputSchema))];
                request.Input = new ResponseInput(history);
                var result = await ReadRoundAsync(call, request);
                // A native approval response is a single upstream decision, not part of later internal tool rounds.
                if (history.RemoveAll(item => item is ResponseMcpApprovalResponseItem or ResponseMcpApprovalRequestItem) > 0)
                    history.AddRange(result.Result.OutputItems.OfType<ResponseMcpCallItem>());
                var outputItems = result.Result.OutputItems.ToArray();
                var calls = outputItems.OfType<ResponseFunctionCallItem>().ToArray();
                if (!calls.Any(f => f.Namespace is null && IsReserved(f.Name)))
                {
                    nativeApprovals?.Observe(result.Result);
                    NativeMcpApprovals.AttachHistory(result.Result, receipts);
                    return WithMcpItems(result, completed);
                }
                if (round == MaxRounds || result.Result.Status != "completed" || result.Result.Error is not null || calls.Length != 1
                    || calls[0].Namespace is not null || !turn.Owns(calls[0].Name) || string.IsNullOrEmpty(calls[0].CallId) || !Permits(request.ToolChoice, calls[0].Name)
                    || outputItems.OfType<ResponseMcpApprovalRequestItem>().Any())
                    throw new ContractException("The provider returned an unexpected hosted tool call.", 502);
                var function = calls[0];
                using var input = JsonDocument.Parse(function.Arguments);
                var outcome = observe is null
                    ? await turn.InvokeAsync(function.Name, function.CallId, input.RootElement, call.Ct)
                    : await turn.InvokeAsync(function.Name, function.CallId, input.RootElement, observe, call.Ct);
                history.AddRange(outputItems.OfType<ResponseInputMessage>());
                history.Add(new ResponseFunctionCallItem { CallId = function.CallId, Name = function.Name, Arguments = function.Arguments });
                if (outcome is McpCallOutcome.Completed done)
                {
                    history.Add(Output(function.CallId, done.Output.GetRawText()));
                    continue;
                }
                if (outcome is not McpCallOutcome.ApprovalRequired { Approval: var approval })
                    throw new ContractException("Unexpected hosted MCP approval data.", 502);
                var descriptor = definitions.OfType<McpToolDefinition>().Single(tool => tool.ServerLabel == approval.ServerLabel);
                if (chat is not null)
                {
                    if (descriptor.RequireApproval is McpApprovalPolicy.Never)
                        throw new ContractException("This hosted MCP tool requires review. Select an approval policy to review its result.", 403);
                    var review = await UpstreamMcpReview.ExecuteHostedAsync(call, chat, descriptor, approval, function.Name,
                        () => turn.ResumeApprovedAsync(approval, call.Ct));
                    result.Result.Output = outputItems.Where(item => !ReferenceEquals(item, function)).ToArray();
                    return new(result.Result, [new ResponseCompleted { Response = result.Result, SequenceNumber = 0 }], review);
                }
                if (nativeApprovals is null) throw new ContractException("MCP execution approval is unavailable.", 422);
                var pending = new ResponseMcpApprovalRequestItem { Id = $"mcpr_{approval.ApprovalId}", ServerLabel = approval.ServerLabel,
                    Name = approval.ToolName, Arguments = function.Arguments };
                var index = Array.IndexOf(outputItems, function);
                result.Result.Output = outputItems.Where(item => !ReferenceEquals(item, function)).ToArray();
                var safeEvents = result.Events.Where(part => part is not ResponseStreamIndexedPart { OutputIndex: var itemIndex } || itemIndex != index).ToArray();
                var paused = WithMcpItems(result with { Events = safeEvents }, [.. completed, pending]);
                nativeApprovals.Observe(paused.Result, approval, function.Name);
                NativeMcpApprovals.AttachHistory(paused.Result, receipts);
                return paused;
            }
            throw new ContractException("The hosted tool round limit was reached.", 502);
        }
        catch (UnauthorizedAccessException) { throw new ContractException("The host did not authorize this hosted tool operation.", 403); }
        catch (InvalidDataException) { throw new ContractException("Invalid hosted tool data.", 422); }
    }

    private static Round WithMcpItems(Round round, IReadOnlyList<ResponseInputItem> added)
    {
        if (added.Count == 0) return round;
        var index = round.Result.Output.Count();
        round.Result.Output = [.. round.Result.Output, .. added];
        if (round.Events.Count == 0) return round;
        var events = round.Events.ToList();
        var terminal = events[^1];
        events.RemoveAt(events.Count - 1);
        var sequence = terminal is ResponseCompleted done ? done.SequenceNumber : ((ResponseIncompleteEvent)terminal).SequenceNumber;
        foreach (var item in added)
        {
            var streamItem = JsonSerializer.SerializeToElement(item, ResponseJson.Default).Deserialize<ResponseStreamItem>(ResponseJson.Default)!;
            events.Add(new ResponseOutputItemAdded { SequenceNumber = sequence++, OutputIndex = index, Item = streamItem });
            events.Add(new ResponseOutputItemDone { SequenceNumber = sequence++, OutputIndex = index++, Item = streamItem });
        }
        events.Add(terminal is ResponseIncompleteEvent
            ? new ResponseIncompleteEvent { SequenceNumber = sequence, Response = round.Result, AdditionalProperties = terminal.AdditionalProperties }
            : new ResponseCompleted { SequenceNumber = sequence, Response = round.Result, AdditionalProperties = terminal.AdditionalProperties });
        return round with { Events = events };
    }

    internal static async Task<Round> ReadRoundAsync(GatewayCall call, ResponseRequest request)
    {
        if (request.Stream != true) return new(await ProtocolAdapters.HostedResponseRoundAsync(call, request), []);
        List<ResponseStreamPart> events = [];
        await foreach (var part in ProtocolAdapters.HostedResponseStreamAsync(call, request).WithCancellation(call.Ct))
        {
            if (events.Count == 16384) throw new ContractException("The hosted response exceeded the stream event limit.", 502);
            events.Add(part);
            if (part is ResponseCompleted completed) return new(completed.Response, events);
            if (part is ResponseIncompleteEvent incomplete) return new(incomplete.Response, events);
            if (part is ResponseFailed or ResponseError) throw new ContractException("The upstream hosted response failed.", 502);
        }
        throw new ContractException("The upstream hosted response did not complete.", 502);
    }

    internal static async IAsyncEnumerable<ResponseStreamPart> Events(IEnumerable<ResponseStreamPart> parts, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        foreach (var part in parts) { ct.ThrowIfCancellationRequested(); yield return part; }
    }

    internal static bool IsReserved(string? name) => name?.StartsWith(Prefix, StringComparison.Ordinal) == true;
    private static ResponseFunctionCallOutputItem Output(string callId, string output) => new() { CallId = callId, Output = output };
    private static bool Permits(object? choice, string name) => choice switch
    {
        null => true,
        string s => s is "auto" or "required" || s == name,
        JsonElement { ValueKind: JsonValueKind.String } s => s.ValueEquals("auto") || s.ValueEquals("required") || s.ValueEquals(name),
        JsonElement { ValueKind: JsonValueKind.Object } obj => obj.TryGetProperty("name", out var selected) && selected.ValueKind == JsonValueKind.String && selected.ValueEquals(name),
        JsonElement => false,
        _ => Permits(JsonSerializer.SerializeToElement(choice), name)
    };

    internal static List<ResponseInputItem> InputItems(ResponseInput? input) => input?.Items?.ToList()
        ?? (input?.Text is { } text ? [new ResponseInputMessage { Role = ResponseRole.User, Content = new ResponseMessageContent([new InputTextPart(text)]) }] : []);
}
