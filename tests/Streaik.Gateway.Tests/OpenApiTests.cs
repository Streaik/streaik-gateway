using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Streaik.Gateway.Sample;

namespace Streaik.Gateway.Tests;

public sealed class OpenApiTests
{
    [Fact]
    public async Task DocumentIncludesEveryGatewayOperationAndRequestBody()
    {
        await using var host = await StartAsync();
        using var document = JsonDocument.Parse(await host.Client.GetStringAsync("/openapi/v1.json"));
        var root = document.RootElement;
        var paths = root.GetProperty("paths");
        (string Path, string? JsonProperty)[] posts =
        [
            ("/v1/chat/completions", "messages"), ("/v1/responses", "input"), ("/v1/messages", "max_tokens"), ("/api/chat", "messages"),
            ("/v1/decisions", "input"), ("/api/decisions", "state"),
            ("/v1/images/generations", "prompt"), ("/v1/images/edits", "images"), ("/api/images", "prompt"), ("/api/videos", "prompt"),
            ("/v1/audio/speech", "input"), ("/api/speech", "text"), ("/api/speech/stream", "text"), ("/v1/audio/transcriptions", null),
            ("/api/transcriptions", "audio"), ("/api/transcriptions/stream", "audio"), ("/api/realtime/token/{model}", null),
            ("/api/realtime/session/{model}", "sdp"), ("/v1/realtime/client_secrets", "session"), ("/v1/live/sessions", "transport"),
            ("/v1/embeddings", "input"), ("/api/embeddings", "values"), ("/api/rerank", "documents"), ("/v1/skills", null)
        ];
        Assert.Equal(posts.Length, paths.EnumerateObject().Count(p => p.Value.TryGetProperty("post", out _)));
        Assert.Equal(7, paths.EnumerateObject().Count(p => p.Value.TryGetProperty("get", out _)));
        AssertPathParameters(paths.GetProperty("/v1/skills/{providerId}/{skillId}/versions/{version}/content").GetProperty("get"),
            "providerId", "skillId", "version");
        Assert.DoesNotContain(paths.EnumerateObject(), path => path.Name.StartsWith("/v1beta/", StringComparison.Ordinal));
        Assert.Equal(new[] { "Anthropic", "OpenAI", "Vercel" },
            root.GetProperty("tags").EnumerateArray().Select(t => t.GetProperty("name").GetString()).Order(StringComparer.Ordinal));
        AssertTag(paths.GetProperty("/v1/models").GetProperty("get"), "OpenAI");
        var video = paths.GetProperty("/api/videos/{providerId}/{operation}").GetProperty("get");
        AssertTag(video, "Vercel");
        AssertPathParameters(video, "operation", "providerId");

        JsonElement Post(string path) => paths.GetProperty(path).GetProperty("post");
        JsonElement RequestContent(string path) => Post(path).GetProperty("requestBody").GetProperty("content");
        JsonElement ResponseContent(string path, string status = "200") => Post(path).GetProperty("responses").GetProperty(status).GetProperty("content");

        foreach (var (path, property) in posts)
        {
            var operation = Post(path);
            var tag = path == "/v1/messages" ? "Anthropic" : path.StartsWith("/api/", StringComparison.Ordinal) ? "Vercel" : "OpenAI";
            AssertTag(operation, tag);
            Assert.True(operation.GetProperty("requestBody").GetProperty("required").GetBoolean());
            if (path == "/v1/audio/transcriptions" || path == "/v1/skills") continue;
            var schema = ResolveSchema(root, RequestContent(path).GetProperty("application/json").GetProperty("schema"));
            Assert.Equal("object", schema.GetProperty("type").GetString());
            if (property is not null) Assert.True(schema.GetProperty("properties").TryGetProperty(property, out _), $"{path} is missing {property}.");
        }

        AssertPathParameters(Post("/api/realtime/token/{model}"), "model");
        AssertPathParameters(Post("/api/realtime/session/{model}"), "model");

        var responses = ResolveSchema(root, RequestContent("/v1/responses").GetProperty("application/json").GetProperty("schema"));
        var input = ResolveSchema(root, responses.GetProperty("properties").GetProperty("input"));
        var inputTypes = input.GetProperty("anyOf").EnumerateArray().Select(s => s.GetProperty("type").GetString()).ToArray();
        Assert.Contains("string", inputTypes);
        Assert.Contains("array", inputTypes);

        var imageForm = RequestContent("/v1/images/edits").GetProperty("multipart/form-data").GetProperty("schema").GetProperty("properties");
        Assert.Equal("binary", imageForm.GetProperty("image").GetProperty("items").GetProperty("format").GetString());
        Assert.Equal("binary", imageForm.GetProperty("mask").GetProperty("format").GetString());
        var transcription = RequestContent("/v1/audio/transcriptions");
        Assert.False(transcription.TryGetProperty("application/json", out _));
        var audioForm = transcription.GetProperty("multipart/form-data").GetProperty("schema");
        Assert.Contains(audioForm.GetProperty("required").EnumerateArray(), field => field.GetString() == "file");
        Assert.Equal("binary", audioForm.GetProperty("properties").GetProperty("file").GetProperty("format").GetString());

        Assert.True(ResponseContent("/v1/chat/completions").TryGetProperty("application/json", out _));
        Assert.Equal("string", ResponseContent("/v1/chat/completions").GetProperty("text/event-stream").GetProperty("schema").GetProperty("type").GetString());
        Assert.True(ResponseContent("/api/chat").TryGetProperty("text/event-stream", out _));
        foreach (var path in new[] { "/v1/decisions", "/api/decisions" })
            Assert.Equal("application/json", Assert.Single(ResponseContent(path).EnumerateObject()).Name);
        Assert.Equal("binary", ResponseContent("/v1/audio/speech").GetProperty("audio/mpeg").GetProperty("schema").GetProperty("format").GetString());
        Assert.True(ResponseContent("/v1/live/sessions", "201").TryGetProperty("application/json", out _));
    }

