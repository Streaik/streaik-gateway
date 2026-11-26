using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Streaik.OpenAI.Model.Providers.OpenAI.Live;
using Streaik.OpenAI.Models;
using Streaik.Providers;
using Streaik.Vercel.Models;

namespace Streaik.Gateway;

public static partial class StreaikGateway
{
    private const int MaxMediaBytes = 25 * 1024 * 1024;
    private static readonly JsonSerializerOptions Web = JsonSerializerOptions.Web;

    private static void MapMedia(IEndpointRouteBuilder endpoints)
    {
        MapGatewayPost<OpenAIImageGenerationRequest>(endpoints, "/v1/images/generations", ImageGenerationAsync)
            .Produces<OpenAIImagesResponse>(200, JsonMediaType, EventStreamMediaType);
        MapGatewayPost<OpenAIImageEditRequest>(endpoints, "/v1/images/edits", ImageEditAsync, multipart: GatewayMultipartBody.ImageEdit)
            .Produces<OpenAIImagesResponse>(200, JsonMediaType, EventStreamMediaType);
        MapGatewayPost<ImageRequest>(endpoints, "/api/images", VercelImagesAsync).Produces<ImageResponse>();
        MapGatewayPost<VideoRequest>(endpoints, "/api/videos", StartVideoAsync).Produces<VideoOperationStartResult>();
        endpoints.MapGet("/api/videos/{providerId}/{operation}", GatewayHandler(VideoStatusAsync))
            .WithTags("Vercel").Produces<VideoOperationStatusResult>();
        MapGatewayPost<AudioSpeechRequest>(endpoints, "/v1/audio/speech", NativeSpeechAsync)
            .Produces<string>(200, "audio/mpeg", "audio/wav", "audio/x-wav", "audio/ogg", "audio/flac", "audio/aac",
                "application/octet-stream", EventStreamMediaType);
        MapGatewayPost<SpeechRequest>(endpoints, "/api/speech", SpeechAsync).Produces<SpeechResponse>();
        MapGatewayPost<StreamingSpeechRequest>(endpoints, "/api/speech/stream", SpeechStreamAsync)
            .Produces<string>(200, EventStreamMediaType);
        MapGatewayPost(endpoints, "/v1/audio/transcriptions", NativeTranscriptionAsync,
            new GatewayRequestBodyMetadata(null, GatewayMultipartBody.AudioTranscription))
            .Produces<JsonElement>(200, JsonMediaType, "text/plain", "text/vtt", "application/octet-stream", EventStreamMediaType);
        MapGatewayPost<TranscriptionRequest>(endpoints, "/api/transcriptions", TranscriptionAsync).Produces<TranscriptionResponse>();
        MapGatewayPost<StreamingTranscriptionRequest>(endpoints, "/api/transcriptions/stream", TranscriptionStreamAsync)
            .Produces<string>(200, EventStreamMediaType);
        MapGatewayPost<RealtimeTokenRequest>(endpoints, "/api/realtime/token/{**model}", RealtimeTokenAsync).Produces<RealtimeTokenResponse>();
        MapGatewayPost<RealtimeSessionRequest>(endpoints, "/api/realtime/session/{**model}", RealtimeSessionAsync).Produces<RealtimeSessionResponse>();
        MapGatewayPost<JsonObject>(endpoints, "/v1/realtime/client_secrets", NativeRealtimeTokenAsync).Produces<JsonObject>();
        MapGatewayPost<LiveSessionRequest>(endpoints, "/v1/live/sessions", NativeLiveSessionAsync)
            .Produces<LiveSessionResponse>(StatusCodes.Status201Created);
        MapGatewayPost<OpenAIEmbeddingRequest>(endpoints, "/v1/embeddings", NativeEmbeddingsAsync).Produces<OpenAIEmbeddingResponse>();
        MapGatewayPost<EmbeddingRequest>(endpoints, "/api/embeddings", EmbeddingsAsync).Produces<EmbeddingResponse>();
        MapGatewayPost<RerankingRequest>(endpoints, "/api/rerank", RerankAsync).Produces<RerankingResponse>();
    }

