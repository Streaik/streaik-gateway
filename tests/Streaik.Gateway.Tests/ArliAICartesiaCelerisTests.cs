using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Xunit;

namespace Streaik.Gateway.Tests;

public sealed class ArliAICartesiaCelerisTests
{
    private static readonly (string, string) Arli = ("X-ArliAI-Key", "fixture-arli");
    private static readonly (string, string) Cartesia = ("X-Cartesia-Key", "fixture-cartesia");
    private static readonly (string, string) Celeris = ("X-Celeris-Key", "fixture-celeris");
    private const string NativeImage = """
        {"images":["AQID"],"parameters":{"init_images":["AQID"],"sampler":{"name":"test"}},"future":{"trace":[3,null]}}
        """;

    [Fact]
    public async Task Catalogs_discover_native_models_and_keep_provider_keys_separate()
    {
        await using var host = await FixtureHost.StartAsync(request => (request.Host, request.Path) switch
        {
            ("api.arliai.com", "/v1/models") => FixtureHost.Json("""{"data":[{"id":"vendor/chat","tags":["agent","realtime","provider-tag"]}]}"""),
            ("api.arliai.com", "/v1/models/image-models") => FixtureHost.Json("""{"models":[{"model_name":"custom-checkpoint"}]}"""),
            ("inference.celeris.ai", "/celeris-1/v1/models") => FixtureHost.Json("""{"data":[{"id":"celeris-1","name":"Native Celeris","tags":true}]}"""),
            ("inference.celeris.ai", "/celeris-1-decision/v1/models") => FixtureHost.Json("""{"data":[{"id":"celeris-1-decision","tags":["agent","realtime","provider-tag"]}]}"""),
            ("api.cartesia.ai", "/voices?limit=100") => FixtureHost.Json("""{"data":[{"id":"voice-1"}],"has_more":true,"next_page":"cursor+2"}"""),
            ("api.cartesia.ai", "/voices?limit=100&starting_after=cursor%2B2") => FixtureHost.Json("""{"data":[{"id":"voice-2","language":"en","gender":"female"}]}"""),
            ("api.cartesia.ai", "/v1/agents?limit=100") => FixtureHost.Json("""{"data":[{"id":"agent-1","name":"Service"}]}"""),
            _ => throw new InvalidOperationException(request.Path)
        }, arliAICartesiaCeleris: true);
        using var response = await host.GetAsync("/v1/models", Arli, Cartesia, Celeris);
        var models = (await Json(response))["data"]!.AsArray().ToDictionary(model => (string)model!["id"]!, model => model!);
        Check("arliai/vendor/chat", "language");
        Check("arliai/custom-checkpoint", "image");
        Check("celeris/celeris-1", "language");
        Assert.Equal("Native Celeris", (string?)models["celeris/celeris-1"]["name"]);
        Check("celeris/celeris-1.1-decision", "decision");
        Check("cartesia/tts/sonic-3.6/voice-2", "speech", "voice");
        Check("cartesia/agents/agent-1", "audio", "agent", "realtime");
        Check("cartesia/transcription/realtime/auto/ink-2", "transcription", "realtime");
        Assert.All(host.Requests, request => Assert.Equal("Bearer fixture-" + (request.Host switch
        {
            "api.arliai.com" => "arli", "inference.celeris.ai" => "celeris", "api.cartesia.ai" => "cartesia", _ => "unknown"
        }), request.Headers["Authorization"]));

        void Check(string id, string type, params string[] tags)
        {
            Assert.Equal(type, (string?)models[id]["type"]);
            if (tags.Length == 0) Assert.Null(models[id]["tags"]);
            else Assert.Equal(tags, models[id]["tags"]!.AsArray().Select(tag => (string)tag!).Order());
        }
    }

