using System.Text.Json;
using System.Text.Json.Nodes;
using Streaik.ChatCompletions.Models;
using Streaik.Messages;
using Streaik.OpenAI.Models;
using Streaik.Providers;
using Streaik.Responses;
using Streaik.Responses.Output;
using Streaik.Responses.Streaming;
using Streaik.Vercel.Models;

namespace Streaik.Gateway;

internal static class ImageAdapters
{
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;

    public static async Task ChatAsync(GatewayCall call, ChatCompletionOptions request, IImageProvider provider)
    {
        if (request.Tools?.Any() == true || request.Functions?.Any() == true || request.ResponseFormat is not null || request.NumberOfChoices is > 1
            || request.Modalities?.Any(x => x is not ("image" or "text")) == true)
            throw new ContractException("Image Chat Completions do not support tools, structured output, multiple choices or audio.", 422);
        var content = (request.Messages.LastOrDefault(x => x.Role == "user") ?? throw MissingPrompt()).Content;
        var (prompt, files) = Collect(content.Text, content.Parts ?? [], p => p switch
        {
            ChatMessageTextPart text => (text.Text, null), ChatMessageImagePart image => (null, Image(image.ImageUrl.Url)),
            _ => throw UnsupportedPart()
        });
        var result = await RunAsync(call, provider, prompt, files, Options(call, request.AdditionalProperties));
        var images = Images(result).Select(x => new { type = "image_url", image_url = new { url = DataUrl(result, x) } }).ToArray();
        var extras = new Dictionary<string, JsonElement> { ["images"] = JsonSerializer.SerializeToElement(images) };
        var id = NewId("chatcmpl");
        var created = Timestamp(result);
        if (request.Stream == true) await call.WriteChatStreamAsync(Parts());
        else await call.WriteAsync(new ChatCompletion { Id = id, Created = created, MetadataExtensions = ProviderMetadata(result, call.ProviderId),
            Choices = [new ChatCompletionChoice { FinishReason = "stop", Message = new ChatCompletionResponseMessage { Content = "", AdditionalProperties = extras } }] });

        async IAsyncEnumerable<ChatCompletionUpdate> Parts()
        {
            await Task.CompletedTask;
            yield return new() { Id = id, Created = created, Choices = [new() { Delta = new() { Role = "assistant" } }] };
            yield return new() { Id = id, Created = created, Choices = [new() { Delta = new() { AdditionalProperties = extras } }] };
            yield return new() { Id = id, Created = created, AdditionalProperties = ProviderMetadata(result, call.ProviderId) is { } metadata
                ? new() { ["metadata"] = JsonSerializer.SerializeToElement(metadata) } : null, Choices = [new() { FinishReason = "stop", Delta = new() }] };
        }
    }

    public static async Task MessagesAsync(GatewayCall call, MessagesRequest request, IImageProvider provider)
    {
        if (request.Tools?.Count > 0 || request.Thinking is not null || request.OutputConfig is not null)
            throw new ContractException("Image Messages do not support tools, thinking or structured output.", 422);
        var content = (request.Messages?.LastOrDefault(x => x.Role == "user") ?? throw MissingPrompt()).Content;
        if (content.IsRaw) throw UnsupportedPart();
        var (prompt, files) = Collect(content.Text, content.Blocks ?? [], p => p switch
        {
            { Type: "text" } => (p.Text, null),
            { Type: "image", Source: { Type: "base64", Data: { } data, MediaType: { } type } } => (null, Image($"data:{type};base64,{data}")),
            { Type: "image", Source: { Type: "url", Url: { } url } } => (null, Image(url)),
            _ => throw UnsupportedPart()
        });
        var result = await RunAsync(call, provider, prompt, files, Options(call, request.AdditionalProperties));
        var blocks = Images(result).Select(x => new MessageContentBlock { Type = "image", Source = new() { Type = "base64", MediaType = Mime(result), Data = x.B64Json } }).ToList();
        var id = NewId("msg");
        MessagesResponse Message(bool completed) => new() { Id = id, Type = "message", Role = "assistant",
            Content = completed ? blocks : [], StopReason = completed ? "end_turn" : null, Metadata = completed ? ProviderMetadata(result, call.ProviderId) : null };
        if (request.Stream == true) await call.WriteMessagesStreamAsync(Parts());
        else await call.WriteAsync(Message(true));

        async IAsyncEnumerable<MessageStreamPart> Parts()
        {
            await Task.CompletedTask;
            yield return new() { Type = "message_start", Message = Message(false) };
            foreach (var (block, index) in blocks.Select((b, i) => (b, i)))
            {
                yield return new() { Type = "content_block_start", Index = index, ContentBlock = block };
                yield return new() { Type = "content_block_stop", Index = index };
            }
            yield return new() { Type = "message_delta", Delta = new() { StopReason = "end_turn" } };
            yield return new() { Type = "message_stop", Metadata = ProviderMetadata(result, call.ProviderId) };
        }
    }