    private static async Task ImageGenerationAsync(HttpContext context)
    {
        var request = await ReadAsync<OpenAIImageGenerationRequest>(context, Web);
        var call = new GatewayCall(context, request.Model);
        // Ideogram V4 can use a structured prompt instead of text.
        if (call.ProviderId != "ideogram") Need(request.Prompt, "prompt");
        request.Model = call.NativeModel;
        if (request.Stream == true && call.Get<IStreamingImagesProvider>() is { } native)
        { await WriteImageStreamAsync(call, native.StreamImagesAsync(request, call.Headers, call.Ct)); return; }
        var result = await call.Require<IImageProvider>().GenerateImageAsync(request, call.Headers, call.Ct);
        await WriteImagesAsync(call, result, request.Stream == true, edit: false);
    }

    private static async Task ImageEditAsync(HttpContext context)
    {
        OpenAIImageEditRequest request;
        if (context.Request.HasJsonContentType()) request = await ReadAsync<OpenAIImageEditRequest>(context, Web);
        else
        {
            var form = await ReadMediaFormAsync(context);
            var json = new JsonObject();
            try
            {
                foreach (var (key, value) in form)
                    json[key] = key switch
                    {
                        "stream" when bool.TryParse(value, out var boolean) => JsonValue.Create(boolean),
                        "n" or "seed" or "partial_images" or "output_compression" or "frame_count" or "frame_duration_ms"
                            when int.TryParse(value, out var number) => JsonValue.Create(number),
                        "svg" or "markup" => JsonNode.Parse(value.ToString()),
                        _ => JsonValue.Create(value.ToString())
                    };
                request = BindImageSettings<OpenAIImageEditRequest>(json);
            }
            catch (JsonException) { throw new ContractException("Invalid image edit settings."); }
            request.ImageFiles = form.Files.Where(f => f.Name is "image" or "image[]").ToArray();
            request.MaskFile = form.Files.GetFile("mask") ?? form.Files.GetFile("mask[]");
        }
        if (request.Images is not { Length: > 0 } && request.ImageFiles is not { Length: > 0 })
            throw new ContractException("An edit requires at least one image.");
        var call = new GatewayCall(context, request.Model);
        if (call.ProviderId != "ideogram") Need(request.Prompt, "prompt");
        request.Model = call.NativeModel;
        if (request.Stream == true && call.Get<IStreamingImagesProvider>() is { } native)
        { await WriteImageStreamAsync(call, native.StreamImagesAsync(request, call.Headers, call.Ct)); return; }
        var result = await call.Require<IImageProvider>().EditImageAsync(request, call.Headers, call.Ct);
        await WriteImagesAsync(call, result, request.Stream == true, edit: true);
    }

    private static async Task WriteImageStreamAsync(GatewayCall call, IAsyncEnumerable<JsonElement> source)
    {
        StartMediaStream(call);
        await foreach (var evt in source.WithCancellation(call.Ct))
        {
            if (!evt.TryGetProperty("type", out var discriminator) || discriminator.ValueKind != JsonValueKind.String)
                throw new JsonException("Missing image event type.");
            var type = discriminator.GetString();
            if (type == "error") throw new IOException("The image stream failed.");
            // Serialize rather than forwarding raw JSON: pretty-print whitespace is not valid inside one SSE data field.
            await GatewayCall.WriteFrameAsync(call.Context.Response, JsonSerializer.Serialize(evt, Web), type, call.Ct);
            if (type is "image_generation.completed" or "image_edit.completed") return;
        }
        throw new IOException("The upstream image stream ended without completion.");
    }

    private static async Task WriteImagesAsync(GatewayCall call, OpenAIImagesResponse result, bool stream, bool edit)
    {
        if (!stream) { await call.Context.Response.WriteAsJsonAsync(result, Web, call.Ct); return; }
        if (result.Data is not [{ B64Json: { Length: > 0 } image }])
            throw new ContractException("Image streaming requires a single base64 output.", 502);
        OpenAIImageStreamEventBase completed = edit
            ? new OpenAIImageEditCompleted { B64Json = image, Usage = result.Usage }
            : new OpenAIImageGenerationCompleted { B64Json = image, Usage = result.Usage };
        completed.CreatedAt = result.Created;
        completed.OutputFormat = result.OutputFormat;
        completed.Size = result.Size;
        completed.Quality = result.Quality;
        completed.Background = result.Background;
        StartMediaStream(call);
        await GatewayCall.WriteFrameAsync(call.Context.Response, JsonSerializer.Serialize(completed, completed.GetType(), Web), completed.Type, call.Ct);
    }