    [Fact]
    public async Task ArliAI_routes_chat_vs_image_and_retains_native_metadata()
    {
        await using var host = await FixtureHost.StartAsync(request => request.Path switch
        {
            "/v1/models/image-models" => FixtureHost.Json(request.Headers["Authorization"] == "Bearer fixture-arli" ? "[\"checkpoint\"]" : "[]"),
            "/v1/txt2img" or "/v1/img2img" => FixtureHost.Json(NativeImage),
            _ => FixtureHost.Reply(request)
        }, arliAICartesiaCeleris: true);
        using var chat = await host.PostAsync("/v1/chat/completions", """
            {"model":"arliai/vendor/chat","messages":[{"role":"user","content":"hello"}],"native":{"options":[1,true]}}
            """, Arli, Celeris);
        Assert.True((bool)(await Json(chat))["future_response"]!);
        var nativeChat = Assert.Single(host.Requests, request => request.Path == "/v1/chat/completions");
        Assert.Equal("vendor/chat", (string?)nativeChat.Body!["model"]);
        Assert.True((bool)nativeChat.Body["native"]!["options"]![1]!);
        Assert.Null(nativeChat.Body["tools"]);
        Assert.False(nativeChat.Headers.ContainsKey(Celeris.Item1));

        using var image = await host.PostAsync("/api/chat", """
            {"model":"arliai/checkpoint","messages":[{"id":"u","role":"user","parts":[{"type":"text","text":"paint"}]}],
             "providerMetadata":{"arliai":{"size":"640x480","native_controls":{"items":[true,null]}}}}
            """, Arli);
        image.EnsureSuccessStatusCode();
        var imageStream = await image.Content.ReadAsStringAsync();
        Assert.Contains("data:image/png;base64,AQID", imageStream);
        Assert.Contains("\"trace\":[3,null]", imageStream);
        Assert.DoesNotContain("init_images", imageStream);
        var generation = Assert.Single(host.Requests, request => request.Path == "/v1/txt2img");
        Assert.Equal(640, (int?)generation.Body!["width"]);
        Assert.True((bool)generation.Body["native_controls"]!["items"]![0]!);

        using var edit = await host.PostAsync("/v1/images/edits", """
            {"model":"arliai/checkpoint","prompt":"edit","images":[{"image_url":"data:image/png;base64,AQID"}],
             "mask":{"image_url":"data:image/png;base64,BAUG"},"denoising_strength":0.5}
            """, Arli);
        Assert.Equal("AQID", (string?)(await Json(edit))["data"]![0]!["b64_json"]);
        var editRequest = Assert.Single(host.Requests, request => request.Path == "/v1/img2img");
        Assert.Equal("AQID", (string?)editRequest.Body!["init_images"]![0]);
        Assert.Equal("BAUG", (string?)editRequest.Body["mask"]);
        Assert.Equal(0.5, (double?)editRequest.Body["denoising_strength"]);

        using var otherKey = await host.PostAsync("/v1/chat/completions", """
            {"model":"arliai/checkpoint","messages":[{"role":"user","content":"hello"}]}
            """, (Arli.Item1, "different-key"));
        otherKey.EnsureSuccessStatusCode();
        Assert.Equal(2, host.Requests.Count(request => request.Path == "/v1/models/image-models"));
        Assert.Equal("Bearer different-key", host.Requests.Last().Headers["Authorization"]);
    }

    [Fact]
    public async Task Celeris_preserves_native_chat_and_Responses_including_stream_extensions()
    {
        await using var host = await FixtureHost.StartAsync(request => FixtureHost.Reply(request with { Path = request.Path["/celeris-1".Length..] }),
            arliAICartesiaCeleris: true);
        using var chat = await host.PostAsync("/v1/chat/completions", """
            {"model":"celeris/celeris-1","messages":[{"role":"user","content":"hello"}],"stream":true,"native":{"chat":true}}
            """, Celeris);
        chat.EnsureSuccessStatusCode();
        Assert.Contains("future_response", await chat.Content.ReadAsStringAsync());
        using var responses = await host.PostAsync("/v1/responses", """
            {"model":"celeris/celeris-1","input":"hello","stream":true,"native":{"responses":[1,2]}}
            """, Celeris);
        responses.EnsureSuccessStatusCode();
        var sse = await responses.Content.ReadAsStringAsync();
        Assert.Contains("response.completed", sse);
        Assert.Contains("future_response", sse);
        Assert.Collection(host.Requests,
            req => { Assert.Equal("/celeris-1/v1/chat/completions", req.Path); Assert.True((bool)req.Body!["native"]!["chat"]!); },
            req => { Assert.Equal("/celeris-1/v1/responses", req.Path); Assert.Equal(2, (int?)req.Body!["native"]!["responses"]![1]); });
    }

