using System.Text.Json;
using Microsoft.AspNetCore.Http;
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
        using var upstream = await SpeakAsync(call, request);
        var type = upstream.Content.Headers.ContentType?.MediaType;
        context.Response.ContentType = type == "text/event-stream" ? type : AudioType(upstream);
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        await upstream.Content.CopyToAsync(context.Response.Body, call.Ct);
    }

    private static async Task SpeechAsync(HttpContext context)
    {
        var request = await ReadAsync<SpeechRequest>(context, Web);
        var call = new GatewayCall(context, request.Model);
        using var upstream = await SpeakAsync(call, new() { Model = call.NativeModel, Input = request.Text,
            Voice = request.Voice, ResponseFormat = request.OutputFormat, Instructions = request.Instructions, Speed = request.Speed,
            AdditionalProperties = SelectedOptions(call, request.ProviderOptions, "model", "input", "voice", "response_format", "stream_format") });
        var type = AudioType(upstream);
        await using var stream = await upstream.Content.ReadAsStreamAsync(call.Ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[16384];
        while (await stream.ReadAsync(chunk, call.Ct) is > 0 and var length)
        {
            if (buffer.Length + length > MaxMediaBytes) throw new IOException("Audio output is too large.");
            buffer.Write(chunk, 0, length);
        }
        await context.Response.WriteAsJsonAsync(new SpeechResponse { Audio = new() {
            Base64 = Convert.ToBase64String(buffer.GetBuffer(), 0, (int)buffer.Length), MimeType = type,
            Format = request.OutputFormat ?? AudioFormatName(type) }, Response = new() { ModelId = call.Model, Timestamp = DateTime.UtcNow } }, Web, call.Ct);
    }

    private static async Task SpeechStreamAsync(HttpContext context)
    {
        var request = await ReadAsync<StreamingSpeechRequest>(context, Web);
        var call = new GatewayCall(context, request.Model);
        if (request.Text is not { Length: > 0 } || request.Text.Any(x => x?.Text is null || x.ProviderOptions is not null)
            || request.Text.Select(x => x.Voice ?? request.Voice).Distinct().Count() != 1 || request.OutputFormat?.Rate is not null)
            throw new ContractException("Streaming speech supports one voice and format per request, without per-part options or a custom rate.");
        using var upstream = await SpeakAsync(call, new() { Model = call.NativeModel, Input = string.Concat(request.Text.Select(x => x.Text)),
            Voice = request.Text[0].Voice ?? request.Voice, ResponseFormat = request.OutputFormat?.Type, Instructions = request.Instructions, Speed = request.Speed,
            AdditionalProperties = SelectedOptions(call, request.ProviderOptions, "model", "input", "voice", "response_format", "stream_format") });
        var type = AudioType(upstream);
        StartMediaStream(call);
        await Part(new SpeechStreamStartPart());
        await Part(new SpeechResponseMetadataPart { ModelId = call.Model, Timestamp = DateTime.UtcNow });
        await using var stream = await upstream.Content.ReadAsStreamAsync(call.Ct);
        var chunk = new byte[16384];
        while (await stream.ReadAsync(chunk, call.Ct) is > 0 and var length)
            await Part(new SpeechAudioPart { Audio = Convert.ToBase64String(chunk, 0, length), AudioFormat = new() { Type = request.OutputFormat?.Type ?? AudioFormatName(type) } });
        await Part(new SpeechFinishPart());

        Task Part(StreamingSpeechPart part) => GatewayCall.WriteFrameAsync(context.Response, JsonSerializer.Serialize(part, Web), null, call.Ct);
    }

    private static Task<HttpResponseMessage> SpeakAsync(GatewayCall call, AudioSpeechRequest request)
    {
        Need(request.Input, "input");
        return call.Require<ISpeechProvider>().SpeakAsync(request, call.Headers, call.Ct);
    }

    private static string AudioType(HttpResponseMessage response)
    {
        var type = response.Content.Headers.ContentType?.MediaType;
        return type?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true || type == "application/octet-stream"
            ? type : throw new IOException("The upstream did not return audio.");
    }

    private static string AudioFormatName(string type) => type switch
    { "audio/mpeg" => "mp3", "audio/x-wav" => "wav", "audio/ogg" => "opus", "application/octet-stream" => "pcm", _ => type.Split('/')[1] };

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
        if (form["stream"] == "true") throw new ContractException("Use the streaming transcription route.");
        var options = form.Where(k => k.Key is not ("model" or "file" or "language" or "response_format" or "stream"))
            .ToDictionary(k => k.Key, k => JsonSerializer.SerializeToElement(k.Value.ToString()));
        await using var audio = file.OpenReadStream();
        var result = await call.Require<ITranscriptionProvider>().TranscribeAsync(call.NativeModel, audio, file.ContentType, file.FileName,
            form["language"], options, call.Headers, call.Ct);
        if (format == "text") { context.Response.ContentType = "text/plain; charset=utf-8"; await context.Response.WriteAsync(result.Text, call.Ct); }
        else if (format == "verbose_json") await context.Response.WriteAsJsonAsync(new { text = result.Text, duration = result.DurationInSeconds,
            language = result.Language, segments = result.Segments.Select((x, i) => new { id = i, start = x.StartSecond, end = x.EndSecond, text = x.Text }) }, Web, call.Ct);
        else await context.Response.WriteAsJsonAsync(new { text = result.Text }, Web, call.Ct);
    }

    private static async Task TranscriptionAsync(HttpContext context)
    {
        var request = await ReadAsync<TranscriptionRequest>(context, Web);
        var call = new GatewayCall(context, request.Model);
        if (request.Audio is not JsonElement { ValueKind: JsonValueKind.String } input) throw new ContractException("'audio' must be a base64 string.");
        var (bytes, mediaType) = ReadAudio(input.GetString(), request.MediaType);
        using var audio = new MemoryStream(bytes, writable: false);
        var options = SelectedOptions(call, request.ProviderOptions, "model", "file");
        var language = options?.TryGetValue("language", out var lang) == true && lang.ValueKind == JsonValueKind.String ? lang.GetString() : null;
        var result = await call.Require<ITranscriptionProvider>().TranscribeAsync(call.NativeModel, audio, mediaType,
            $"audio.{(mediaType == "audio/mpeg" ? "mp3" : mediaType.Split('/')[1])}", language, options, call.Headers, call.Ct);
        result.Response ??= new();
        result.Response.ModelId = call.Model;
        result.Response.Timestamp = DateTime.UtcNow;
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
            await GatewayCall.WriteFrameAsync(context.Response, JsonSerializer.Serialize(part, Web), null, call.Ct);
        }
    }

    private static (byte[] Bytes, string MediaType) ReadAudio(string? input, string? mediaType)
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
        if (mediaType is null || !mediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) || !mediaType.Skip(6).All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or '+'))
            throw new ContractException("An audio MIME type is required.");
        try { return (Convert.FromBase64String(encoded), mediaType); }
        catch (FormatException) { throw new ContractException("Invalid base64 audio."); }
    }
}