    private static async Task VercelImagesAsync(HttpContext context)
    {
        var request = await ReadAsync<ImageRequest>(context, Web);
        var call = new GatewayCall(context, request.Model);
        if (call.ProviderId is not ("ideogram" or "segmind")
            && !(call.ProviderId == "picsart" && call.NativeModel.StartsWith("logo/", StringComparison.Ordinal))) Need(request.Prompt, "prompt");
        var provider = call.Require<IImageProvider>();
        var files = request.Files?.Select(ImageReference).ToArray();
        if (request.Mask is not null && files is not { Length: > 0 }) throw new ContractException("A mask requires an image.");
        // Bind the same typed fields as the native endpoints, while preserving provider extensions.
        // Gateway-owned routing, input and transport fields cannot be supplied through provider options.
        var options = SelectedOptions(call, request.ProviderOptions, "model", "prompt", "n", "size", "stream", "response_format", "partial_images", "files", "images", "mask");
        // Yollomi native options can share a name with a standard field but have a different shape.
        var body = options is null ? new JsonObject() : call.ProviderId == "yollomi"
            ? new JsonObject { [call.ProviderId] = JsonSerializer.SerializeToNode(options, Web) }
            : JsonSerializer.SerializeToNode(options, Web)!.AsObject();
        body["model"] = call.NativeModel;
        body["prompt"] = request.Prompt;
        if (request.Size is not null) body["size"] = request.Size;
        if (request.N is { } n) body["n"] = n;
        // Segmind's native fields take precedence over the Vercel convenience defaults.
        var clarifai = call.ProviderId == "clarifai";
        if (!clarifai && request.AspectRatio is not null && (call.ProviderId != "segmind" || !body.ContainsKey("aspect_ratio"))) body["aspect_ratio"] = request.AspectRatio;
        if (!clarifai && request.Seed is { } seed && (call.ProviderId != "segmind" || !body.ContainsKey("seed"))) body["seed"] = seed;
        OpenAIImagesResponse result;
        if (files is { Length: > 0 })
        {
            var edit = BindImageSettings<OpenAIImageEditRequest>(body);
            edit.Images = files;
            edit.Mask = request.Mask is null ? null : ImageReference(request.Mask);
            result = await provider.EditImageAsync(edit, call.Headers, call.Ct);
        }
        else
        {
            var generation = BindImageSettings<OpenAIImageGenerationRequest>(body);
            if (clarifai || call.ProviderId == "openai" && call.NativeModel is "dall-e-2" or "dall-e-3") generation.ResponseFormat = "b64_json";
            result = await provider.GenerateImageAsync(generation, call.Headers, call.Ct);
        }
        var providerWarnings = result.AdditionalProperties?.GetValueOrDefault("warnings") is { ValueKind: JsonValueKind.Array } warnings
            ? warnings.EnumerateArray().Select(static item => (object)item).ToList() : [];
        if (clarifai && request.Seed.HasValue) providerWarnings.Add(new { type = "unsupported", feature = "seed" });
        if (clarifai && !string.IsNullOrWhiteSpace(request.AspectRatio)) providerWarnings.Add(new { type = "unsupported", feature = "aspectRatio" });
        await context.Response.WriteAsJsonAsync(new ImageResponse
        {
            Images = result.Data?.Select(item => VercelImageData(item, result.OutputFormat, call.ProviderId == "digitalocean")).ToArray(),
            ProviderMetadata = ImageAdapters.ProviderMetadata(result, call.ProviderId),
            Warnings = providerWarnings,
            Response = new() { ModelId = call.Model, Timestamp = DateTime.UtcNow },
            Usage = result.Usage is { } usage ? new() { InputTokens = usage.InputTokens, OutputTokens = usage.OutputTokens, TotalTokens = usage.TotalTokens } : null
        }, Web, call.Ct);
    }

