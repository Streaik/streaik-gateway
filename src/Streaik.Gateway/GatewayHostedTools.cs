using System.Net;
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
        services.TryAddSingleton(new McpResultReviews());
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

    internal sealed record Round(ResponseResult Result, IReadOnlyList<ResponseStreamPart> Events, McpResultReviews.Result? Review = null);

    internal static async Task<Round> RunAsync(GatewayCall call, ResponseRequest request, HostedToolDefinition[] definitions,
        ChatRequest? chat = null, Func<HostedToolActivity, Task>? observe = null)
    {
        var services = call.Context.RequestServices;
        var reviews = services.GetService<McpResultReviews>() ?? throw new ContractException("Streaik hosted tools are not enabled on this gateway.", 422);
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
            List<ResponseInputItem> history = [.. initial];
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
                if (history.RemoveAll(item => item is ResponseMcpApprovalResponseItem) > 0)
                    history.AddRange(result.Result.OutputItems.OfType<ResponseMcpCallItem>());
                var outputItems = result.Result.OutputItems.ToArray();
                var calls = outputItems.OfType<ResponseFunctionCallItem>().ToArray();
                if (!calls.Any(f => f.Namespace is null && IsReserved(f.Name))) return result;
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
                if (chat is not null && outcome is McpCallOutcome.ApprovalRequired { Approval: var approval }
                    && definitions.OfType<McpToolDefinition>().SingleOrDefault(tool => tool.ServerLabel == approval.ServerLabel)
                        is { RequireApproval: McpApprovalPolicy.Always } descriptor)
                {
                    var scope = UpstreamMcpReview.Binding(call, chat);
                    // On API Chat 'always' explicitly asks to execute for review, not yet to disclose to inference.
                    McpResultReviews.Result review;
                    try
                    {
                        review = await reviews.ExecuteHostedAsync(scope, UpstreamMcpReview.HostedBinding(descriptor), approval, function.Name,
                            () => turn.ResumeApprovedAsync(approval, call.Ct));
                    }
                    catch (InvalidOperationException) { throw new ContractException("The hosted MCP call could not be completed for review or was already handled.", 409); }
                    result.Result.Output = outputItems.Where(item => !ReferenceEquals(item, function)).ToArray();
                    return new(result.Result, [new ResponseCompleted { Response = result.Result, SequenceNumber = 0 }], review);
                }
                throw new ContractException("This hosted MCP tool requires approval. No upstream approval was requested; the tool was not dispatched.", 403);
            }
            throw new ContractException("The hosted tool round limit was reached.", 502);
        }
        catch (UnauthorizedAccessException) { throw new ContractException("The host did not authorize this hosted tool operation.", 403); }
        catch (InvalidDataException) { throw new ContractException("Invalid hosted tool data.", 422); }
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