    public static async Task ResponsesAsync(GatewayCall call, ResponseRequest request, IImageProvider provider)
    {
        if (request.Tools?.Any(tool => tool.Type != "image_generation") == true || request.Tools?.Count > 1 || request.Reasoning is not null || request.Text is not null)
            throw new ContractException("Image Responses support only the image_generation tool, without reasoning or structured output.", 422);
        var content = request.Input?.Items?.OfType<ResponseInputMessage>().LastOrDefault(x => x.Role == ResponseRole.User)?.Content;
        var (prompt, files) = Collect(request.Input?.Text ?? content?.Text, content?.Parts ?? [], p => p switch
        {
            InputTextPart text => (text.Text, null), InputImagePart image => (null, Image(image.ImageUrl, image.FileId)),
            _ => throw UnsupportedPart()
        });
        var options = Options(call, request.AdditionalProperties);
        foreach (var entry in request.Tools?.SingleOrDefault()?.Extra ?? []) options[entry.Key] = JsonNode.Parse(entry.Value.GetRawText());
        var result = await RunAsync(call, provider, prompt, files, options);
        var items = Images(result).Select(x => new ResponseOutputImageGenerationCall { Id = NewId("ig"), Status = "completed",
            Result = x.B64Json, OutputFormat = result.OutputFormat, RevisedPrompt = x.RevisedPrompt,
            Quality = result.Quality, Size = result.Size, Action = files.Length == 0 ? "generate" : "edit" }).ToArray();
        var id = NewId("resp");
        var created = Timestamp(result);
        ResponseResult Envelope(bool completed) => new() { Id = id, CreatedAt = created, Status = completed ? "completed" : "in_progress",
            Output = completed ? [.. items] : [], Metadata = completed ? ProviderMetadata(result, call.ProviderId)?.ToDictionary(x => x.Key, x => (object?)x.Value) : null };
        if (request.Stream == true) await call.WriteResponsesStreamAsync(Parts());
        else await call.WriteAsync(Envelope(true));

        async IAsyncEnumerable<ResponseStreamPart> Parts()
        {
            await Task.CompletedTask;
            var sequence = 0;
            yield return new ResponseCreated { SequenceNumber = sequence++, Response = Envelope(false) };
            foreach (var (image, index) in items.Select((item, i) => (item, i)))
            {
                yield return new ResponseOutputItemAdded { SequenceNumber = sequence++, OutputIndex = index,
                    Item = new ResponseStreamItem { Type = image.Type, Id = image.Id, Status = "in_progress" } };
                yield return new ResponseImageGenerationCallCompleted { SequenceNumber = sequence++, ItemId = image.Id, OutputIndex = index };
                yield return new ResponseOutputItemDone { SequenceNumber = sequence++, OutputIndex = index,
                    Item = JsonSerializer.SerializeToElement(image, ResponseJson.Default).Deserialize<ResponseStreamItem>(ResponseJson.Default)! };
            }
            yield return new ResponseCompleted { SequenceNumber = sequence, Response = Envelope(true) };
        }
    }

    public static async Task VercelAsync(GatewayCall call, ChatRequest request, IImageProvider provider)
    {
        if (request.Tools?.Any() == true || request.ResponseFormat is not null)
            throw new ContractException("Image Chat does not support tools or structured output.", 422);
        var content = (request.Messages.LastOrDefault(x => x.Role == Role.user) ?? throw MissingPrompt()).Parts;
        var (prompt, files) = Collect<UIMessagePart>(null, content, p => p switch
        {
            TextUIPart text => (text.Text, null), FileUIPart file when file.MediaType.StartsWith("image/", StringComparison.Ordinal) => (null, Image(file.Url)),
            _ => throw UnsupportedPart()
        });
        var result = await RunAsync(call, provider, prompt, files, Options(call, request.ProviderMetadata));
        var images = Images(result);
        await call.WriteVercelStreamAsync(Parts());

        async IAsyncEnumerable<UIMessagePart> Parts()
        {
            await Task.CompletedTask;
            yield return new StartStepUIPart();
            foreach (var image in images) yield return new FileUIPart { MediaType = Mime(result), Url = DataUrl(result, image) };
            yield return new FinishUIPart { FinishReason = "stop", MessageMetadata = new() { Model = call.Model,
                Timestamp = DateTimeOffset.FromUnixTimeSeconds(Timestamp(result)), Usage = new(), ProviderMetadata = ProviderMetadata(result, call.ProviderId) } };
        }
    }