    private static string VercelImageData(OpenAIImageData item, string? format, bool allowUrl)
    {
        // DigitalOcean's SDK surface returns image URLs as well as inline images. Do not fetch them at the gateway.
#pragma warning disable CS0618 // Compatibility with upstream URL outputs, only on the SDK image endpoint.
        if (allowUrl && item.B64Json is null && Uri.TryCreate(item.Url, UriKind.Absolute, out var url)
            && url.Scheme == Uri.UriSchemeHttps && url.UserInfo.Length == 0) return item.Url!;
#pragma warning restore CS0618
        var image = item.B64Json ?? throw new IOException("The provider did not return an inline image.");
        var mediaType = format?.ToLowerInvariant() switch
        {
            "png" => "image/png", "jpeg" or "jpg" => "image/jpeg", "webp" => "image/webp",
            "gif" => "image/gif", "svg" => "image/svg+xml", "avif" => "image/avif", "bmp" => "image/bmp", _ => null
        };
        return mediaType is null || image.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ? image : $"data:{mediaType};base64,{image}";
    }

    private static T BindImageSettings<T>(JsonObject body) where T : class
    {
        try { return body.Deserialize<T>(Web) ?? throw new JsonException(); }
        catch (JsonException) { throw new ContractException("Invalid image settings."); }
    }

    private static OpenAIImageReference ImageReference(ImageFile file)
        => new() { ImageUrl = file is ImageFileUrl url ? url.Url : $"data:{file.MediaType};base64,{file.Data}" };

    private sealed record VideoReference(string Operation, string Model);

    private static async Task StartVideoAsync(HttpContext context)
    {
        var request = await ReadAsync<VideoRequest>(context, Web);
        var call = new GatewayCall(context, request.Model);
        // AtlasCloud accepts image-only inputs. Bria and Decart validate workflow-specific prompts; FLUX editing takes a video or draft cache.
        if (call.ProviderId is not ("atlascloud" or "bria" or "decart") && (call.ProviderId != "blackforestlabs"
            || call.NativeModel.ToLowerInvariant() is not ("flux-3-video-draft-enhance" or "flux-tools/video-upscale-v1")))
            Need(request.Prompt, "prompt");
        var provider = call.Require<IVideoProvider>();
        var references = new GatewayReferences(call, "video");
        request.Model = call.NativeModel;
        var result = await provider.StartVideoAsync(request, call.Headers, call.Ct);
        result.Operation = $"{call.ProviderId}/{references.Protect(new VideoReference(result.Operation, call.Model))}";
        result.Response.ModelId = call.Model;
        await context.Response.WriteAsJsonAsync(result, Web, call.Ct);
    }

    private static async Task VideoStatusAsync(HttpContext context)
    {
        var call = new GatewayCall(context, $"{context.Request.RouteValues["providerId"]}/video");
        var provider = call.Require<IVideoProvider>();
        var reference = new GatewayReferences(call, "video").Read<VideoReference>((string)context.Request.RouteValues["operation"]!);
        var result = await provider.GetVideoAsync(reference.Operation, call.Headers, call.Ct);
        result.Response.ModelId = reference.Model;
        await context.Response.WriteAsJsonAsync(result, Web, call.Ct);
    }

    private static async Task NativeEmbeddingsAsync(HttpContext context)
    {
        var request = await ReadAsync<OpenAIEmbeddingRequest>(context, Web);
        var call = new GatewayCall(context, request.Model);
        if (request.Input.ValueKind is not (JsonValueKind.Array or JsonValueKind.String)) throw new ContractException("'input' is required.");
        request.Model = call.NativeModel;
        var result = await call.Require<IEmbeddingsProvider>().EmbedAsync(request, call.Headers, call.Ct);
        result.Model = call.Model;
        await context.Response.WriteAsJsonAsync(result, Web, call.Ct);
    }