    [Fact]
    public async Task CapabilityViewGroupsTheSameOperationsByCapabilityThenRoutePrefix()
    {
        await using var host = await StartAsync();
        var family = JsonNode.Parse(await host.Client.GetStringAsync("/openapi/v1.json"))!;
        var capability = JsonNode.Parse(await host.Client.GetStringAsync("/openapi/capabilities.json"))!;
        var capabilityPaths = capability["paths"]!.AsObject();
        var groups = capability["x-tagGroups"]!.AsArray();
        var displayNames = capability["tags"]!.AsArray().ToDictionary(
            tag => tag!["name"]!.GetValue<string>(), tag => tag!["x-displayName"]!.GetValue<string>());

        (string Name, string[] V1, string[] Api)[] expected =
        [
            ("Chat", ["/v1/chat/completions", "/v1/responses", "/v1/messages"], ["/api/chat"]),
            ("Decisions", ["/v1/decisions"], ["/api/decisions"]),
            ("Images", ["/v1/images/generations", "/v1/images/edits"], ["/api/images"]),
            ("Videos", [], ["/api/videos", "/api/videos/{providerId}/{operation}"]),
            ("Speech", ["/v1/audio/speech"], ["/api/speech", "/api/speech/stream"]),
            ("Transcriptions", ["/v1/audio/transcriptions"], ["/api/transcriptions", "/api/transcriptions/stream"]),
            ("Realtime", ["/v1/realtime/client_secrets", "/v1/live/sessions"], ["/api/realtime/token/{model}", "/api/realtime/session/{model}"]),
            ("Embeddings", ["/v1/embeddings"], ["/api/embeddings"]),
            ("Rerank", [], ["/api/rerank"]),
            ("Discovery", ["/v1/models"], []),
            ("Skills", ["/v1/skills", "/v1/skills/providers", "/v1/skills/{providerId}/{skillId}/versions",
                "/v1/skills/{providerId}/{skillId}/content", "/v1/skills/{providerId}/{skillId}/versions/{version}/content"], [])
        ];
        Assert.Equal(expected.Select(item => item.Name), groups.Select(group => group!["name"]!.GetValue<string>()));
        Assert.Equal(expected.SelectMany(item => item.V1.Concat(item.Api)).Order(StringComparer.Ordinal),
            capabilityPaths.Select(path => path.Key).Order(StringComparer.Ordinal));

        foreach (var ((name, v1, api), group) in expected.Zip(groups))
        {
            var sectionTags = new List<string>();
            CheckSection("/v1", v1);
            CheckSection("/api", api);
            Assert.Equal(sectionTags, group!["tags"]!.AsArray().Select(tag => tag!.GetValue<string>()));

            void CheckSection(string prefix, string[] paths)
            {
                if (paths.Length == 0) return;
                var tag = $"{name} {prefix}";
                sectionTags.Add(tag);
                Assert.True(displayNames.Remove(tag, out var displayName), $"Missing tag: {tag}");
                Assert.Equal(prefix, displayName);
                foreach (var path in paths)
                    foreach (var (_, value) in capabilityPaths[path]!.AsObject())
                    {
                        var operation = value!.AsObject();
                        Assert.Equal(tag, Assert.Single(operation["tags"]!.AsArray())!.GetValue<string>());
                        operation.Remove("tags");
                    }
            }
        }
        Assert.Empty(displayNames);

        foreach (var (_, path) in family["paths"]!.AsObject())
            foreach (var (_, operation) in path!.AsObject())
                operation!.AsObject().Remove("tags");

        Assert.True(JsonNode.DeepEquals(family["paths"], capabilityPaths), "Only the operation tags should differ.");
        Assert.True(JsonNode.DeepEquals(family["components"], capability["components"]), "Both documents should use the same components.");
    }

