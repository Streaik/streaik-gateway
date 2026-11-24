using System.Text.Json;
using Streaik.ChatCompletions.Models;
using Streaik.Messages;
using Streaik.OpenAI.Models;
using Streaik.Providers;
using Streaik.Responses;
using Streaik.Responses.Output;
using Streaik.Responses.Streaming;
using Streaik.Vercel.Models;

namespace Streaik.Gateway;

public static partial class StreaikGateway
{
    private static async Task<bool> TryChatAudioAsync(GatewayCall call, ChatCompletionOptions request)
    {
        if (call.Get<IChatAudioRoutingProvider>() is not { } route) return false;
        var speech = route.IsSpeechModel(call.NativeModel);
        if (!speech && !route.IsTranscriptionModel(call.NativeModel)) return false;
        if (request.Tools?.Any() == true || request.ResponseFormat is not null
            || request.AdditionalProperties?.TryGetValue("n", out var choices) == true
                && (choices.ValueKind != JsonValueKind.Number || !choices.TryGetInt32(out var count) || count != 1))
            throw new ContractException("Audio chat turns do not support tools, structured output, or multiple choices.", 422);
        var user = request.Messages.LastOrDefault(message => message.Role == "user")
            ?? throw new ContractException("An audio chat turn requires a user message.");
        var id = $"chatcmpl_{Guid.NewGuid():N}";
        var audioId = $"audio_{Guid.NewGuid():N}";
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (speech)
        {
            var parts = user.Content.Parts;
            if (parts?.Any(part => part is not ChatMessageTextPart) == true)
                throw new ContractException("Speech turns require text input only.", 422);
            var format = request.Audio?.Format;
            var speechRequest = new AudioSpeechRequest { Model = call.NativeModel,
                Input = user.Content.Text ?? string.Concat((parts ?? []).OfType<ChatMessageTextPart>().Select(part => part.Text)),
                ResponseFormat = format == "pcm16" && !(call.ProviderId == "audixa" && request.Stream == true) ? "pcm" : format,
                AdditionalProperties = request.Audio?.AdditionalProperties };
            if (request.Stream == true && call.Get<IStreamingSpeechEventsProvider>() is { } events && format is (null or "pcm16" or "pcm" or "raw"))
                await call.WriteChatStreamAsync(SpeechStream(events, speechRequest));
            else
            {
                var (encoded, mediaType, providerMetadata) = await ReadInlineSpeechAsync(call, speechRequest);
                var metadata = new Dictionary<string, JsonElement> { ["mime_type"] = JsonSerializer.SerializeToElement(mediaType) };
                if (request.Stream == true)
                    await call.WriteChatStreamAsync(BufferedStream(new() { Role = "assistant", Audio = new() { Id = audioId, Data = encoded,
                        Transcript = speechRequest.Input, AdditionalProperties = metadata } }, providerMetadata));
                else await Write(new() { Audio = new() { Id = audioId, Data = encoded, Transcript = speechRequest.Input,
                    ExpiresAt = created, AdditionalProperties = metadata } }, providerMetadata);
            }
        }
        else
        {
            var parts = user.Content.Parts;
            if (parts?.OfType<ChatMessageInputAudioPart>().ToArray() is not [{ InputAudio: { } audio }]
                || parts.Any(part => part is not (ChatMessageInputAudioPart or ChatMessageTextPart)))
                throw new ContractException("Transcription turns require one input_audio part.", 422);
            var inputType = audio.Format switch { "wav" => "audio/wav", "mp3" => "audio/mpeg", _ => throw new ContractException("Use wav or mp3 input audio.", 422) };
            var (bytes, mediaType) = ReadAudio(audio.Data, inputType);
            using var input = new MemoryStream(bytes, writable: false);
            var result = await call.Require<ITranscriptionProvider>().TranscribeAsync(call.NativeModel, input, mediaType, $"audio.{audio.Format}",
                null, audio.AdditionalProperties, call.Headers, call.Ct);
            if (request.Stream == true) await call.WriteChatStreamAsync(BufferedStream(new() { Role = "assistant", Content = result.Text }, result.ProviderMetadata));
            else await Write(new() { Content = result.Text }, result.ProviderMetadata);
        }
        return true;

        Task Write(ChatCompletionResponseMessage message, Dictionary<string, JsonElement>? metadata) => call.WriteAsync(new ChatCompletion
        {
            Id = id, Created = created, MetadataExtensions = metadata,
            Choices = [new ChatCompletionChoice { FinishReason = "stop", Message = message }]
        });

        ChatCompletionUpdate Chunk(ChatCompletionDelta delta, bool done = false, Dictionary<string, JsonElement>? metadata = null) => new()
        {
            Id = id, Created = created, Choices = [new() { Delta = delta, FinishReason = done ? "stop" : null }],
            AdditionalProperties = metadata is null ? null : new() { ["metadata"] = JsonSerializer.SerializeToElement(metadata) }
        };

        async IAsyncEnumerable<ChatCompletionUpdate> BufferedStream(ChatCompletionDelta delta, Dictionary<string, JsonElement>? metadata)
        {
            await Task.CompletedTask;
            yield return Chunk(delta);
            yield return Chunk(new(), done: true, metadata);
        }

        async IAsyncEnumerable<ChatCompletionUpdate> SpeechStream(IStreamingSpeechEventsProvider provider, AudioSpeechRequest audio)
        {
            audio.StreamFormat = "sse";
            await foreach (var part in provider.StreamSpeechAsync(audio, call.Headers, call.Ct).WithCancellation(call.Ct))
            {
                switch (part)
                {
                    case SpeechStreamStartPart:
                        yield return Chunk(new() { Role = "assistant", Audio = new() { Id = audioId, Transcript = audio.Input } });
                        break;
                    case SpeechAudioPart { Audio.Length: > 0 } data:
                        yield return Chunk(new() { Audio = new() { Data = data.Audio } }, metadata: data.ProviderMetadata);
                        break;
                    case SpeechAudioPart { ProviderMetadata: { } metadata }:
                        yield return Chunk(new(), metadata: metadata);
                        break;
                    case SpeechFinishPart finish:
                        yield return Chunk(new(), done: true, finish.ProviderMetadata);
                        break;
                }
            }
        }
    }