    private static async Task EmbeddingsAsync(HttpContext context)
    {
        var request = await ReadAsync<EmbeddingRequest>(context, Web);
        var call = new GatewayCall(context, request.Model);
        if (request.Values?.Any() != true) throw new ContractException("'values' is required.");
        var native = new OpenAIEmbeddingRequest { Model = call.NativeModel,
            Input = JsonSerializer.SerializeToElement(request.Values, Web), Dimensions = request.Dimensions, EncodingFormat = "float",
            AdditionalProperties = call.ProviderId == "isaacus"
                ? SelectedOptions(call, request.ProviderOptions, "model", "input", "encoding_format") // Isaacus accepts native null dimensions.
                : SelectedOptions(call, request.ProviderOptions, "model", "input", "dimensions", "encoding_format") };
        OpenAIEmbeddingResponse result;
        EmbeddingResponseMetadata? metadata = null;
        if (call.Get<IEmbeddingMetadataProvider>() is { } provider)
        {
            var (embedding, body, headers) = await provider.EmbedWithMetadataAsync(native, call.Headers, call.Ct);
            result = embedding;
            metadata = new() { Body = body,
                Headers = headers.Where(pair => pair.Value is not null).ToDictionary(pair => pair.Key, pair => pair.Value!, StringComparer.OrdinalIgnoreCase) };
        }
        else result = await call.Require<IEmbeddingsProvider>().EmbedAsync(native, call.Headers, call.Ct);
        await context.Response.WriteAsJsonAsync(new EmbeddingResponse
        {
            Embeddings = result.Data.OrderBy(item => item.Index).Select(item => item.Embedding.Deserialize<double[]>() ?? throw new JsonException()).ToArray(),
            Usage = new() { Tokens = result.Usage.TotalTokens }, Response = metadata
        }, Web, call.Ct);
    }

    private static async Task RerankAsync(HttpContext context)
    {
        var request = await ReadAsync<RerankingRequest>(context, Web);
        Need(request.Query, "query");
        if (request.Documents?.Values.ValueKind != JsonValueKind.Array || request.TopN is <= 0)
            throw new ContractException("'documents' must contain an array, and topN must be positive.");
        var call = new GatewayCall(context, request.Model);
        request.Model = call.NativeModel;
        var result = await call.Require<IRerankingProvider>().RerankAsync(request, call.Headers, call.Ct);
        result.Response.ModelId = call.Model;
        await context.Response.WriteAsJsonAsync(result, Web, call.Ct);
    }

    private static async Task RealtimeTokenAsync(HttpContext context)
    {
        var call = new GatewayCall(context, (string?)context.Request.RouteValues["model"]);
        var request = await ReadAsync<RealtimeTokenRequest>(context, Web);
        var result = await call.Require<IRealtimeTokenProvider>().CreateRealtimeTokenAsync(call.NativeModel, request, call.Headers, call.Ct);
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsJsonAsync(result, Web, call.Ct);
    }

    private static async Task NativeRealtimeTokenAsync(HttpContext context)
    {
        var request = await ReadAsync<JsonObject>(context, Web);
        var session = request["session"] as JsonObject;
        var isTranscription = ReadString(session?["type"]) == "transcription";
        var transcription = isTranscription ? InputTranscription(session) : null;
        var sessionModel = ReadString(session?["model"]);
        var routeModel = ReadString(request["model"]);
        var inputModel = ReadString(transcription?["model"]);
        var model = isTranscription ? routeModel ?? inputModel : sessionModel ?? routeModel;
        var call = new GatewayCall(context, model);
        context.Response.Headers.CacheControl = "no-store";
        if (isTranscription && (call.ProviderId != "openai" || transcription is null || sessionModel is not null ||
            string.IsNullOrWhiteSpace(inputModel) || (inputModel != call.NativeModel && inputModel != call.Model)))
            throw new ContractException("Use an OpenAI model in session.audio.input.transcription.model for a transcription session.");
        if (call.Get<IOpenAIRealtimeProvider>() is { } provider)
        {
            if (session is null) { session = new JsonObject(); request["session"] = session; }
            request.Remove("model");
            if (isTranscription) transcription!["model"] = call.NativeModel;
            else
            {
                session["type"] ??= "realtime";
                session["model"] = call.NativeModel;
            }
            var result = await provider.CreateClientSecretAsync(request, call.Headers, call.Ct);
            if (JsonNode.Parse(result.GetRawText()) is not JsonObject response) throw new JsonException();
            if (response["session"] is JsonObject returned)
            {
                if (isTranscription)
                {
                    if (InputTranscription(returned) is { } returnedInput) returnedInput["model"] = call.Model;
                }
                else returned["model"] = call.Model;
            }
            await context.Response.WriteAsJsonAsync(response, Web, call.Ct);
        }
        else
        {
            var result = await call.Require<IRealtimeTokenProvider>().CreateRealtimeTokenAsync(call.NativeModel, new(), call.Headers, call.Ct);
            await context.Response.WriteAsJsonAsync(new { value = result.Token, expires_at = result.ExpiresAt, url = result.Url }, Web, call.Ct);
        }

        static string? ReadString(JsonNode? value) => value is JsonValue node && node.TryGetValue<string>(out var text) ? text : null;
        static JsonObject? InputTranscription(JsonObject? value)
            => (((value?["audio"] as JsonObject)?["input"] as JsonObject)?["transcription"] as JsonObject);
    }

