using System.Runtime.CompilerServices;
using System.Text.Json;
using Streaik.ChatCompletions.Models;
using Streaik.Providers;
using Streaik.Responses;
using Streaik.Responses.Output;
using Streaik.Responses.Streaming;
using Streaik.Vercel.Models;

namespace Streaik.Gateway;

public static partial class StreaikGateway
{
    private const string VideoTool = "generate_video";

    private static IVideoProvider? AstriaVideo(GatewayCall call) => call.ProviderId == "astria" ? call.Get<IVideoProvider>() : null;

    private static Task VideoViaVercelAsync(GatewayCall call, ChatRequest request, IVideoProvider provider)
    {
        if (request.Tools?.Any() == true || request.ResponseFormat is not null)
            throw new ContractException("Video turns do not support tools or structured output.", 422);
        var parts = request.Messages.LastOrDefault(message => message.Role == Role.user)?.Parts ?? [];
        var video = BuildVideo(call, request.ProviderMetadata, null, parts.Select(part => part switch
        {
            TextUIPart text => ((string?)text.Text, (VideoFile?)null),
            FileUIPart file => (null, VideoInput(file.Url, file.MediaType)),
            _ => throw UnsupportedVideoPart()
        }));
        return call.WriteVercelStreamAsync(Parts());

        async IAsyncEnumerable<UIMessagePart> Parts()
        {
            var id = $"call_{Guid.NewGuid():N}";
            var started = await provider.StartVideoAsync(video, call.Headers, call.Ct);
            var operation = ProtectVideo(call, started.Operation);
            yield return new StartStepUIPart();
            yield return new ToolCallPart { ToolCallId = id, ToolName = VideoTool, Input = VideoToolInput(call, video),
                ProviderExecuted = true, Title = "Generate video", ToolMetadata = started.ProviderMetadata };
            yield return new ToolOutputAvailablePart { ToolCallId = id, ProviderExecuted = true, Preliminary = true,
                Output = VideoToolResult("submitted", operation, warnings: started.Warnings), ToolMetadata = started.ProviderMetadata };
            await foreach (var status in PollVideoAsync(call, provider, started.Operation, call.Ct))
            {
                if (status is VideoOperationErrorResult error)
                    yield return new ToolOutputErrorPart { ToolCallId = id, ProviderExecuted = true, ErrorText = error.Error, ToolMetadata = status.ProviderMetadata };
                else yield return new ToolOutputAvailablePart { ToolCallId = id, ProviderExecuted = true,
                    Preliminary = status is VideoOperationPendingResult, Output = VideoToolResult(status.Status, operation, status), ToolMetadata = status.ProviderMetadata };
                if (status is not VideoOperationPendingResult)
                    yield return new FinishUIPart { FinishReason = status is VideoOperationErrorResult ? "error" : "tool-calls",
                        MessageMetadata = new() { Model = call.Model, Timestamp = DateTimeOffset.UtcNow, Usage = new(), ProviderMetadata = status.ProviderMetadata } };
            }
        }
    }

