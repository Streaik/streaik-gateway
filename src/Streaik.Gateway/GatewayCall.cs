using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Streaik.ChatCompletions.Models;
using Streaik.Messages;
using Streaik.Providers;
using Streaik.Responses;
using Streaik.Responses.Output;
using Streaik.Responses.Streaming;
using Streaik.Vercel.Models;

namespace Streaik.Gateway;

internal sealed class GatewayCall
{
    public GatewayCall(HttpContext context, string? model)
    {
        Context = context;
        var slash = model?.IndexOf('/') ?? -1;
        if (slash <= 0 || string.IsNullOrWhiteSpace(model![(slash + 1)..]))
            throw new ContractException("Use a qualified model ID: provider/model.");
        Model = model!;
        ProviderId = Model[..slash].ToLowerInvariant();
        NativeModel = Model[(slash + 1)..];
        var registration = Get<ProviderRegistration>() ?? throw new ContractException("Unknown provider.", 404);
        Headers = GatewayHeaders.Select(context.Request.Headers, registration);
        context.Items[GatewayHeaderAuth.ActiveProvider] = ProviderId;
    }

    public HttpContext Context { get; }
    public string Model { get; }
    public string ProviderId { get; }
    public string NativeModel { get; }
    public IReadOnlyDictionary<string, string> Headers { get; }
    public CancellationToken Ct => Context.RequestAborted;
    // These providers use Chat Completions for translated turns, even when they have other native endpoints.
    public bool PreferChatBridge => ProviderId is "nebius" or "mixedbread" or "tetrate" or "upstage" or "akashml" or "coralbricks" or "doubleword" or "clarifai" or "tensorblock" or "aichixia" or "edgespeak" or "cognitivessai" or "baseapi";
    public T? Get<T>() where T : class => Context.RequestServices.GetKeyedService<T>(ProviderId);
    public IChatCompletionsProvider? GetChatBridge() => Get<IChatBridgeProvider>()?.ChatBridge ?? Get<IChatCompletionsProvider>();
    public T Require<T>() where T : class => Get<T>() ?? throw new ContractException("Provider does not support this protocol.");
    private GatewayPerformance Measure() => new(Context.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System);

    public async Task WriteAsync(ChatCompletion result)
    {
        var cost = await GatewayPricing.EstimateAsync(this, result.Model, result.Usage);
        if (result.Metadata?.ContainsKey("gateway") != true)
            result.MetadataExtensions = GatewayPricing.Attach(result.MetadataExtensions, cost);
        result.Model = Model;
        await Context.Response.WriteAsJsonAsync(result, Ct);
    }

    public async Task WriteAsync(ResponseResult result, GatewayContainerArtifacts.Turn? artifacts = null)
    {
        GatewayPricing.Attach(result, await GatewayPricing.EstimateAsync(this, result.Model, result.Usage));
        GatewayContainerArtifacts.AddResponseReferences(this, result);
        if (artifacts is not null && result.Status is "completed" or "incomplete")
        {
            var output = result.Output.ToList();
            await foreach (var artifact in artifacts.DownloadAsync(result)) output.Add(artifact.ToResponse());
            result.Output = output;
        }
        result.Model = Model;
        await Context.Response.WriteAsJsonAsync(result, ResponseJson.Default, Ct);
    }

    public async Task WriteAsync(MessagesResponse result)
    {
        result.Metadata = GatewayPricing.Attach(result.Metadata, await GatewayPricing.EstimateAsync(this, result.Model, result.Usage));
        result.Model = Model;
        await Context.Response.WriteAsJsonAsync(result, MessagesJson.Default, Ct);
    }