    private static async Task RealtimeSessionAsync(HttpContext context)
    {
        var call = new GatewayCall(context, (string?)context.Request.RouteValues["model"]);
        var request = await ReadAsync<RealtimeSessionRequest>(context, Web);
        Need(request.Sdp, "sdp");
        var result = await call.Require<IRealtimeSessionProvider>().CreateRealtimeSessionAsync(call.NativeModel, request, call.Headers, call.Ct);
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsJsonAsync(result, Web, call.Ct);
    }

    private static async Task NativeLiveSessionAsync(HttpContext context)
    {
        var request = await ReadAsync<LiveSessionRequest>(context, Web);
        if (request.Session is null || request.Transport is null) throw new ContractException("session and transport are required.");
        var call = new GatewayCall(context, request.Session.Model);
        if (request.Session.Store == true) throw new ContractException("The gateway requires store: false.");
        request.Session.Model = call.NativeModel;
        request.Session.Store = false;
        var provider = call.Get<IOpenAILiveSessionProvider>() ?? throw new ContractException("Provider does not support Live sessions.", 501);
        var result = await provider.CreateLiveSessionAsync(request, call.Headers, call.Ct);
        context.Response.Headers.CacheControl = "no-store";
        context.Response.StatusCode = StatusCodes.Status201Created;
        await context.Response.WriteAsJsonAsync(result, Web, call.Ct);
    }

    private static async Task<IFormCollection> ReadMediaFormAsync(HttpContext context)
    {
        if (!context.Request.HasFormContentType || context.Request.ContentType?.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase) != true)
            throw new ContractException("Expected multipart/form-data.", 415);
        if (context.Request.ContentLength > MaxMediaBytes) throw new ContractException("Media request is too large.", 413);
        try
        {
            var form = await context.Request.ReadFormAsync(new Microsoft.AspNetCore.Http.Features.FormOptions { MultipartBodyLengthLimit = MaxMediaBytes }, context.RequestAborted);
            if (form.Files.Sum(file => file.Length) > MaxMediaBytes) throw new ContractException("Media request is too large.", 413);
            return form;
        }
        catch (InvalidDataException) { throw new ContractException("Invalid or oversized multipart request."); }
    }

    private static void Need(string? value, string name)
    { if (string.IsNullOrWhiteSpace(value)) throw new ContractException($"'{name}' is required."); }

    private static void StartMediaStream(GatewayCall call)
    { call.Context.Response.ContentType = "text/event-stream"; call.Context.Response.Headers.CacheControl = "no-cache"; }

    private static Dictionary<string, JsonElement>? SelectedOptions(GatewayCall call, Dictionary<string, JsonElement>? options, params string[] reserved)
        => options?.TryGetValue(call.ProviderId, out var selected) == true && selected.ValueKind == JsonValueKind.Object
            ? selected.EnumerateObject().Where(p => !reserved.Contains(p.Name, StringComparer.OrdinalIgnoreCase))
                .ToDictionary(p => p.Name, p => p.Value) : null;
}
