using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Streaik.ChatCompletions.Models;
using Streaik.Messages;
using Streaik.OpenAI.Models;
using Streaik.Providers;
using Streaik.Responses;
using Streaik.Responses.Streaming;
using Streaik.Vercel.Models;

namespace Streaik.Gateway;

public static partial class StreaikGateway
{
    private const string JsonMediaType = "application/json";
    private const string EventStreamMediaType = "text/event-stream";

    public static IEndpointRouteBuilder MapStreaikGateway(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/v1/models", ModelsAsync).WithTags("OpenAI").Produces<ModelResponse>();
        MapGatewayPost<ChatCompletionOptions>(endpoints, "/v1/chat/completions", ChatAsync)
            .Produces<ChatCompletion>(200, JsonMediaType, EventStreamMediaType);
        MapGatewayPost<ResponseRequest>(endpoints, "/v1/responses", ResponsesAsync, Protocol.Responses)
            .Produces<ResponseResult>(200, JsonMediaType, EventStreamMediaType);
        MapGatewayPost<MessagesRequest>(endpoints, "/v1/messages", MessagesAsync, Protocol.Messages)
            .Produces<MessagesResponse>(200, JsonMediaType, EventStreamMediaType);
        MapGatewayPost<ChatRequest>(endpoints, "/api/chat", VercelAsync, Protocol.Vercel)
            .Produces<string>(200, EventStreamMediaType);
        MapDecisions(endpoints);
        MapMedia(endpoints);
        MapSkills(endpoints);
        return endpoints;
    }

    private static RouteHandlerBuilder MapGatewayPost<TRequest>(IEndpointRouteBuilder endpoints, string path,
        Func<HttpContext, Task> action, Protocol protocol = Protocol.Chat, GatewayMultipartBody? multipart = null)
        => MapGatewayPost(endpoints, path, action, new GatewayRequestBodyMetadata(typeof(TRequest), multipart), protocol);

    private static RouteHandlerBuilder MapGatewayPost(IEndpointRouteBuilder endpoints, string path,
        Func<HttpContext, Task> action, GatewayRequestBodyMetadata body, Protocol protocol = Protocol.Chat)
        => endpoints.MapPost(path, GatewayHandler(action, protocol))
            .WithTags(path switch
            {
                "/v1/messages" => "Anthropic",
                _ when path.StartsWith("/api/", StringComparison.Ordinal) => "Vercel",
                _ => "OpenAI"
            })
            .WithMetadata(body); // Accepts metadata would affect content-type matching before the handler runs.

    // Returning Delegate selects the discoverable route-handler overload, not the raw RequestDelegate overload.
    private static Delegate GatewayHandler(Func<HttpContext, Task> action, Protocol protocol = Protocol.Chat)
        => (HttpContext context) => RunAsync(context, action, protocol);

    private static async Task<IResult> ModelsAsync(HttpContext context, IEnumerable<ProviderRegistration> registrations,
        ILoggerFactory logs, CancellationToken ct)
    {
        var models = new List<Model>();
        foreach (var registration in registrations)
        {
            var id = registration.Id;
            if (context.RequestServices.GetKeyedService<IModelListingProvider>(id) is not { } provider) continue;
            try
            {
                await foreach (var model in provider.ListModelsAsync(ct).WithCancellation(ct))
                {
                    if (string.IsNullOrWhiteSpace(model.Id)) continue;
                    GatewayModelMetadata.Normalize(model, id);
                    model.Id = $"{id}/{model.Id}";
                    model.OwnedBy ??= id;
                    model.Name ??= model.Id;
                    model.Pricing ??= GatewayPricing.Rates(id, model.Id);
                    models.Add(model);
                }
            }
            catch (Exception error) when (!ct.IsCancellationRequested && error is HttpRequestException or JsonException or OperationCanceledException)
            {
                logs.CreateLogger("Streaik.Models").LogWarning("Model listing failed for {ProviderId}.", id);
            }
        }
        return Results.Json(new ModelResponse { Data = models });
    }