    private static Task VideoViaChatAsync(GatewayCall call, ChatCompletionOptions request, IVideoProvider provider)
    {
        if (request.Stream != true) throw new ContractException("Astria video Chat Completions require streaming. Alternatively, use the video endpoint.", 422);
        if (request.Tools?.Any() == true || request.Functions?.Any() == true || request.ResponseFormat is not null || request.NumberOfChoices is > 1)
            throw new ContractException("Video turns do not support tools, structured output, or multiple choices.", 422);
        var content = (request.Messages.LastOrDefault(message => message.Role == "user") ?? throw UnsupportedVideoPart()).Content;
        var video = BuildVideo(call, request.AdditionalProperties, content.Text, (content.Parts ?? []).Select(part => part switch
        {
            ChatMessageTextPart text => ((string?)text.Text, (VideoFile?)null),
            ChatMessageImagePart image => (null, VideoInput(image.ImageUrl.Url, "image/png")),
            _ => throw UnsupportedVideoPart()
        }));
        return call.WriteChatStreamAsync(Parts());

        async IAsyncEnumerable<ChatCompletionUpdate> Parts()
        {
            var id = $"chatcmpl_{Guid.NewGuid():N}";
            var tool = $"call_{Guid.NewGuid():N}";
            var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var started = await provider.StartVideoAsync(video, call.Headers, call.Ct);
            var operation = ProtectVideo(call, started.Operation);
            yield return Chunk(new() { Name = VideoTool, Arguments = JsonSerializer.Serialize(VideoToolInput(call, video), Web) }, started.ProviderMetadata);
            yield return Chunk(Output(VideoToolResult("submitted", operation, warnings: started.Warnings), true), started.ProviderMetadata);
            await foreach (var status in PollVideoAsync(call, provider, started.Operation, call.Ct))
            {
                var failed = status is VideoOperationErrorResult;
                yield return Chunk(Output(VideoToolResult(status.Status, operation, status), status is VideoOperationPendingResult), status.ProviderMetadata);
                if (status is not VideoOperationPendingResult) yield return Chunk(null, status.ProviderMetadata, failed ? "error" : "tool_calls");
            }

            ChatCompletionUpdate Chunk(ChatCompletionFunctionCallDelta? function, Dictionary<string, JsonElement>? metadata, string? finish = null)
                => new() { Id = id, Created = created, AdditionalProperties = metadata is null ? null : new() { ["metadata"] = JsonSerializer.SerializeToElement(metadata, Web) },
                    Choices = [new() { FinishReason = finish, Delta = function is null ? new() : new() { ToolCalls = [new ChatCompletionToolCallChunk
                    { Id = tool, Type = "function", Function = function, AdditionalProperties = new() { ["provider_executed"] = JsonSerializer.SerializeToElement(true) } }] } }] };

            static ChatCompletionFunctionCallDelta Output(object result, bool preliminary) => new() { Name = VideoTool,
                AdditionalProperties = new() { ["output"] = JsonSerializer.SerializeToElement(result, Web), ["preliminary"] = JsonSerializer.SerializeToElement(preliminary) } };
        }
    }

    private static Task VideoViaResponsesAsync(GatewayCall call, ResponseRequest request, IVideoProvider provider)
    {
        if (request.Stream != true) throw new ContractException("Astria video Responses require streaming. Alternatively, use the video endpoint.", 422);
        if (request.Tools?.Count > 0 || request.Reasoning is not null || request.Text is not null)
            throw new ContractException("Video turns do not support tools, reasoning, or structured output.", 422);
        var content = request.Input?.Items?.OfType<ResponseInputMessage>().LastOrDefault(message => message.Role == ResponseRole.User)?.Content;
        var video = BuildVideo(call, request.AdditionalProperties, request.Input?.Text ?? content?.Text, (content?.Parts ?? []).Select(part => part switch
        {
            InputTextPart text => ((string?)text.Text, (VideoFile?)null),
            InputImagePart { FileId: null } image => (null, VideoInput(image.ImageUrl, "image/png")),
            InputFilePart { FileId: null } file => (null, VideoInput(file.FileUrl ?? file.FileData)),
            _ => throw UnsupportedVideoPart()
        }));
        return call.WriteResponsesStreamAsync(Parts());

        async IAsyncEnumerable<ResponseStreamPart> Parts()
        {
            var id = $"resp_{Guid.NewGuid():N}";
            var itemId = $"ctc_{Guid.NewGuid():N}";
            var outputId = $"ctco_{Guid.NewGuid():N}";
            var tool = $"call_{Guid.NewGuid():N}";
            var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var sequence = 0;
            var started = await provider.StartVideoAsync(video, call.Headers, call.Ct);
            var operation = ProtectVideo(call, started.Operation);
            var toolCall = new ResponseOutputItem<ResponseCustomToolCallItem>(new() { Id = itemId, CallId = tool, Name = VideoTool,
                Input = JsonSerializer.Serialize(VideoToolInput(call, video), Web), AdditionalProperties = new() { ["provider_executed"] = JsonSerializer.SerializeToElement(true) } });
            yield return new ResponseCreated { SequenceNumber = sequence++, Response = new() { Id = id, CreatedAt = created, Status = "in_progress", Output = [] } };
            yield return new ResponseOutputItemAdded { SequenceNumber = sequence++, OutputIndex = 0, Item = StreamItem(toolCall) };
            yield return new ResponseOutputItemDone { SequenceNumber = sequence++, OutputIndex = 0, Item = StreamItem(toolCall) };
            await foreach (var status in PollVideoAsync(call, provider, started.Operation, call.Ct))
            {
                if (status is VideoOperationPendingResult) continue;
                var failed = status is VideoOperationErrorResult;
                var output = new ResponseOutputCustomToolCallResult { Id = outputId, CallId = tool, Status = failed ? "incomplete" : "completed",
                    Output = JsonSerializer.Serialize(VideoToolResult(status.Status, operation, status), Web) };
                yield return new ResponseOutputItemDone { SequenceNumber = sequence++, OutputIndex = 1, Item = StreamItem(output) };
                var response = new ResponseResult { Id = id, CreatedAt = created, Status = failed ? "incomplete" : "completed", Output = [toolCall, output],
                    Metadata = status.ProviderMetadata?.ToDictionary(pair => pair.Key, pair => (object?)pair.Value) };
                if (failed) yield return new ResponseIncompleteEvent { SequenceNumber = sequence, Response = response };
                else yield return new ResponseCompleted { SequenceNumber = sequence, Response = response };
            }
        }
    }