    [Fact]
    public async Task Celeris_uses_the_alias_for_both_decision_routes_and_keeps_raw_answers()
    {
        await using var host = await FixtureHost.StartAsync(request => request.Path switch
        {
            "/celeris-1-decision/v1/decisions" => FixtureHost.Json("""
                {"model":"celeris-1-decision","answers":[{"type":"predicate","name":"flag","probability":0.8}],
                 "usage":{"input_tokens":4,"output_tokens":0,"total_tokens":4},"native_extra":{"mode":"one"}}
                """),
            "/celeris-1-decision/v1/systemone" => FixtureHost.Json("""
                {"model":"celeris-1-decision","answers":{"flag":{"type":"noul","noul":0.8},
                 "band":{"type":"choice","choice":"ok","probabilities":{"ok":0.9,"bad":0.1}}},
                 "usage":{"input_tokens":4,"output_tokens":1},"native_extra":{"items":[2,true]}}
                """),
            _ => throw new InvalidOperationException(request.Path)
        }, arliAICartesiaCeleris: true);
        using var native = await host.PostAsync("/v1/decisions", """
            {"model":"celeris/celeris-1.1-decision","input":"Check","questions":[{"type":"predicate","name":"flag","instructions":"Flag?"}],
             "native_option":{"one":true}}
            """, Celeris);
        Assert.Equal("one", (string?)(await Json(native))["native_extra"]!["mode"]);
        using var sdk = await host.PostAsync("/api/decisions", """
            {"model":"celeris/celeris-1.1-decision","state":{"data":[7]},"questions":{
             "flag":{"type":"boolean","instructions":"Flag?"},"band":{"type":"choice","instructions":"Choose","criteria":{"ok":"Good","bad":"Bad"}}}}
            """, Celeris);
        var result = await Json(sdk);
        Assert.Equal(0.8, (double?)result["answers"]!["flag"]!["probability"]);
        Assert.Equal(0.9, (double?)result["answers"]!["band"]!["probabilities"]!["ok"]);
        Assert.Equal("celeris/celeris-1.1-decision", (string?)result["response"]!["modelId"]);
        Assert.True((bool)result["providerMetadata"]!["celeris"]!["native_extra"]!["items"]![1]!);
        Assert.Collection(host.Requests,
            req => { Assert.Equal("celeris-1-decision", (string?)req.Body!["model"]); Assert.True((bool)req.Body["native_option"]!["one"]!); },
            req => { Assert.Equal("noul", (string?)req.Body!["questions"]!["flag"]!["type"]); Assert.Equal(7, (int?)req.Body["state"]!["data"]![0]); });
    }

    [Fact]
    public async Task Cartesia_maps_speech_bytes_and_SSE_and_preserves_SDK_event_metadata()
    {
        await using var host = await FixtureHost.StartAsync(request => request.Path switch
        {
            "/tts/bytes" => Audio(),
            "/tts/sse" => FixtureHost.Sse("""{"type":"chunk","data":"AQID","context_id":"ctx","trace":{"seq":1}}""",
                """{"type":"timestamps","word_timestamps":{"words":["hello"]}}""", """{"type":"done","future":{"stream":true}}"""),
            _ => throw new InvalidOperationException(request.Path)
        }, arliAICartesiaCeleris: true);
        using var bytes = await host.PostAsync("/v1/audio/speech", """
            {"model":"cartesia/tts/sonic-3/voice","input":"hello","response_format":"wav","future":{"deep":[4]}}
            """, Cartesia);
        bytes.EnsureSuccessStatusCode();
        Assert.Equal("audio/wav", bytes.Content.Headers.ContentType!.MediaType);
        using var native = await host.PostAsync("/v1/audio/speech", """
            {"model":"cartesia/tts/sonic-3/voice","input":"hello","stream_format":"sse","response_format":"pcm"}
            """, Cartesia);
        native.EnsureSuccessStatusCode();
        var sse = await native.Content.ReadAsStringAsync();
        Assert.Contains("speech.audio.delta", sse);
        Assert.Contains("speech.audio.done", sse);
        Assert.DoesNotContain("timestamps", sse);
        using var sdk = await host.PostAsync("/api/speech/stream", """
            {"model":"cartesia/tts/sonic-3/voice","text":[{"type":"text","text":"hel","providerOptions":{"cartesia":{"language":"en"}}},{"type":"text","text":"lo"}],
             "language":"en","outputFormat":{"type":"audio/pcm","rate":24000},"providerOptions":{"cartesia":{"future":{"deep":[4]}}}}
            """, Cartesia, ("Cartesia-Version", "2026-08-15"));
        sdk.EnsureSuccessStatusCode();
        var events = await sdk.Content.ReadAsStringAsync();
        Assert.Contains("\"word_timestamps\":{\"words\":[\"hello\"]}", events);
        Assert.Contains("\"future\":{\"stream\":true}", events);
        Assert.Contains("\"rate\":24000", events);
        var outbound = host.Requests.Last();
        Assert.Equal("Bearer fixture-cartesia", outbound.Headers["Authorization"]);
        Assert.Equal("2026-08-15", outbound.Headers["Cartesia-Version"]);
        Assert.Equal("voice", (string?)outbound.Body!["voice"]!["id"]);
        Assert.Equal("hello", (string?)outbound.Body["transcript"]);
        Assert.Equal(4, (int?)outbound.Body["future"]!["deep"]![0]);
    }