    private static async Task ChatAsync(HttpContext context)
    {
        var request = await ReadAsync<ChatCompletionOptions>(context, JsonSerializerOptions.Web);
        var call = new GatewayCall(context, request.Model);
        if (request.Messages is null) throw new ContractException("'messages' is required.");
        if (request.Store == true) throw new ContractException("Streaik does not store Chat Completions.");
        request.Store = false;
        request.Model = call.NativeModel;
        if (await ImageRouteAsync(call) is { } image) { await ImageAdapters.ChatAsync(call, request, image); return; }
        if (VideoRoute(call) is { } video) { await VideoViaChatAsync(call, request, video); return; }
        if (await TryChatAudioAsync(call, request)) return;
        if (call.Get<IChatCompletionsProvider>() is not { } native)
        {
            if (call.ProviderId == "bananapeel" && call.Get<IResponsesProvider>() is { } preferred)
                await ProtocolAdapters.ChatViaResponsesAsync(call, request, preferred);
            else if (call.Get<IMessagesProvider>() is { } messages) await ProtocolAdapters.ChatViaMessagesAsync(call, request, messages);
            else if (call.Get<IResponsesProvider>() is { } responses) await ProtocolAdapters.ChatViaResponsesAsync(call, request, responses);
            else if (call.Get<IImageProvider>() is { } images) await ImageAdapters.ChatAsync(call, request, images);
            else await GatewayCall.ErrorAsync(context, Protocol.Chat, 400, "Provider does not support this protocol.");
            return;
        }
        if (request.Stream == true) await call.WriteChatStreamAsync(native.StreamChatAsync(request, call.Headers, call.Ct));
        else await call.WriteAsync(await native.CompleteChatAsync(request, call.Headers, call.Ct));
    }

    private static async Task ResponsesAsync(HttpContext context)
    {
        var request = await ReadAsync<ResponseRequest>(context, ResponseJson.Default);
        var call = new GatewayCall(context, request.Model);
        if (request.PreviousResponseId is not null || request.Conversation is not null || request.Store == true || request.Background == true
            || request.Input?.Items?.Any(item => item is ResponseItemReference) == true)
            throw new ContractException("Streaik Responses require inline history and do not support stored or background responses.");
        request.Store = false;
        request.Model = call.NativeModel;
        if (UseSpeechFileAdapter(call)) { await SpeechViaResponsesAsync(call, request); return; }
        if (UseTranscriptionFileAdapter(call)) { await TranscriptionViaResponsesAsync(call, request); return; }
        if (await ImageRouteAsync(call) is { } image) { await ImageAdapters.ResponsesAsync(call, request, image); return; }
        if (VideoRoute(call) is { } video) { await VideoViaResponsesAsync(call, request, video); return; }
        if (call.Get<IResponsesProvider>() is not { } native)
        {
            if (!call.PreferChatBridge && call.Get<IMessagesProvider>() is { } messages) await ProtocolAdapters.ResponsesViaMessagesAsync(call, request, messages);
            else if (call.GetChatBridge() is { } chat) await ProtocolAdapters.ResponsesViaChatAsync(call, request, chat);
            else if (call.Get<IImageProvider>() is { } images) await ImageAdapters.ResponsesAsync(call, request, images);
            else await GatewayCall.ErrorAsync(context, Protocol.Responses, 400, "Provider does not support this protocol.");
            return;
        }
        await GatewayContainerArtifacts.PrepareAsync(call, request);
        var artifacts = new GatewayContainerArtifacts.Turn(call);
        if (request.Stream == true) await call.WriteResponsesStreamAsync(native.StreamResponseAsync(request, call.Headers, call.Ct), artifacts);
        else await call.WriteAsync(await native.CreateResponseAsync(request, call.Headers, call.Ct), artifacts);
    }

