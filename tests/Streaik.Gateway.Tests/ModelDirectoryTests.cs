using System.Net;
using System.Text.Json.Nodes;

namespace Streaik.Gateway.Tests;

public sealed class ModelDirectoryTests
{
    [Fact]
    public async Task OpenRouter_uses_output_modalities_and_preserves_multimodal_ids()
    {
        await using var host = await FixtureHost.StartAsync(request => request.Host == "openrouter.ai"
            ? FixtureHost.Json("""
                {"data":[
                  {"id":"org/vision-reader","architecture":{"input_modalities":["text","image"],"output_modalities":["text"]}},
                  {"id":"org/multimodal","created":1700000000,"tags":["agent","voice","translate","realtime","tools"],"architecture":{"input_modalities":["text"],"output_modalities":["text","image"]},"top_provider":{"max_completion_tokens":2048}},
                  {"id":"org/speech-to-text","architecture":{"input_modalities":["audio"],"output_modalities":["text"]}}
                ]}
                """) : new HttpResponseMessage(HttpStatusCode.NotFound), media: true);
        using var response = await host.GetAsync("/v1/models", ("X-OpenRouter-Key", "fixture"));
        var models = JsonNode.Parse(await GatewayTests.Success(response))!["data"]!.AsArray()
            .ToLookup(item => (string)item!["id"]!, item => item!);
        Assert.Equal("language", (string?)Assert.Single(models["openrouter/org/vision-reader"])["type"]);
        Assert.Equal("transcription", (string?)Assert.Single(models["openrouter/org/speech-to-text"])["type"]);
        var multimodal = models["openrouter/org/multimodal"].ToArray();
        Assert.Equal(new[] { "image", "language" }, multimodal.Select(item => (string)item["type"]!).Order());
        Assert.All(multimodal, item =>
        {
            Assert.Equal(1700000000L, (long)item["created"]!);
            Assert.Equal(2048, (int)item["max_tokens"]!);
            Assert.Null(item["tags"]);
        });
        Assert.Equal(new[] { "/api/v1/models?output_modalities=all", "/api/v1/models?output_modalities=speech,transcription" },
            host.Requests.Where(request => request.Host == "openrouter.ai").Select(request => request.Path));
    }