    private static async Task<bool> TryVercelAudioChatAsync(GatewayCall call, ChatRequest request)
    {
        if (call.Get<IChatAudioRoutingProvider>() is not { } route) return false;
        var speech = route.IsSpeechModel(call.NativeModel);
        if (!speech && !route.IsTranscriptionModel(call.NativeModel)) return false;
        if (request.Tools?.Any() == true || request.ResponseFormat is not null)
            throw new ContractException("Audio chat turns do not support tools or structured output.", 422);
        var user = request.Messages.LastOrDefault(message => message.Role == Role.user)
            ?? throw new ContractException("An audio chat turn requires a user message.");
        if (speech)
        {
            if (user.Parts.Any(part => part is not TextUIPart)) throw new ContractException("Speech turns require text input only.", 422);
            await call.WriteVercelStreamAsync(SpeechParts(string.Concat(user.Parts.OfType<TextUIPart>().Select(part => part.Text))));
        }
        else
        {
            if (user.Parts.OfType<FileUIPart>().ToArray() is not [{ } file] || file.Url?.StartsWith("data:audio/", StringComparison.OrdinalIgnoreCase) != true
                || user.Parts.Any(part => part is not (FileUIPart or TextUIPart)))
                throw new ContractException("Transcription turns require one inline audio file.", 422);
            var options = SelectedOptions(call, request.ProviderMetadata, "model", "file");
            var (bytes, mediaType) = ReadAudio(file.Url, file.MediaType);
            using var input = new MemoryStream(bytes, writable: false);
            var result = await call.Require<ITranscriptionProvider>().TranscribeAsync(call.NativeModel, input, mediaType, "audio",
                null, options, call.Headers, call.Ct);
            await call.WriteVercelStreamAsync(TranscriptParts(result));
        }
        return true;

        async IAsyncEnumerable<UIMessagePart> SpeechParts(string input)
        {
            var (audio, mediaType, metadata) = await ReadInlineSpeechAsync(call, new() { Model = call.NativeModel, Input = input,
                AdditionalProperties = SpeechOptions(call, request.ProviderMetadata) });
            yield return new StartStepUIPart();
            yield return new FileUIPart { MediaType = mediaType, Url = $"data:{mediaType};base64,{audio}" };
            yield return Finish(metadata);
        }

        async IAsyncEnumerable<UIMessagePart> TranscriptParts(TranscriptionResponse result)
        {
            await Task.CompletedTask;
            const string id = "transcript";
            yield return new StartStepUIPart();
            yield return new TextStartUIMessageStreamPart { Id = id };
            yield return new TextDeltaUIMessageStreamPart { Id = id, Delta = result.Text };
            yield return new TextEndUIMessageStreamPart { Id = id };
            yield return Finish(result.ProviderMetadata);
        }

        FinishUIPart Finish(Dictionary<string, JsonElement>? metadata) => new() { FinishReason = "stop", MessageMetadata = new()
            { Model = call.Model, Timestamp = DateTimeOffset.UtcNow, Usage = new(), ProviderMetadata = metadata } };
    }

    private static bool UseSpeechFileAdapter(GatewayCall call) => call.ProviderId is "typecast" or "async" or "audixa"
        && call.Get<IChatAudioRoutingProvider>()?.IsSpeechModel(call.NativeModel) == true;