    private static async Task MessagesAsync(HttpContext context)
    {
        var request = await ReadAsync<MessagesRequest>(context, MessagesJson.Default);
        var call = new GatewayCall(context, request.Model);
        request.Model = call.NativeModel;
        if (UseSpeechFileAdapter(call)) { await SpeechViaMessagesAsync(call, request); return; }
        if (UseTranscriptionFileAdapter(call)) { await TranscriptionViaMessagesAsync(call, request); return; }
        if (await ImageRouteAsync(call) is { } image) { await ImageAdapters.MessagesAsync(call, request, image); return; }
        if (VideoRoute(call) is not null) throw new ContractException("Use the video endpoint for video generation with Messages.", 422);
        if (call.Get<IMessagesProvider>() is not { } native)
        {
            if (!call.PreferChatBridge && call.Get<IResponsesProvider>() is { } responses) await ProtocolAdapters.MessagesViaResponsesAsync(call, request, responses);
            else if (call.GetChatBridge() is { } chat) await ProtocolAdapters.MessagesViaChatAsync(call, request, chat);
            else if (call.Get<IImageProvider>() is { } images) await ImageAdapters.MessagesAsync(call, request, images);
            else await GatewayCall.ErrorAsync(context, Protocol.Messages, 400, "Provider does not support this protocol.");
            return;
        }
        if (request.Stream == true) await call.WriteMessagesStreamAsync(native.StreamMessageAsync(request, call.Headers, call.Ct));
        else await call.WriteAsync(await native.CreateMessageAsync(request, call.Headers, call.Ct));
    }

    private static async Task VercelAsync(HttpContext context)
    {
        var request = await ReadAsync<ChatRequest>(context, JsonSerializerOptions.Web);
        if (request.Reasoning is not (null or "low" or "medium" or "high"))
            throw new ContractException("reasoning must be null, low, medium, or high.");
        var call = new GatewayCall(context, request.Model);
        if (await ImageRouteAsync(call) is { } image) await ImageAdapters.VercelAsync(call, request, image);
        else if (VideoRoute(call) is { } video) await VideoViaVercelAsync(call, request, video);
        else if (!await TryVercelAudioChatAsync(call, request)) await VercelAdapter.RunAsync(call, request);
    }

    private static async Task<IImageProvider?> ImageRouteAsync(GatewayCall call)
        => call.Get<IImageModelRoutingProvider>() is { } provider && await provider.IsImageModelAsync(call.NativeModel, call.Ct) ? provider : null;

    private static async Task<T> ReadAsync<T>(HttpContext context, JsonSerializerOptions options)
    {
        if (!context.Request.HasJsonContentType()) throw new ContractException("Expected application/json.", 415);
        try { return await context.Request.ReadFromJsonAsync<T>(options, context.RequestAborted) ?? throw new JsonException(); }
        catch (JsonException) { throw new ContractException("Invalid request for this protocol."); }
    }

    private static async Task RunAsync(HttpContext context, Func<HttpContext, Task> run, Protocol protocol = Protocol.Chat)
    {
        var ct = context.RequestAborted;
        try { await run(context); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (ContractException error) when (!context.Response.HasStarted)
        { await GatewayCall.ErrorAsync(context, protocol, error.Status, error.Message); }
        catch (ProviderCredentialException error) when (!context.Response.HasStarted)
        { await GatewayCall.ErrorAsync(context, protocol, 401, error.Message); }
        catch (ProviderRequestException error) when (!context.Response.HasStarted)
        { await GatewayCall.ErrorAsync(context, protocol, error.Status, error.Message); }
        catch (HttpRequestException error) when (!context.Response.HasStarted && (int?)error.StatusCode >= 400)
        { await GatewayCall.ErrorAsync(context, protocol, (int)error.StatusCode!.Value, "The upstream provider rejected the request."); }
        catch (Exception error) when (error is HttpRequestException or JsonException or IOException or OperationCanceledException
            or ContractException or ProviderRequestException or System.Net.WebSockets.WebSocketException)
        {
            if (ct.IsCancellationRequested) return;
            if (!context.Response.HasStarted) await GatewayCall.ErrorAsync(context, protocol, 502, "The upstream response could not be read.");
            else await GatewayCall.StreamErrorAsync(context, protocol);
        }
    }
}

internal enum Protocol { Chat, Responses, Messages, Vercel }
internal sealed class ContractException(string message, int status = 400) : Exception(message)
{
    public int Status { get; } = status;
}