    [Fact]
    public async Task Cartesia_keeps_transcription_metadata_and_mints_only_the_requested_realtime_grant()
    {
        await using var host = await FixtureHost.StartAsync(request => request.Path switch
        {
            "/stt" or "/stt?sample_rate=16000" => FixtureHost.Json("""
                {"text":"hello","duration":0.7,"language":"en","words":[{"word":"hello","start":0.1,"end":0.6}],"analytics":{"native":[2]}}
                """),
            "/access-token" => FixtureHost.Json("""{"token":"short-lived"}"""),
            _ => throw new InvalidOperationException(request.Path)
        }, arliAICartesiaCeleris: true);
        using var transcript = await host.PostAsync("/api/transcriptions", """
            {"model":"cartesia/transcription/ink-whisper","audio":"AQID","mediaType":"audio/wav",
             "providerOptions":{"cartesia":{"sampleRate":16000,"future":{"deep":[4]}}}}
            """, Cartesia);
        var raw = await Json(transcript);
        Assert.Equal(2, (int?)raw["providerMetadata"]!["cartesia"]!["analytics"]!["native"]![0]);
        Assert.Equal(0.1, (double?)raw["segments"]![0]!["startSecond"]);
        Assert.Contains("{\"deep\":[4]}", host.Requests.Last().RawBody);

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("cartesia/transcription/ink-whisper"), "model");
        form.Add(new StringContent("true"), "stream");
        form.Add(Audio().Content, "file", "audio.wav");
        using var native = await host.PostFormAsync("/v1/audio/transcriptions", form, Cartesia);
        native.EnsureSuccessStatusCode();
        Assert.Contains("transcript.text.done", await native.Content.ReadAsStringAsync());

        using var chat = await host.PostAsync("/api/chat", """
            {"model":"cartesia/transcription/ink-whisper","messages":[{"id":"u","role":"user","parts":[{"type":"file","mediaType":"audio/wav","url":"data:audio/wav;base64,AQID"}]}]}
            """, Cartesia);
        chat.EnsureSuccessStatusCode();
        Assert.Contains("\"analytics\":{\"native\":[2]}", await chat.Content.ReadAsStringAsync());