    [Fact]
    public async Task Listing_normalizes_types_and_tags_without_changing_model_identity()
    {
        await using var host = await FixtureHost.StartAsync(request => request.Host switch
        {
            "api.openai.com" => FixtureHost.Json("""
                {"data":[
                  {"id":"gpt-6.2","tags":{"unexpected":true}}, {"id":"text-embedding-3-small"}, {"id":"image-to-video"},
                  {"id":"vendor/realtime-dashboard"}, {"id":"gpt-4o-audio-preview"},
                  {"id":"gpt-realtime-mini","tags":["Voice","real-time","Realtime","voice"]},
                  {"id":"gpt-realtime-transcribe","type":"transcription","tags":["asr"]},
                  {"id":"gpt-live-1"}, {"id":"gpt-live-1-2026-10-01","type":"language"},
                  {"id":"gpt-live-transcribe"}, {"id":"gpt-live-transcribe-2026-10-01"}, {"id":"gpt-realtime-whisper"}, {"id":"gpt-transcribe"},
                  {"id":"gpt-image-1","type":" language ","tags":["streaming","translate","agent"]},
                  {"id":"gpt-4o-mini-tts","type":"model"}, {"id":"whisper-1"}
                ]}
                """),
            "generativelanguage.googleapis.com" => FixtureHost.Json("""
                {"data":[{"id":"models/gemini-2.5-flash-native-audio-preview","tags":["voice","agent"]},
                  {"id":"models/gemini-embedding-001","tags":["real-time","translate","provider-tag"]}]}
                """),
            "api.x.ai" => FixtureHost.Json("""
                {"data":[{"id":"grok-voice-latest","type":"model"}]}
                """),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        }, media: true, liveProviders: true);

        using var response = await host.GetAsync("/v1/models", ("X-OpenAI-Key", "fixture"), ("X-Google-Key", "fixture"),
            ("X-Xai-Key", "fixture"), ("X-AssemblyAI-Key", "fixture"), ("X-Deepgram-Key", "fixture"));
        var models = JsonNode.Parse(await GatewayTests.Success(response))!["data"]!.AsArray()
            .ToDictionary(item => (string)item!["id"]!, item => item!);
        Check("openai/gpt-6.2", "language");
        Check("openai/text-embedding-3-small", "embedding");
        Check("openai/image-to-video", "video");
        Check("openai/vendor/realtime-dashboard", "other");
        Check("openai/gpt-4o-audio-preview", "audio");
        Check("openai/gpt-image-1", "language"); // Explicit provider type wins; streaming is not realtime.
        Check("openai/gpt-realtime-mini", "audio", "realtime");
        Check("openai/gpt-realtime-transcribe", "transcription", "realtime");
        Check("openai/gpt-live-1", "audio", "realtime");
        Check("openai/gpt-live-1-2026-10-01", "audio", "realtime");
        Check("openai/gpt-live-transcribe", "transcription", "realtime");
        Check("openai/gpt-live-transcribe-2026-10-01", "transcription", "realtime");
        Check("openai/gpt-realtime-whisper", "transcription", "realtime");
        Check("openai/gpt-transcribe", "transcription");
        Check("openai/gpt-4o-mini-tts", "speech");
        Check("openai/whisper-1", "transcription");
        Check("google/gemini-2.5-flash-native-audio-preview", "audio", "realtime");
        Check("google/gemini-embedding-001", "embedding");
        Check("xai/grok-voice-latest", "audio", "realtime");
        Check("assemblyai/assemblyai-stt-3-7-realtime", "transcription", "realtime");
        Check("assemblyai/universal-streaming-english", "transcription", "realtime");
        Check("deepgram/nova-3", "transcription", "realtime");
        Check("deepgram/aura-2-asteria-en", "speech");

        void Check(string id, string type, params string[] tags)
        {
            var model = models[id];
            Assert.Equal(type, (string?)model["type"]);
            Assert.Equal(tags.Order(), (model["tags"]?.AsArray().Select(tag => (string)tag!) ?? []).Order());
        }
    }

    [Fact]
    public async Task Xai_exposes_grok_voice_when_the_language_model_directory_omits_it()
    {
        await using var host = await FixtureHost.StartAsync(_ => FixtureHost.Json("""{"data":[{"id":"grok-4"}]}"""), liveProviders: true);
        using var response = await host.GetAsync("/v1/models", ("X-Xai-Key", "fixture"));
        var models = JsonNode.Parse(await GatewayTests.Success(response))!["data"]!.AsArray();
        var voice = Assert.Single(models, model => (string?)model?["id"] == "xai/grok-voice-latest")!;
        Assert.Equal("audio", (string?)voice["type"]);
        Assert.Equal("realtime", (string?)Assert.Single(voice["tags"]!.AsArray()));
    }

    [Fact]
    public async Task Anthropic_ignores_upstream_tag_values_without_losing_models_or_metadata()
    {
        await using var host = await FixtureHost.StartAsync(request => request.Host == "api.anthropic.com"
            ? FixtureHost.Json("""
                {"data":[{"id":"claude-typed","created_at":"2025-01-01T01:00:00+01:00","tags":["real-time","voice","agent","translate","provider-tag"]},
                  {"id":"claude-untyped","created_at":"not a date","description":"Model description","context_window":16384,"tags":[42]}],"has_more":false}
                """) : new HttpResponseMessage(HttpStatusCode.NotFound));

        using var response = await host.GetAsync("/v1/models", ("X-Anthropic-Key", "fixture"));
        var models = JsonNode.Parse(await GatewayTests.Success(response))!["data"]!.AsArray()
            .ToDictionary(item => (string)item!["id"]!, item => item!);
        Assert.Equal(2, models.Count);
        Assert.All(models.Values, model =>
        {
            Assert.Equal("language", (string?)model["type"]);
            Assert.Null(model["tags"]);
        });
        Assert.Equal(1735689600L, (long)models["anthropic/claude-typed"]["created"]!);
        Assert.Null(models["anthropic/claude-untyped"]["created"]);
        Assert.Equal("Model description", (string?)models["anthropic/claude-untyped"]["description"]);
        Assert.Equal(16384, (int?)models["anthropic/claude-untyped"]["context_window"]);
    }
}