    public async Task WriteChatStreamAsync(IAsyncEnumerable<ChatCompletionUpdate> source)
    {
        BeginStream();
        var performance = Measure();
        string? upstreamModel = null;
        await foreach (var part in source.WithCancellation(Ct))
        {
            if (part.AdditionalProperties?.ContainsKey("error") == true)
                throw new IOException("Chat Completions returned a stream error.");
            upstreamModel = part.Model ?? upstreamModel;
            if (part.Usage is not null)
                part.AdditionalProperties = GatewayPricing.AttachChat(part.AdditionalProperties,
                    await GatewayPricing.EstimateAsync(this, upstreamModel, part.Usage));
            performance.Observe(part);
            await WritePartAsync(part);
        }
        await WriteFrameAsync(Context.Response, "[DONE]", null, Ct);

        Task WritePartAsync(ChatCompletionUpdate part)
        {
            part.Model = Model;
            return WriteFrameAsync(Context.Response, JsonSerializer.Serialize(part, JsonSerializerOptions.Web), null, Ct);
        }
    }

    public async Task WriteResponsesStreamAsync(IAsyncEnumerable<ResponseStreamPart> source, GatewayContainerArtifacts.Turn? artifacts = null)
    {
        BeginStream();
        var performance = Measure();
        await foreach (var part in source.WithCancellation(Ct))
        {
            artifacts?.Observe(part);
            var outbound = part;
            var response = part switch
            {
                ResponseCreated evt => evt.Response, ResponseInProgress evt => evt.Response, ResponseCompleted evt => evt.Response,
                ResponseFailed evt => evt.Response, ResponseIncompleteEvent evt => evt.Response, ResponseQueuedEvent evt => evt.Response, _ => null
            };
            if (response is not null)
            {
                if (part is ResponseCompleted or ResponseIncompleteEvent)
                {
                    GatewayPricing.Attach(response, await GatewayPricing.EstimateAsync(this, response.Model, response.Usage));
                    GatewayContainerArtifacts.AddResponseReferences(this, response);
                    if (artifacts is not null)
                    {
                        var sequence = part is ResponseCompleted completed ? completed.SequenceNumber : ((ResponseIncompleteEvent)part).SequenceNumber;
                        var next = await WriteArtifactEventsAsync(artifacts, response, sequence);
                        if (next != sequence)
                            outbound = part is ResponseCompleted
                                ? new ResponseCompleted { Response = response, SequenceNumber = next, AdditionalProperties = part.AdditionalProperties }
                                : new ResponseIncompleteEvent { Response = response, SequenceNumber = next, AdditionalProperties = part.AdditionalProperties };
                    }
                }
                response.Model = Model;
            }
            performance.Observe(part, response);
            await WriteResponseEventAsync(outbound);
        }
    }

    private async Task<int> WriteArtifactEventsAsync(GatewayContainerArtifacts.Turn artifacts, ResponseResult response, int sequence)
    {
        var output = response.Output.ToList();
        var initialCount = output.Count;
        await foreach (var artifact in artifacts.DownloadAsync(response))
        {
            var index = output.Count;
            var item = artifact.ToResponse();
            await WriteResponseEventAsync(new ResponseOutputItemAdded
                { SequenceNumber = sequence++, OutputIndex = index, Item = artifact.ToResponse(completed: false).ToStreamItem() });
            await WriteResponseEventAsync(new ResponseMcpCallArgumentsDone
                { SequenceNumber = sequence++, OutputIndex = index, ItemId = item.Id, Arguments = item.Arguments });
            await WriteResponseEventAsync(new ResponseOutputItemDone
                { SequenceNumber = sequence++, OutputIndex = index, Item = item.ToStreamItem() });
            output.Add(item);
        }
        if (output.Count != initialCount) response.Output = output;
        return sequence;
    }

    private Task WriteResponseEventAsync(ResponseStreamPart part) =>
        WriteFrameAsync(Context.Response, JsonSerializer.Serialize(part, ResponseJson.Default), part.Type, Ct);