    private static (string Prompt, OpenAIImageReference[] Files) Collect<T>(string? text, IEnumerable<T> parts, Func<T, (string? Text, OpenAIImageReference? File)> read)
    {
        var mapped = parts.Select(read).ToArray();
        var prompt = string.Join('\n', mapped.Select(x => x.Text).Prepend(text).Where(s => !string.IsNullOrWhiteSpace(s)));
        return (prompt, mapped.Select(x => x.File).OfType<OpenAIImageReference>().ToArray());
    }

    private static Task<OpenAIImagesResponse> RunAsync(GatewayCall call, IImageProvider provider, string prompt, OpenAIImageReference[] files, JsonObject options)
    {
        if (call.ProviderId is not ("ideogram" or "vanceai") && string.IsNullOrWhiteSpace(prompt)) throw MissingPrompt();
        var action = options["action"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
        if (action is not (null or "auto" or "edit" or "generate") || action == "edit" && files.Length == 0 || action == "generate" && files.Length > 0)
            throw new ContractException("Image action does not match the input images.", 422);
        foreach (var key in new[] { "model", "prompt", "stream", "partial_images", "action", "images", "response_format" }) options.Remove(key);
        options["model"] = call.NativeModel;
        options["prompt"] = prompt;
        if (files.Length == 0)
        {
            var generation = options.Deserialize<OpenAIImageGenerationRequest>(Json) ?? throw MissingPrompt();
            generation.ResponseFormat = "b64_json";
            return provider.GenerateImageAsync(generation, call.Headers, call.Ct);
        }
        var edit = options.Deserialize<OpenAIImageEditRequest>(Json) ?? throw MissingPrompt();
        edit.Images = files;
        return provider.EditImageAsync(edit, call.Headers, call.Ct);
    }

    private static JsonObject Options(GatewayCall call, Dictionary<string, JsonElement>? extras)
    {
        if (extras?.TryGetValue(call.ProviderId, out var direct) == true && direct.ValueKind == JsonValueKind.Object) return JsonObject.Create(direct)!;
        if (extras is not null)
            foreach (var key in new[] { "provider_options", "providerOptions" })
                if (extras.TryGetValue(key, out var all) && all.ValueKind == JsonValueKind.Object
                    && all.TryGetProperty(call.ProviderId, out var selected) && selected.ValueKind == JsonValueKind.Object)
                    return JsonObject.Create(selected)!;
        return new JsonObject();
    }

    private static OpenAIImageData[] Images(OpenAIImagesResponse result) => result.Data is { Count: > 0 } data && data.All(x => !string.IsNullOrWhiteSpace(x.B64Json))
        ? data.ToArray() : throw new ContractException("The image provider did not return inline images.", 502);
    private static OpenAIImageReference Image(string? url, string? file = null) => new() { ImageUrl = url, FileId = file };
    private static string DataUrl(OpenAIImagesResponse result, OpenAIImageData image) => $"data:{Mime(result)};base64,{image.B64Json}";
    private static string Mime(OpenAIImagesResponse result) => result.OutputFormat switch
    { "jpeg" or "jpg" => "image/jpeg", "webp" => "image/webp", "gif" => "image/gif", "avif" => "image/avif", "bmp" => "image/bmp", "svg" => "image/svg+xml", _ => "image/png" };
    private static long Timestamp(OpenAIImagesResponse result) => result.Created > 0 ? result.Created : DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private static string NewId(string prefix) => $"{prefix}_{Guid.NewGuid():N}";
    internal static Dictionary<string, JsonElement>? ProviderMetadata(OpenAIImagesResponse result, string provider)
    {
        if (result.AdditionalProperties?.GetValueOrDefault("providerMetadata") is { ValueKind: JsonValueKind.Object } metadata)
            return metadata.EnumerateObject().ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);
        return result.AdditionalProperties?.GetValueOrDefault("cost") is { ValueKind: JsonValueKind.Number } cost
            ? new() { [provider] = JsonSerializer.SerializeToElement(new { cost }) } : null;
    }
    private static ContractException MissingPrompt() => new("The latest user turn must include an image prompt.");
    private static ContractException UnsupportedPart() => new("Image turns accept text and image inputs only.", 422);
}