    private static ResponseStreamItem StreamItem(ResponseOutputItem item)
        => JsonSerializer.SerializeToElement(item, ResponseJson.Default).Deserialize<ResponseStreamItem>(ResponseJson.Default)!;

    private static VideoRequest BuildVideo(GatewayCall call, Dictionary<string, JsonElement>? options, string? text, IEnumerable<(string? Text, VideoFile? File)> parts)
    {
        var input = parts.ToArray();
        var prompt = string.Join('\n', input.Select(item => item.Text).Prepend(text).Where(value => !string.IsNullOrWhiteSpace(value)));
        Need(prompt, "prompt");
        var files = input.Select(item => item.File).OfType<VideoFile>().ToArray();
        var image = files.FirstOrDefault(file => file.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true);
        return new() { Model = call.NativeModel, Prompt = prompt, Image = image, InputReferences = files.Where(file => file != image), ProviderOptions = options };
    }

    private static VideoFile VideoInput(string? data, string? mime = null)
    {
        if (data is null) throw UnsupportedVideoPart();
        if (data.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && data.IndexOf(';') is > 5 and var separator)
            mime = data[5..separator];
        if (mime?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true && mime?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) != true
            && mime?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) != true) throw UnsupportedVideoPart();
        return new() { Data = data, MediaType = mime };
    }

    private static async IAsyncEnumerable<VideoOperationStatusResult> PollVideoAsync(GatewayCall call, IVideoProvider provider, string operation,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        while (true)
        {
            var status = await provider.GetVideoAsync(operation, call.Headers, timeout.Token);
            yield return status;
            if (status is not VideoOperationPendingResult) yield break;
            await Task.Delay(TimeSpan.FromSeconds(2), timeout.Token);
        }
    }

    private static string ProtectVideo(GatewayCall call, string operation)
        => $"{call.ProviderId}/{new GatewayReferences(call, "video").Protect(new VideoReference(operation, call.Model))}";

    private static object VideoToolInput(GatewayCall call, VideoRequest video) => new { model = call.Model, prompt = video.Prompt };

    private static object VideoToolResult(string status, string operation, VideoOperationStatusResult? result = null, IEnumerable<object>? warnings = null)
    {
        if (result is VideoOperationCompletedResult completed)
        {
            var videos = completed.Videos.ToArray();
            return new { isError = false, content = videos.Select((video, index) => new { type = "resource",
                resource = new { uri = $"video://generated/{Uri.EscapeDataString(operation)}/{index}", mimeType = video.MediaType, blob = video.Data } }),
                structuredContent = new { status, operation, videos = videos.Select((video, index) => new { index, type = "embedded-resource", mediaType = video.MediaType }) } };
        }
        return result is VideoOperationErrorResult error
            ? new { isError = true, content = new[] { new { type = "text", text = error.Error } }, structuredContent = new { status, operation, error = error.Error } }
            : new { isError = false, content = Array.Empty<object>(), structuredContent = new { status, operation, warnings = warnings ?? (result as VideoOperationPendingResult)?.Warnings ?? [] } };
    }

    private static ContractException UnsupportedVideoPart() => new("Video turns accept text and inline or URL media references.", 422);
}