    public async Task WriteMessagesStreamAsync(IAsyncEnumerable<MessageStreamPart> source)
    {
        BeginStream();
        var performance = Measure();
        var usage = new MessagesUsage();
        string? upstreamModel = null;
        GatewayPrice? reportedCost = null;
        await foreach (var part in source.WithCancellation(Ct))
        {
            if (part.Message is { } message)
            {
                upstreamModel = message.Model;
                MessagesHistory.MergeUsage(usage, message.Usage);
                reportedCost = GatewayPricing.Cost(message.Metadata) ?? GatewayPricing.Reported(ProviderId, message.Usage) ?? reportedCost;
                message.Model = Model;
            }
            MessagesHistory.MergeUsage(usage, part.Usage);
            reportedCost = GatewayPricing.Cost(part.Metadata) ?? GatewayPricing.Reported(ProviderId, part.Usage) ?? reportedCost;
            if (part.Type == "message_stop")
                part.Metadata = GatewayPricing.Attach(part.Metadata, reportedCost ?? await GatewayPricing.EstimateAsync(this, upstreamModel, usage));
            performance.Observe(part);
            // Native Messages clients require these counters. Bridged streams can
            // start before the upstream reports usage, or end without a usage tail.
            // Fill the wire defaults only after measuring real usage for pricing.
            if (part is { Type: "message_start", Message: { } start })
            {
                start.Usage ??= new();
                start.Usage.InputTokens ??= 0;
                start.Usage.OutputTokens ??= 0;
            }
            else if (part.Type == "message_delta")
            {
                part.Usage ??= new();
                part.Usage.OutputTokens ??= 0;
            }
            await WriteFrameAsync(Context.Response, JsonSerializer.Serialize(part, MessagesJson.Default), part.Type, Ct);
        }
    }

    public async Task WriteVercelStreamAsync(IAsyncEnumerable<UIMessagePart> source)
    {
        BeginStream();
        Context.Response.Headers["x-vercel-ai-ui-message-stream"] = "v1";
        var performance = Measure();
        await foreach (var part in source.WithCancellation(Ct))
            await WriteFrameAsync(Context.Response, JsonSerializer.Serialize(performance.Observe(part), JsonSerializerOptions.Web), null, Ct);
        await WriteFrameAsync(Context.Response, "[DONE]", null, Ct);
    }

    private void BeginStream()
    {
        Context.Response.ContentType = "text/event-stream";
        Context.Response.Headers.CacheControl = "no-cache";
    }

    public static Task ErrorAsync(HttpContext context, Protocol protocol, int status, string message)
    {
        context.Response.StatusCode = status;
        context.Response.Headers.Remove("x-vercel-ai-ui-message-stream");
        context.Response.Headers.Remove("Cache-Control");
        var error = new { type = status >= 500 ? "api_error" : "invalid_request_error", message };
        return protocol == Protocol.Messages
            ? context.Response.WriteAsJsonAsync(new { type = "error", error }, context.RequestAborted)
            : context.Response.WriteAsJsonAsync(new { error }, context.RequestAborted);
    }

    public static async Task StreamErrorAsync(HttpContext context, Protocol protocol)
    {
        const string message = "The upstream stream ended unexpectedly.";
        var ct = context.RequestAborted;
        var error = new { type = "api_error", message };
        var payload = protocol switch
        {
            Protocol.Messages => JsonSerializer.Serialize(new { type = "error", error }),
            Protocol.Responses => JsonSerializer.Serialize(new { type = "error", code = "upstream_error", message }),
            Protocol.Vercel => JsonSerializer.Serialize(new ErrorUIPart { ErrorText = message }, JsonSerializerOptions.Web),
            _ => JsonSerializer.Serialize(new { error })
        };
        await WriteFrameAsync(context.Response, payload, protocol is Protocol.Messages or Protocol.Responses ? "error" : null, ct);
        if (protocol is Protocol.Chat or Protocol.Vercel) await WriteFrameAsync(context.Response, "[DONE]", null, ct);
    }

    internal static async Task WriteFrameAsync(HttpResponse response, string data, string? name, CancellationToken ct)
    {
        // Only controlled or JSON-decoded event names get here. Do not allow SSE field injection.
        if (name is not null && name.IndexOfAny(['\r', '\n']) >= 0) throw new IOException("Invalid upstream SSE event name.");
        await response.WriteAsync(name is null ? $"data: {data}\n\n" : $"event: {name}\ndata: {data}\n\n", ct);
        await response.Body.FlushAsync(ct);
    }
}