    private static async Task SpeechViaResponsesAsync(GatewayCall call, ResponseRequest request)
    {
        if (request.Tools?.Any() == true || request.Reasoning is not null || request.Text is not null)
            throw new ContractException("Speech Responses require text input without tools, reasoning, or structured output.", 422);
        var content = request.Input?.Items?.OfType<ResponseInputMessage>().LastOrDefault(x => x.Role == ResponseRole.User)?.Content;
        if (content?.Parts?.Any(part => part is not InputTextPart) == true)
            throw new ContractException("Speech turns require text input only.", 422);
        var text = request.Input?.Text ?? content?.Text ?? string.Concat((content?.Parts ?? []).OfType<InputTextPart>().Select(x => x.Text));
        var (audio, mime, metadata) = await ReadInlineSpeechAsync(call, new() { Model = call.NativeModel, Input = text,
            AdditionalProperties = request.AdditionalProperties });
        var id = $"resp_{Guid.NewGuid():N}";
        var itemId = $"msg_{Guid.NewGuid():N}";
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var file = new InputFilePart { Filename = SpeechFileName(mime), FileData = $"data:{mime};base64,{audio}",
            AdditionalProperties = new() { ["media_type"] = JsonSerializer.SerializeToElement(mime) } }.ToOutputContent();
        ResponseOutputMessage Item(bool done) => new() { Id = itemId, Role = ResponseRole.Assistant, Status = done ? "completed" : "in_progress",
            Content = done ? [file] : [] };
        ResponseResult Result(bool done) => new() { Id = id, CreatedAt = created, Status = done ? "completed" : "in_progress",
            Output = done ? [Item(true)] : [], Metadata = done ? metadata?.ToDictionary(x => x.Key, x => (object?)x.Value) : null };
        if (request.Stream == true) await call.WriteResponsesStreamAsync(Parts());
        else await call.WriteAsync(Result(true));

        async IAsyncEnumerable<ResponseStreamPart> Parts()
        {
            await Task.CompletedTask;
            yield return new ResponseCreated { SequenceNumber = 0, Response = Result(false) };
            yield return new ResponseOutputItemAdded { SequenceNumber = 1, OutputIndex = 0, Item = StreamItem(false) };
            yield return new ResponseOutputItemDone { SequenceNumber = 2, OutputIndex = 0, Item = StreamItem(true) };
            yield return new ResponseCompleted { SequenceNumber = 3, Response = Result(true) };
        }

        ResponseStreamItem StreamItem(bool done) => JsonSerializer.SerializeToElement(Item(done), ResponseJson.Default)
            .Deserialize<ResponseStreamItem>(ResponseJson.Default)!;
    }

    private static async Task SpeechViaMessagesAsync(GatewayCall call, MessagesRequest request)
    {
        if (request.Tools?.Count > 0 || request.Thinking is not null || request.OutputConfig is not null)
            throw new ContractException("Speech Messages require text input without tools, thinking, or structured output.", 422);
        var content = (request.Messages?.LastOrDefault(x => x.Role == "user")
            ?? throw new ContractException("A speech turn requires a user message.")).Content;
        if (content.IsRaw || content.Blocks?.Any(part => part.Type != "text") == true)
            throw new ContractException("Speech turns require text input only.", 422);
        var text = content.Text ?? string.Concat((content.Blocks ?? []).Select(x => x.Text));
        var (audio, mime, metadata) = await ReadInlineSpeechAsync(call, new() { Model = call.NativeModel, Input = text,
            AdditionalProperties = request.AdditionalProperties });
        var id = $"msg_{Guid.NewGuid():N}";
        // Match the legacy attachment representation rather than introducing a Messages audio block type.
        var file = new MessageContentBlock { Type = "document", Title = SpeechFileName(mime),
            Source = new() { Type = "text", Data = $"data:{mime};base64,{audio}", MediaType = mime } };
        MessagesResponse Result(bool done) => new() { Id = id, Type = "message", Role = "assistant",
            Content = done ? [file] : [], StopReason = done ? "end_turn" : null, Metadata = done ? metadata : null };
        if (request.Stream == true) await call.WriteMessagesStreamAsync(Parts());
        else await call.WriteAsync(Result(true));

        async IAsyncEnumerable<MessageStreamPart> Parts()
        {
            await Task.CompletedTask;
            yield return new() { Type = "message_start", Message = Result(false) };
            yield return new() { Type = "content_block_start", Index = 0, ContentBlock = file };
            yield return new() { Type = "content_block_stop", Index = 0 };
            yield return new() { Type = "message_delta", Delta = new() { StopReason = "end_turn" } };
            yield return new() { Type = "message_stop", Metadata = metadata };
        }
    }

    private static string SpeechFileName(string mime) => $"speech.{(mime is "audio/mpeg" or "audio/mp3" ? "mp3" : "wav")}";

    private static async Task<(string Base64, string MediaType, Dictionary<string, JsonElement>? Metadata)> ReadInlineSpeechAsync(GatewayCall call, AudioSpeechRequest request)
    {
        using var upstream = await SpeakAsync(call, request);
        var mediaType = AudioType(upstream);
        await using var source = await upstream.Content.ReadAsStreamAsync(call.Ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[16384];
        while (await source.ReadAsync(chunk, call.Ct) is > 0 and var size)
        {
            if (buffer.Length + size > MaxMediaBytes) throw new IOException("Audio output is too large.");
            buffer.Write(chunk, 0, size);
        }
        if (buffer.Length == 0) throw new IOException("The upstream returned no audio.");
        return (Convert.ToBase64String(buffer.GetBuffer(), 0, (int)buffer.Length), mediaType, (upstream as SpeechProviderResponse)?.ProviderMetadata);
    }
}