        using var token = await host.PostAsync("/api/realtime/token/cartesia/transcription/realtime/auto/ink-2", """
            {"sessionConfig":{"providerOptions":{"cartesia":{"expires_in":45}}}}
            """, Cartesia);
        Assert.Equal("short-lived", (string?)(await Json(token))["token"]);
        var outbound = host.Requests.Last();
        Assert.Equal(45, (int?)outbound.Body!["expires_in"]);
        Assert.True((bool)outbound.Body["grants"]!["stt"]!);
        Assert.False((bool)outbound.Body["grants"]!["tts"]!);
        Assert.False((bool)outbound.Body["grants"]!["agent"]!);
        Assert.Equal("no-store", token.Headers.CacheControl!.ToString());
    }

    [Fact]
    public async Task Cartesia_sonic_speech_is_tagged_and_uses_a_TTS_only_browser_grant()
    {
        await using var host = await FixtureHost.StartAsync(request => request.Path switch
        {
            "/voices?limit=100" => FixtureHost.Json("""{"data":[{"id":"voice-1"}]}"""),
            "/v1/agents?limit=100" => FixtureHost.Json("""{"data":[]}"""),
            "/access-token" => FixtureHost.Json("""{"token":"short-lived"}"""),
            _ => throw new InvalidOperationException(request.Path)
        }, arliAICartesiaCeleris: true);
        const string id = "cartesia/tts/sonic-3.6/voice-1";
        using var catalog = await host.GetAsync("/v1/models", Cartesia);
        var model = Assert.Single((await Json(catalog))["data"]!.AsArray(), x => (string?)x?["id"] == id);
        Assert.Equal("speech", (string?)model?["type"]);
        Assert.Contains(model!["tags"]!.AsArray(), tag => (string?)tag == "realtime");

        using var response = await host.PostAsync($"/api/realtime/token/{id}", "{}", Cartesia);
        var body = await Json(response);
        Assert.Equal("short-lived", (string?)body["token"]);
        Assert.Equal("wss://api.cartesia.ai/tts/websocket?cartesia_version=2026-08-14", (string?)body["url"]);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var request = host.Requests.Last();
        Assert.Equal("/access-token", request.Path);
        Assert.Equal("Bearer fixture-cartesia", request.Headers["Authorization"]);
        Assert.True((bool)request.Body!["grants"]!["tts"]!);
        Assert.False((bool)request.Body!["grants"]!["stt"]!);
        Assert.False((bool)request.Body!["grants"]!["agent"]!);
    }

    [Fact]
    public async Task Cartesia_uses_existing_Chat_Completions_audio_fields()
    {
        await using var host = await FixtureHost.StartAsync(request => request.Path switch
        {
            "/tts/bytes" => Audio(),
            "/tts/sse" => FixtureHost.Sse("""{"type":"chunk","data":"AQID","future":{"sequence":9}}""", """{"type":"done"}"""),
            "/stt" => FixtureHost.Json("""{"text":"hello","provider_trace":{"deep":3}}"""),
            _ => throw new InvalidOperationException(request.Path)
        }, arliAICartesiaCeleris: true);
        using var voice = await host.PostAsync("/v1/chat/completions", """
            {"model":"cartesia/tts/sonic-3/voice","messages":[{"role":"user","content":"Say hello"}],"audio":{"voice":"voice","format":"wav"}}
            """, Cartesia);
        Assert.Equal("AQID", (string?)(await Json(voice))["choices"]![0]!["message"]!["audio"]!["data"]);
        using var stream = await host.PostAsync("/v1/chat/completions", """
            {"model":"cartesia/tts/sonic-3/voice","messages":[{"role":"user","content":"Say hello"}],
             "stream":true,"audio":{"voice":"voice","format":"pcm16"}}
            """, Cartesia);
        stream.EnsureSuccessStatusCode();
        var chunks = await stream.Content.ReadAsStringAsync();
        Assert.Contains("\"data\":\"AQID\"", chunks);
        Assert.Contains("\"sequence\":9", chunks);
        Assert.Contains("[DONE]", chunks);
        using var text = await host.PostAsync("/v1/chat/completions", """
            {"model":"cartesia/transcription/ink-whisper","messages":[{"role":"user","content":[{"type":"input_audio","input_audio":{"data":"AQID","format":"wav"}}]}]}
            """, Cartesia);
        var result = await Json(text);
        Assert.Equal("hello", (string?)result["choices"]![0]!["message"]!["content"]);
        Assert.Equal(3, (int?)result["metadata"]!["cartesia"]!["provider_trace"]!["deep"]);
    }

    private static HttpResponseMessage Audio()
    {
        var result = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
        result.Content.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        return result;
    }

    private static async Task<JsonNode> Json(HttpResponseMessage response)
    {
        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"HTTP {(int)response.StatusCode}: {raw}");
        return JsonNode.Parse(raw)!;
    }
}