    [Theory]
    [InlineData("/v1/messages", "text/plain", HttpStatusCode.UnsupportedMediaType, "Expected application/json.", true)]
    [InlineData("/v1/messages", "application/json", HttpStatusCode.BadRequest, "Invalid request for this protocol.", true)]
    [InlineData("/v1/audio/transcriptions", "application/json", HttpStatusCode.UnsupportedMediaType, "Expected multipart/form-data.", false)]
    public async Task RequestMetadataDoesNotReplaceGatewayErrorHandling(string path, string mediaType, HttpStatusCode status, string message, bool messages)
    {
        await using var host = await StartAsync();
        using var content = new StringContent("{", Encoding.UTF8, mediaType);
        using var response = await host.Client.PostAsync(path, content);
        Assert.Equal(status, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(message, body.GetProperty("error").GetProperty("message").GetString());
        if (messages) Assert.Equal("error", body.GetProperty("type").GetString());
        Assert.Empty(host.Requests);
    }

    private static Task<FixtureHost> StartAsync() => FixtureHost.StartAsync(
        configure: services => services.AddOpenApi(GatewayOpenApi.Configure)
            .AddOpenApi(GatewayCapabilityOpenApi.DocumentName, GatewayCapabilityOpenApi.Configure),
        configureApp: app => app.MapOpenApi());

    private static JsonElement ResolveSchema(JsonElement root, JsonElement schema) => schema.TryGetProperty("$ref", out var reference)
        ? root.GetProperty("components").GetProperty("schemas").GetProperty(reference.GetString()!.Split('/')[^1])
        : schema;

    private static void AssertTag(JsonElement operation, string expected)
        => Assert.Equal(expected, Assert.Single(operation.GetProperty("tags").EnumerateArray()).GetString());

    private static void AssertPathParameters(JsonElement operation, params string[] names)
    {
        var parameters = operation.GetProperty("parameters").EnumerateArray().ToArray();
        Assert.Equal(names, parameters.Select(p => p.GetProperty("name").GetString()).Order(StringComparer.Ordinal));
        Assert.All(parameters, parameter =>
        {
            Assert.Equal("path", parameter.GetProperty("in").GetString());
            Assert.True(parameter.GetProperty("required").GetBoolean());
        });
    }
}
