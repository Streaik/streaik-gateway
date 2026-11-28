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
                    model.Pricing ??= GatewayPricing.Rates(id, model.Id, model.Type);
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
        await call.ResolveDeapiModelAsync();
        if (await ImageRouteAsync(call) is { } image) { await ImageAdapters.ChatAsync(call, request, image); return; }
        if (VideoRoute(call) is { } video) { await VideoViaChatAsync(call, request, video); return; }
        if (await TryChatAudioAsync(call, request)) return;
        if (call.Get<IChatCompletionsProvider>() is not { } native)
        {
            if (call.ProviderId == "bananapeel" && call.Get<IResponsesProvider>() is { } preferred)
                await ProtocolAdapters.ChatViaResponsesAsync(call, request, preferred);
            else if (call.Get<IMessagesProvider>() is { } messages) await ProtocolAdapters.ChatViaMessagesAsync(call, request, messages);
            else if (call.Get<IResponsesProvider>() is { } responses) await ProtocolAdapters.ChatViaResponsesAsync(call, request, responses);
            else if (call.ProviderId != "deapi" && call.Get<IImageProvider>() is { } images) await ImageAdapters.ChatAsync(call, request, images);
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
        await call.ResolveDeapiModelAsync();
        var tools = GatewayHostedTools.Extract(request);
        var hosted = tools.Local;
        IMessagesProvider? messagesMcp = null;
        if (tools.Provider.Length != 0)
        {
            if (call.Get<IResponsesProvider>() is not null)
                GatewayHostedTools.AddProviderTools(request.Tools ??= [], tools.Provider);
            else if (call.ProviderId == "anthropic" && call.Get<IMessagesProvider>() is { } messages) messagesMcp = messages;
            else
                throw new ContractException("The selected provider route cannot forward provider-executed MCP.", 422);
        }
        var nativeApprovals = new NativeMcpApprovals(call, request, hosted);
        var decisions = nativeApprovals.Validate(request);
        if (hosted.Length != 0)
        {
            var reportActivity = request.Stream == true && context.Request.Headers[Streaik.Tools.HostedToolActivity.ResponsesHeader] == "1";
            // The approval proof lives on the public Responses envelope, not in native Messages options.
            var readRound = call.ProviderId == "anthropic" && !call.PreferChatBridge && call.Get<IResponsesProvider>() is null
                && call.Get<IMessagesProvider>() is { } anthropic
                ? ProtocolAdapters.HostedMessagesReader(call, anthropic, request, tools.Provider) : null;
            var handled = await GatewayHostedTools.RunAsync(call, request, hosted, observe: reportActivity ? call.WriteHostedActivityAsync : null,
                nativeApprovals: nativeApprovals, decisions: decisions, readRound: readRound);
            if (request.Stream == true) await call.WriteResponsesStreamAsync(GatewayHostedTools.Events(reportActivity
                ? handled.Events.Where(part => part.Type != Streaik.Tools.HostedToolActivity.ResponsesEvent) : handled.Events, call.Ct));
            else await call.WriteAsync(handled.Result);
            return;
        }
        if (request.Input?.Items is { } history) request.Input = nativeApprovals.RestoreHistory(request, history.ToList());
        if (messagesMcp is not null) { await ProtocolAdapters.ResponsesViaMessagesAsync(call, request, messagesMcp, tools.Provider); return; }
        if (tools.Provider.Length == 0)
        {
            if (UseSpeechFileAdapter(call)) { await SpeechViaResponsesAsync(call, request); return; }
            if (UseTranscriptionFileAdapter(call)) { await TranscriptionViaResponsesAsync(call, request); return; }
            if (await ImageRouteAsync(call) is { } image) { await ImageAdapters.ResponsesAsync(call, request, image); return; }
            if (VideoRoute(call) is { } video) { await VideoViaResponsesAsync(call, request, video); return; }
        }
        if (call.Get<IResponsesProvider>() is not { } native)
        {
            if (!call.PreferChatBridge && call.Get<IMessagesProvider>() is { } messages) await ProtocolAdapters.ResponsesViaMessagesAsync(call, request, messages);
            else if (call.GetChatBridge() is { } chat) await ProtocolAdapters.ResponsesViaChatAsync(call, request, chat);
            else if (call.ProviderId != "deapi" && call.Get<IImageProvider>() is { } images) await ImageAdapters.ResponsesAsync(call, request, images);
            else await GatewayCall.ErrorAsync(context, Protocol.Responses, 400, "Provider does not support this protocol.");
            return;
        }
        await GatewayContainerArtifacts.PrepareAsync(call, request);
        var artifacts = new GatewayContainerArtifacts.Turn(call);
        if (request.Stream == true) await call.WriteResponsesStreamAsync(nativeApprovals.Observe(native.StreamResponseAsync(request, call.Headers, call.Ct), call.Ct), artifacts);
        else await call.WriteAsync(nativeApprovals.Observe(await native.CreateResponseAsync(request, call.Headers, call.Ct)), artifacts);
    }

    private static async Task MessagesAsync(HttpContext context)
    {
        var request = await ReadAsync<MessagesRequest>(context, MessagesJson.Default);
        var call = new GatewayCall(context, request.Model);
        request.Model = call.NativeModel;
        await call.ResolveDeapiModelAsync();
        if (UseSpeechFileAdapter(call)) { await SpeechViaMessagesAsync(call, request); return; }
        if (UseTranscriptionFileAdapter(call)) { await TranscriptionViaMessagesAsync(call, request); return; }
        if (await ImageRouteAsync(call) is { } image) { await ImageAdapters.MessagesAsync(call, request, image); return; }
        if (VideoRoute(call) is not null) throw new ContractException("Use the video endpoint for video generation with Messages.", 422);
        if (call.Get<IMessagesProvider>() is not { } native)
        {
            if (!call.PreferChatBridge && call.Get<IResponsesProvider>() is { } responses) await ProtocolAdapters.MessagesViaResponsesAsync(call, request, responses);
            else if (call.GetChatBridge() is { } chat) await ProtocolAdapters.MessagesViaChatAsync(call, request, chat);
            else if (call.ProviderId != "deapi" && call.Get<IImageProvider>() is { } images) await ImageAdapters.MessagesAsync(call, request, images);
            else await GatewayCall.ErrorAsync(context, Protocol.Messages, 400, "Provider does not support this protocol.");
            return;
        }
        ValidateAnthropicMessageInputs(call, request.MaxTokens);
        if (request.Stream == true) await call.WriteMessagesStreamAsync(native.StreamMessageAsync(request, call.Headers, call.Ct));
        else await call.WriteAsync(await native.CreateMessageAsync(request, call.Headers, call.Ct));
    }

    internal static void ValidateAnthropicMessageInputs(GatewayCall call, int? maxTokens)
    {
        if (call.ProviderId != "anthropic") return;
        if (maxTokens is not > 0) throw new ContractException("Anthropic requires a positive max_tokens value.");
        if (string.IsNullOrWhiteSpace(call.Headers.GetValueOrDefault("anthropic-version")))
            throw new ContractException("Anthropic requires the anthropic-version header.");
    }

    private static async Task VercelAsync(HttpContext context)
    {
        var request = await ReadAsync<ChatRequest>(context, JsonSerializerOptions.Web);
        if (request.Reasoning is not (null or "low" or "medium" or "high"))
            throw new ContractException("reasoning must be null, low, medium, or high.");
        var call = new GatewayCall(context, request.Model);
        await call.ResolveDeapiModelAsync();
        var tools = GatewayHostedTools.Extract(request);
        if (tools.Any) await VercelAdapter.RunAsync(call, request, tools.Local, tools.Provider);
        else if (await ImageRouteAsync(call) is { } image) await ImageAdapters.VercelAsync(call, request, image);
        else if (VideoRoute(call) is { } video) await VideoViaVercelAsync(call, request, video);
        else if (!await TryVercelAudioChatAsync(call, request))
        {
            if (call.ProviderId == "deapi") throw new ContractException("Provider model does not support chat.", 422);
            await VercelAdapter.RunAsync(call, request);
        }
    }

    private static async Task<IImageProvider?> ImageRouteAsync(GatewayCall call)
        => call.DeapiModelType == "image" ? call.Get<IImageProvider>()
            : call.Get<IImageModelRoutingProvider>() is { } provider && await provider.IsImageModelAsync(call.NativeModel, call.Ct) ? provider : null;

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
        catch (Exception error) when (error is HttpRequestException or JsonException or IOException or OperationCanceledException
            or ContractException or ProviderCredentialException or ProviderRequestException or System.Net.WebSockets.WebSocketException)
        {
            if (ct.IsCancellationRequested) return;
            var diagnostic = error switch
            {
                UpstreamHttpException upstream => GatewayCall.SafeProviderMessage(context, upstream.Diagnostic),
                UpstreamStreamException upstream => GatewayCall.SafeProviderMessage(context, upstream.Diagnostic),
                ProviderRequestException => GatewayCall.SafeProviderMessage(context, error.Message),
                ContractException or ProviderCredentialException => error.Message,
                _ => null
            };
            if (context.Response.HasStarted)
            {
                if (context.Response.ContentType == EventStreamMediaType) await GatewayCall.StreamErrorAsync(context, protocol, diagnostic);
                else context.Abort(); // Never append an SSE frame to a binary response.
                return;
            }
            var (status, fallback) = error switch
            {
                ContractException contract => (contract.Status, contract.Message),
                ProviderCredentialException => (401, error.Message),
                ProviderRequestException provider => (provider.Status, "The upstream provider rejected the request."),
                HttpRequestException http when (int?)http.StatusCode >= 400 =>
                    ((int)http.StatusCode!.Value, "The upstream provider rejected the request."),
                _ => (502, "The upstream response could not be read.")
            };
            await GatewayCall.ErrorAsync(context, protocol, status, diagnostic ?? fallback);
        }
    }
}

internal enum Protocol { Chat, Responses, Messages, Vercel }
internal sealed class UpstreamStreamException(string? diagnostic) : IOException("The upstream reported an error.")
{
    internal string? Diagnostic { get; } = diagnostic;
}

internal sealed class ContractException(string message, int status = 400) : Exception(message)
{
    public int Status { get; } = status;
}
