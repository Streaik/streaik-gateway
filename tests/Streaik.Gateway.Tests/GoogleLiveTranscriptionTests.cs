using System.Net;
using System.Text.Json.Nodes;

namespace Streaik.Gateway.Tests;

public sealed class GoogleLiveTranscriptionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Catalog_distinguishes_transcription_from_voice_on_both_Google_listings(bool useFallback)
    {
        await using var host = await FixtureHost.StartAsync(request => request.Path switch
        {
            "/v1beta/models?pageSize=1000" => useFallback ? FixtureHost.Json("{}") : FixtureHost.Json("""
                {"models":[{"name":"models/gemini-3.5-transcribe-live","supportedGenerationMethods":["bidiGenerateContent"]},{"name":"models/gemini-2.5-flash-native-audio-preview"}]}
                """),
            "/v1beta/openai/models" when useFallback => FixtureHost.Json("""
                {"data":[{"id":"models/gemini-3.5-transcribe-live","type":"audio"},{"id":"models/gemini-2.5-flash-native-audio-preview"}]}
                """),
            "/v1beta/agents?page_size=1000" => FixtureHost.Json("{}"),
            _ => throw new InvalidOperationException("Unexpected Google request.")
        }, liveProviders: true);
        using var catalog = await host.GetAsync("/v1/models", ("x-google-key", "google-fixture-key"));
        var data = JsonNode.Parse(await GatewayTests.Success(catalog))!["data"]!.AsArray();
        var live = Assert.Single(data, row => (string?)row?["id"] == "google/gemini-3.5-transcribe-live")!;
        Assert.Equal("transcription", (string?)live["type"]);
        Assert.Equal("realtime", (string?)Assert.Single(live["tags"]!.AsArray()));
        var voice = Assert.Single(data, row => (string?)row?["id"] == "google/gemini-2.5-flash-native-audio-preview")!;
        Assert.Equal("audio", (string?)voice["type"]);
    }

    [Fact]
    public async Task Transcribe_Live_mints_a_constrained_beta_token_and_rejects_file_uploads()
    {
        await using var host = await FixtureHost.StartAsync(_ => FixtureHost.Json("""{"name":"auth_tokens/temporary-google-token"}"""), liveProviders: true);
        using var token = await host.PostAsync("/api/realtime/token/google/gemini-3.5-transcribe-live", """
            {"sessionConfig":{"providerOptions":{"google":{"uses":1,"bidiGenerateContentSetup":{
              "model":"models/not-the-route-model","generationConfig":{"responseModalities":["TEXT"]},
              "inputAudioTranscription":{"languageCodes":["nl-NL"],"mode":"VERBATIM"}}}}}}
            """, ("x-google-key", "google-fixture-key"));
        Assert.True(token.Headers.CacheControl?.NoStore);
        var output = await GatewayTests.Success(token);
        Assert.DoesNotContain("google-fixture-key", output);
        var payload = JsonNode.Parse(output)!;
        Assert.Equal("auth_tokens/temporary-google-token", (string?)payload["token"]);
        Assert.EndsWith(".v1beta.GenerativeService.BidiGenerateContentConstrained", (string?)payload["url"]);

        using var file = await host.PostAsync("/api/transcriptions", """
            {"model":"google/gemini-3.5-transcribe-live","audio":"YXVkaW8=","mediaType":"audio/wav"}
            """, ("x-google-key", "google-fixture-key"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, file.StatusCode);

        var upstream = Assert.Single(host.Requests);
        Assert.Equal("/v1beta/auth_tokens", upstream.Path);
        Assert.Equal("google-fixture-key", upstream.Headers["x-goog-api-key"]);
        Assert.Equal(1, (int?)upstream.Body!["uses"]);
        Assert.False(upstream.Body.AsObject().ContainsKey("fieldMask")); // An omitted field mask locks the whole setup.
        Assert.True(DateTimeOffset.TryParse((string?)upstream.Body["newSessionExpireTime"], out var startBefore));
        Assert.True(DateTimeOffset.TryParse((string?)upstream.Body["expireTime"], out var expires));
        Assert.True(startBefore < expires);
        Assert.Equal(expires.ToUnixTimeSeconds(), (long?)payload["expiresAt"]);
        var setup = upstream.Body["bidiGenerateContentSetup"]!;
        Assert.Equal("models/gemini-3.5-transcribe-live", (string?)setup["model"]);
        Assert.Equal("TEXT", (string?)Assert.Single(setup["generationConfig"]!["responseModalities"]!.AsArray()));
        Assert.Equal("nl-NL", (string?)Assert.Single(setup["inputAudioTranscription"]!["languageCodes"]!.AsArray()));
        Assert.Equal("VERBATIM", (string?)setup["inputAudioTranscription"]!["mode"]);
    }
}
