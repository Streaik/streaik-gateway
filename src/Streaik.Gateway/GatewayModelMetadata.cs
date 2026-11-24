using System.Text.RegularExpressions;
using Streaik.OpenAI.Models;

namespace Streaik.Gateway;

internal static partial class GatewayModelMetadata
{
    internal static void Normalize(Model model, string provider)
    {
        // Use the model name segment, not the provider or publisher namespace.
        var name = model.Id[(model.Id.LastIndexOf('/') + 1)..].ToLowerInvariant();
        var type = model.Type?.Trim().ToLowerInvariant();
        var realtime = IsRealtime(type) || IsKnownLiveModel(provider, name);
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in model.Tags ?? [])
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var tag = raw.Trim();
            if (IsRealtime(tag.ToLowerInvariant())) realtime = true;
            else tags.Add(tag);
        }

        if (realtime) tags.Add("realtime");
        model.Tags = tags.Count > 0 ? tags.ToArray() : null;
        model.Type = type switch
        {
            null or "" or "model" => Infer(provider, name, realtime),
            "chat" or "text" or "llm" => "language",
            "embeddings" => "embedding",
            "rerank" => "reranking",
            "tts" or "text-to-speech" => "speech",
            "stt" or "asr" or "speech-to-text" => "transcription",
            "real-time" or "realtime" => "audio",
            _ => type,
        };
    }

    private static bool IsRealtime(string? value) => value is "realtime" or "real-time";

    private static bool IsKnownLiveModel(string provider, string name) => provider switch
    {
        "openai" => OpenAiLive().IsMatch(name) || OpenAiLiveTranscription().IsMatch(name),
        "google" => name.StartsWith("gemini-", StringComparison.Ordinal) &&
            (name.Contains("-live", StringComparison.Ordinal) || name.Contains("-native-audio", StringComparison.Ordinal)),
        "xai" => name == "grok-voice-latest",
        _ => false,
    };

    private static string Infer(string provider, string name, bool realtime)
    {
        if (Transcription().IsMatch(name) || (name.StartsWith("voxtral", StringComparison.Ordinal) && !Speech().IsMatch(name))) return "transcription";
        if (Speech().IsMatch(name)) return "speech";
        if (Reranking().IsMatch(name)) return "reranking";
        if (Embedding().IsMatch(name)) return "embedding";
        // Check video before image for image-to-video families.
        if (Video().IsMatch(name)) return "video";
        if (Image().IsMatch(name)) return "image";
        if (realtime || Audio().IsMatch(name)) return "audio";
        if (Moderation().IsMatch(name)) return "moderation";
        if (provider == "openai")
            return OpenAiLanguage().IsMatch(name) && !OpenAiNonChat().IsMatch(name) ? "language" : "other";
        if (provider == "anthropic" && name.StartsWith("claude-", StringComparison.Ordinal)) return "language";
        if (provider == "google" && name.StartsWith("gemini-", StringComparison.Ordinal)) return "language";
        if (provider == "xai" && name.StartsWith("grok-", StringComparison.Ordinal) && !XaiNonChat().IsMatch(name)) return "language";
        // These adapters use chat-oriented listings; special-purpose IDs above still take precedence.
        return provider is "cencori" or "cerebras" or "chutes" or "deepseek" or "groq" ? "language" : "other";
    }

    [GeneratedRegex("^gpt-(?:4o-)?realtime(?:-|$)")]
    private static partial Regex OpenAiLive();
    [GeneratedRegex("^gpt-live-transcribe(?:-\\d{4}-\\d{2}-\\d{2})?$")]
    private static partial Regex OpenAiLiveTranscription();
    [GeneratedRegex("^(?:gpt-(?:3\\.5|4o|[4-9](?:\\.\\d+)?)|o[1-9])(?:-|$)")]
    private static partial Regex OpenAiLanguage();
    [GeneratedRegex("(?:^|[-_.])(?:search|vision)(?:[-_.]|$)")]
    private static partial Regex OpenAiNonChat();
    [GeneratedRegex("(?:^|[-_.])(?:voice|imagine)(?:[-_.]|$)")]
    private static partial Regex XaiNonChat();
    [GeneratedRegex("(?:^|[-_.])(?:whisper|transcribe|transcription|stt|asr|speech-to-text)(?:[-_.]|$)")]
    private static partial Regex Transcription();
    [GeneratedRegex("(?:^|[-_.])(?:tts|text-to-speech|cartesia|orpheus|kokoro|chatterbox)(?:[-_.]|$)")]
    private static partial Regex Speech();
    [GeneratedRegex("(?:^|[-_.])rerank(?:er|ing)?(?:[-_.]|$)")]
    private static partial Regex Reranking();
    [GeneratedRegex("(?:^|[-_.])embed(?:ding|dings)?(?:[-_.]|$)|^bge-(?:m3|multilingual)(?:[-_.]|$)")]
    private static partial Regex Embedding();
    [GeneratedRegex("(?:^|[-_.])(?:video|sora|seedance|veo|wan[23]?|kling|pixverse|runway|hailuo|[tir]2v)(?:[0-9]*[-_.]|$)")]
    private static partial Regex Video();
    [GeneratedRegex("(?:^|[-_.])(?:image|flux|stable-diffusion|sdxl|dall-e|dalle|imagen|ideogram|seedream|recraft|upscaler?)(?:[0-9]*[-_.]|$)")]
    private static partial Regex Image();
    [GeneratedRegex("(?:^|[-_.])audio(?:[-_.]|$)")]
    private static partial Regex Audio();
    [GeneratedRegex("(?:^|[-_.])moderation(?:[-_.]|$)")]
    private static partial Regex Moderation();
}
