using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Streaik.Core.Models;
using Streaik.OpenAI.Models;
using Streaik.Providers;
using Streaik.Vercel.Models;

namespace Streaik.Gateway;

public static partial class StreaikGateway
{
    private static async Task NativeSpeechAsync(HttpContext context)
    {
        var request = await ReadAsync<AudioSpeechRequest>(context, Web);
        var call = new GatewayCall(context, request.Model);
        request.Model = call.NativeModel;
        if (request.StreamFormat == "sse" && call.Get<IStreamingSpeechEventsProvider>() is { } events)
        {
            await foreach (var part in events.StreamSpeechAsync(request, call.Headers, call.Ct).WithCancellation(call.Ct))
            {
                if (part is SpeechStreamStartPart)
                {
                    StartMediaStream(call);
                    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                }
                else if (part is SpeechAudioPart { Audio.Length: > 0 } audio)
                    await GatewayCall.WriteFrameAsync(context.Response, JsonSerializer.Serialize(new AudioSpeechStreamDelta { Audio = audio.Audio }, Web), null, call.Ct);
                else if (part is SpeechFinishPart finish)
                {
                    var done = new AudioSpeechStreamDone();
                    if (call.ProviderId == "edgespeak" && finish.ProviderMetadata?.TryGetValue("edgespeak", out var native) == true
                        && native.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                        done.Usage = usage.Deserialize<AudioSpeechUsage>(Web);
                    await GatewayCall.WriteFrameAsync(context.Response, JsonSerializer.Serialize(done, Web), null, call.Ct);
                }
            }
            return;
        }
        if (request.StreamFormat == "sse" && call.Get<IStreamingSpeechProvider>() is { } streaming)
        {
            Need(request.Input, "input");
            using var binary = await streaming.SpeakStreamingAsync(request, call.Headers, call.Ct);
            _ = AudioType(binary);
            StartMediaStream(call);
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            await using var source = await binary.Content.ReadAsStreamAsync(call.Ct);
            var chunk = new byte[16384];
            while (await source.ReadAsync(chunk, call.Ct) is > 0 and var length)
                await GatewayCall.WriteFrameAsync(context.Response, JsonSerializer.Serialize(new AudioSpeechStreamDelta
                    { Audio = Convert.ToBase64String(chunk, 0, length) }, Web), null, call.Ct);
            await GatewayCall.WriteFrameAsync(context.Response, JsonSerializer.Serialize(new AudioSpeechStreamDone(), Web), null, call.Ct);
            return;
        }
        using var upstream = await SpeakAsync(call, request);
        var type = upstream.Content.Headers.ContentType?.MediaType;
        context.Response.ContentType = type == "text/event-stream" ? type : AudioType(upstream, allowZip: call.ProviderId == "edgespeak");
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        await upstream.Content.CopyToAsync(context.Response.Body, call.Ct);
    }

    private static async Task SpeechAsync(HttpContext context)
    {
        var request = await ReadAsync<SpeechRequest>(context, Web);
        var call = new GatewayCall(context, request.Model);
        var options = SpeechOptions(call, request.ProviderOptions);
        if (request.Language is { } language && (call.ProviderId == "deapi" || call.Get<IStreamingSpeechEventsProvider>() is not null
                || call.Get<IChatAudioRoutingProvider>()?.IsSpeechModel(call.NativeModel) == true))
            (options ??= new())["language"] = JsonSerializer.SerializeToElement(language, Web);
        using var upstream = await SpeakAsync(call, new() { Model = call.NativeModel, Input = request.Text,
            Voice = request.Voice, ResponseFormat = request.OutputFormat, Instructions = request.Instructions, Speed = request.Speed,
            AdditionalProperties = options });
        var type = AudioType(upstream, allowZip: call.ProviderId == "edgespeak");
        await using var stream = await upstream.Content.ReadAsStreamAsync(call.Ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[16384];
        while (await stream.ReadAsync(chunk, call.Ct) is > 0 and var length)
        {
            if (buffer.Length + length > MaxMediaBytes) throw new IOException("Audio output is too large.");
            buffer.Write(chunk, 0, length);
        }
        var metadata = (upstream as SpeechProviderResponse)?.ProviderMetadata;
        var format = call.ProviderId is "astica" or "unrealspeech" or "edgespeak" or "speechmatics" or "bineric" or "stabilityai" or "cambai" or "addisai" or "murfai" or "resembleai" or "runway" or "speechactors"
            ? AudioFormatName(type) : request.OutputFormat ?? AudioFormatName(type);
        if (call.ProviderId is "rime" or "neuphonic" && metadata?.TryGetValue(call.ProviderId, out var native) == true
            && native.ValueKind == JsonValueKind.Object && native.TryGetProperty("audioFormat", out var nativeFormat)
            && nativeFormat.ValueKind == JsonValueKind.String)
            format = nativeFormat.GetString() ?? format;
        await context.Response.WriteAsJsonAsync(new SpeechResponse { Audio = new() {
            Base64 = Convert.ToBase64String(buffer.GetBuffer(), 0, (int)buffer.Length), MimeType = type, Format = format },
            ProviderMetadata = GatewayPricing.Speech(call, metadata, request.Text?.Length),
            Response = new() { ModelId = call.Model, Timestamp = DateTime.UtcNow } }, Web, call.Ct);
    }

    private static async Task SpeechStreamAsync(HttpContext context)
    {
        var request = await ReadAsync<StreamingSpeechRequest>(context, Web);
        var call = new GatewayCall(context, request.Model);
        if (call.Get<IStreamingSpeechEventsProvider>() is { } events)
        {
            request.Model = call.NativeModel;
            await foreach (var part in events.StreamSpeechAsync(request, call.Headers, call.Ct).WithCancellation(call.Ct))
            {
                if (part is SpeechStreamStartPart) StartMediaStream(call);
                if (part is SpeechResponseMetadataPart metadata) metadata.ModelId = call.Model;
                await GatewayCall.WriteFrameAsync(context.Response, JsonSerializer.Serialize(part, Web), null, call.Ct);
            }
            return;
        }
        if (request.Text is not { Length: > 0 } || request.Text.Any(x => x?.Text is null || x.ProviderOptions is not null)
            || request.Text.Select(x => x.Voice ?? request.Voice).Distinct().Count() != 1 || request.OutputFormat?.Rate is not null)
            throw new ContractException("Streaming speech supports one voice and format per request, without per-part options or a custom rate.");
        var text = string.Concat(request.Text.Select(x => x.Text));
        using var upstream = await SpeakAsync(call, new() { Model = call.NativeModel, Input = text,
            Voice = request.Text[0].Voice ?? request.Voice, ResponseFormat = request.OutputFormat?.Type, Instructions = request.Instructions, Speed = request.Speed,
            AdditionalProperties = SpeechOptions(call, request.ProviderOptions) }, stream: true);
        var type = AudioType(upstream);
        StartMediaStream(call);
        await Part(new SpeechStreamStartPart());
        await Part(new SpeechResponseMetadataPart { ModelId = call.Model, Timestamp = DateTime.UtcNow });
        await using var stream = await upstream.Content.ReadAsStreamAsync(call.Ct);
        var chunk = new byte[16384];
        var format = call.ProviderId is "unrealspeech" or "bineric" or "cambai" or "addisai" or "runway" or "speechactors" ? AudioFormatName(type) : request.OutputFormat?.Type ?? AudioFormatName(type);
        while (await stream.ReadAsync(chunk, call.Ct) is > 0 and var length)
            await Part(new SpeechAudioPart { Audio = Convert.ToBase64String(chunk, 0, length), AudioFormat = new() { Type = format } });
        await Part(new SpeechFinishPart { ProviderMetadata = GatewayPricing.Speech(call, (upstream as SpeechProviderResponse)?.ProviderMetadata, text.Length) });

        Task Part(StreamingSpeechPart part) => GatewayCall.WriteFrameAsync(context.Response, JsonSerializer.Serialize(part, Web), null, call.Ct);
    }

    private static Dictionary<string, JsonElement>? SpeechOptions(GatewayCall call, Dictionary<string, JsonElement>? options)
        => call.ProviderId is "async" or "edgespeak" or "speechmatics" or "rime" || call.Get<IStreamingSpeechProvider>() is not null
            ? SelectedOptions(call, options, "model", "input", "response_format", "stream_format", "stream")
            : SelectedOptions(call, options, "model", "input", "voice", "response_format", "stream_format");

    private static Task<HttpResponseMessage> SpeakAsync(GatewayCall call, AudioSpeechRequest request, bool stream = false)
    {
        if (call.ProviderId != "edgespeak") Need(request.Input, "input"); // EdgeSpeak can use native segments instead.
        if (stream && call.Get<IStreamingSpeechProvider>() is { } streaming)
            return streaming.SpeakStreamingAsync(request, call.Headers, call.Ct);
        return call.Require<ISpeechProvider>().SpeakAsync(request, call.Headers, call.Ct);
    }

    private static string AudioType(HttpResponseMessage response, bool allowZip = false)
    {
        var type = response.Content.Headers.ContentType?.MediaType;
        return type?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true || type == "application/octet-stream" || allowZip && type == "application/zip"
            ? type : throw new IOException("The upstream did not return audio.");
    }

    private static string AudioFormatName(string type) => type switch
    { "audio/mpeg" => "mp3", "audio/x-wav" => "wav", "audio/ogg" => "opus", "audio/basic" => "mulaw", "application/octet-stream" => "pcm", _ => type.Split('/')[1] };

    private static async Task NativeTranscriptionAsync(HttpContext context)
    {
        var form = await ReadMediaFormAsync(context);
        var call = new GatewayCall(context, form["model"]);
        var file = form.Files.GetFile("file") ?? throw new ContractException("'file' is required.");
        if (file.Length == 0 || form.Files.Count != 1) throw new ContractException("Expected one nonempty audio file.");
        if (call.Get<INativeAudioTranscriptionProvider>() is { } native)
        {
            using var upstream = await native.TranscribeNativeAsync(call.NativeModel, form, call.Headers, call.Ct);
            var type = upstream.Content.Headers.ContentType?.MediaType;
            context.Response.ContentType = type is "application/json" or "text/event-stream" or "text/plain" or "text/vtt"
                ? type : "application/octet-stream";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            if (type == "text/event-stream") context.Response.Headers.CacheControl = "no-cache";
            await upstream.Content.CopyToAsync(context.Response.Body, call.Ct);
            return;
        }
        var format = form["response_format"].ToString();
        if (format is not ("" or "json" or "text" or "verbose_json")) throw new ContractException("Unsupported transcription response format.");
        var stream = form["stream"] == "true";
        if (stream && call.ProviderId is not ("bineric" or "cambai" or "addisai" or "resembleai" or "soniox" or "aether")) throw new ContractException("Use the streaming transcription route.");
        if (stream && format is not ("" or "json")) throw new ContractException("Streaming transcription requires JSON response format.");
        var options = form.Where(k => k.Key is not ("model" or "file" or "language" or "response_format" or "stream"))
            .ToDictionary(k => k.Key, k => JsonSerializer.SerializeToElement(k.Value.ToString()));
        if (call.ProviderId is "soniox" or "aether")
        {
            // Multipart uses text fields. Decode the structured native options without changing other providers' handling.
            foreach (var key in options.Keys.ToArray())
            {
                if (key != "providerOptions" && key != call.ProviderId && (call.ProviderId != "soniox"
                    || key is not ("context" or "language_hints" or "language_hints_strict" or "enable_speaker_diarization" or "enable_language_identification"))) continue;
                try { options[key] = JsonSerializer.Deserialize<JsonElement>(options[key].GetString()!); }
                catch (JsonException) { throw new ContractException($"'{key}' must be valid JSON."); }
            }
        }
        await using var audio = file.OpenReadStream();
        var result = await call.Require<ITranscriptionProvider>().TranscribeAsync(call.NativeModel, audio, file.ContentType, file.FileName,
            form["language"], options, call.Headers, call.Ct);
        if (stream)
        {
            StartMediaStream(call);
            if (!string.IsNullOrWhiteSpace(result.Text)) await GatewayCall.WriteFrameAsync(context.Response,
                JsonSerializer.Serialize(new OpenAITranscriptionTextDelta { Delta = result.Text }, Web), null, call.Ct);
            await GatewayCall.WriteFrameAsync(context.Response, JsonSerializer.Serialize(new OpenAITranscriptionTextDone { Text = result.Text }, Web), null, call.Ct);
        }
        else if (format == "text") { context.Response.ContentType = "text/plain; charset=utf-8"; await context.Response.WriteAsync(result.Text, call.Ct); }
        else if (format == "verbose_json") await context.Response.WriteAsJsonAsync(new { text = result.Text, duration = result.DurationInSeconds,
            language = result.Language, segments = result.Segments.Select((x, i) => new { id = i, start = x.StartSecond, end = x.EndSecond, text = x.Text }) }, Web, call.Ct);
        else await context.Response.WriteAsJsonAsync(new { text = result.Text }, Web, call.Ct);
    }

    private static async Task TranscriptionAsync(HttpContext context)
    {
        var request = await ReadAsync<TranscriptionRequest>(context, Web);
        var call = new GatewayCall(context, request.Model);
        var options = SelectedOptions(call, request.ProviderOptions, "model", "file");
        if (request.Audio is not JsonElement { ValueKind: JsonValueKind.String } input) throw new ContractException("'audio' must be a base64 string.");
        var value = input.GetString();
        var useSourceUrl = call.ProviderId == "deapi" && (options?.ContainsKey("source_url") == true
            || Uri.TryCreate(value, UriKind.Absolute, out var url) && url.Scheme is "https" or "http");
        if (useSourceUrl) (options ??= new()).TryAdd("source_url", input);
        var (bytes, mediaType) = useSourceUrl ? (System.Array.Empty<byte>(), "audio/wav")
            : ReadAudio(value, request.MediaType, allowVideo: call.ProviderId == "deapi");
        using var audio = new MemoryStream(bytes, writable: false);
        var language = options?.TryGetValue("language", out var lang) == true && lang.ValueKind == JsonValueKind.String ? lang.GetString() : null;
        var result = await call.Require<ITranscriptionProvider>().TranscribeAsync(call.NativeModel, audio, mediaType,
            $"audio.{(mediaType == "audio/mpeg" ? "mp3" : mediaType.Split('/').Last())}", language, options, call.Headers, call.Ct);
        result.Response ??= new();
        result.Response.ModelId = call.Model;
        result.Response.Timestamp = DateTime.UtcNow;
        result.ProviderMetadata = GatewayPricing.Transcription(call, result.ProviderMetadata);
        await context.Response.WriteAsJsonAsync(result, Web, call.Ct);
    }

    private static async Task TranscriptionStreamAsync(HttpContext context)
    {
        var request = await ReadAsync<StreamingTranscriptionRequest>(context, Web);
        var call = new GatewayCall(context, request.Model);
        Need(request.Audio, "audio");
        Need(request.InputAudioFormat?.Type, "inputAudioFormat.type");
        if (request.Audio.Length > MaxMediaBytes * 4 / 3 + 8) throw new ContractException("Audio is too large.", 413);
        var provider = call.Require<IStreamingTranscriptionProvider>();
        request.Model = call.NativeModel;
        StartMediaStream(call);
        await foreach (var part in provider.StreamTranscriptionAsync(request, call.Headers, call.Ct).WithCancellation(call.Ct))
        {
            if (part is TranscriptionResponseMetadataPart metadata) metadata.ModelId = call.Model;
            if (part is TranscriptionFinishPart finish)
                finish.ProviderMetadata = GatewayPricing.Transcription(call, finish.ProviderMetadata);
            await GatewayCall.WriteFrameAsync(context.Response, JsonSerializer.Serialize(part, Web), null, call.Ct);
        }
    }

    private static (byte[] Bytes, string MediaType) ReadAudio(string? input, string? mediaType, bool allowVideo = false)
    {
        Need(input, "audio");
        var encoded = input!;
        if (encoded.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = encoded.IndexOf(',');
            if (comma < 12 || !encoded[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase)) throw new ContractException("Invalid audio data URL.");
            mediaType = encoded[5..(comma - 7)];
            encoded = encoded[(comma + 1)..];
        }
        if (encoded.Length > MaxMediaBytes * 4 / 3 + 8) throw new ContractException("Audio is too large.", 413);
        if (mediaType is not { Length: > 6 } || !(mediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
                || allowVideo && mediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
            || !mediaType.Skip(6).All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or '+'))
            throw new ContractException(allowVideo ? "An audio or video MIME type is required." : "An audio MIME type is required.");
        try
        {
            var bytes = Convert.FromBase64String(encoded);
            if (bytes.Length == 0) throw new ContractException("'audio' must not be empty.");
            if (bytes.Length > MaxMediaBytes) throw new ContractException("Audio is too large.", 413);
            return (bytes, mediaType);
        }
        catch (FormatException) { throw new ContractException("Invalid base64 audio."); }
    }
}
