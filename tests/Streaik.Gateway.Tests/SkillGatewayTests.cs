using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Streaik.Gateway.Tests;

public sealed class SkillGatewayTests
{
    internal const string Skill = """{"id":"skill_1","name":"daily-notes","description":"Summarize meeting notes.","created_at":1,"default_version":"1","latest_version":"2","object":"skill"}""";
    private const string Second = """{"id":"skill_2","name":"daily-brief","description":"Write a daily brief.","created_at":2,"default_version":"1","latest_version":"1","object":"skill"}""";

    [Fact]
    public async Task Lists_qualified_skills_and_versions_and_downloads_current_or_pinned_content()
    {
        var bytes = Zip();
        await using var host = await FixtureHost.StartAsync(request => request.Path switch
        {
            "/v1/skills" => FixtureHost.Json($$"""{"data":[{{Second}}],"last_id":"skill_2","has_more":true}"""),
            "/v1/skills?after=skill_2" => FixtureHost.Json($$"""{"data":[{{Skill}}],"has_more":false}"""),
            "/v1/skills/skill_1/versions" => FixtureHost.Json("""{"object":"list","data":[{"id":"sv_2","skill_id":"skill_1","version":"2","created_at":2,"name":"daily-notes","description":"Summarize notes."}]}"""),
            "/v1/skills/skill_1/content" or "/v1/skills/skill_1/versions/2/content" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) },
            _ => throw new InvalidOperationException(request.Path)
        });

        using var anonymous = await host.GetAsync("/v1/skills");
        Assert.Empty((await anonymous.Content.ReadFromJsonAsync<JsonNode>())!["data"]!.AsArray());
        Assert.Empty(host.Requests);
        using var page = await host.GetAsync("/v1/skills?limit=1&order=asc", ("X-OpenAI-Key", "skill-key"));
        var list = await page.Content.ReadFromJsonAsync<JsonNode>();
        Assert.Equal("openai/skill_1", list!["first_id"]!.GetValue<string>());
        Assert.True(list["has_more"]!.GetValue<bool>());
        using var next = await host.GetAsync("/v1/skills?limit=1&order=asc&after=openai%2Fskill_1", ("X-OpenAI-Key", "skill-key"));
        Assert.Equal("openai/skill_2", (await next.Content.ReadFromJsonAsync<JsonNode>())!["last_id"]!.GetValue<string>());
        using var versions = await host.GetAsync("/v1/skills/openai/skill_1/versions", ("X-OpenAI-Key", "skill-key"));
        var version = (await versions.Content.ReadFromJsonAsync<JsonNode>())!["data"]![0]!;
        Assert.Equal("openai/skill_1", version["skill_id"]!.GetValue<string>());
        Assert.Equal("skill.version", version["object"]!.GetValue<string>());
        foreach (var path in new[] { "/v1/skills/openai/skill_1/content", "/v1/skills/openai/skill_1/versions/2/content" })
        {
            using var file = await host.GetAsync(path, ("X-OpenAI-Key", "skill-key"));
            Assert.Equal(HttpStatusCode.OK, file.StatusCode);
            Assert.Equal("application/zip", file.Content.Headers.ContentType?.MediaType);
            Assert.Equal(bytes, await file.Content.ReadAsByteArrayAsync());
        }
        Assert.All(host.Requests, request => Assert.Equal("Bearer skill-key", request.Headers["Authorization"]));
    }

    [Fact]
    public async Task Byok_upload_accepts_only_one_destination_key_and_a_valid_archive()
    {
        await using var host = await FixtureHost.StartAsync(request => request.Path == "/v1/skills" && request.Method == "POST"
            ? FixtureHost.Json(Skill) : throw new InvalidOperationException(request.Path));
        using var targets = await host.GetAsync("/v1/skills/providers", ("x-openai-key", "skill-key"), ("x-anthropic-key", "unused"));
        var destination = await targets.Content.ReadFromJsonAsync<JsonNode>();
        Assert.Equal("provider-key", destination!["auth"]!.GetValue<string>());
        Assert.Equal("openai", Assert.Single(destination["providers"]!.AsArray())!.GetValue<string>());
        using var ambiguous = await host.PostFormAsync("/v1/skills", Form(), ("x-openai-key", "skill-key"), ("x-anthropic-key", "unused"));
        Assert.Equal(HttpStatusCode.BadRequest, ambiguous.StatusCode);
        using var bearer = await host.PostFormAsync("/v1/skills", Form(), ("Authorization", "Bearer decoy"));
        Assert.Equal(HttpStatusCode.BadRequest, bearer.StatusCode);
        using var unsupported = await host.PostFormAsync("/v1/skills", Form(), ("x-anthropic-key", "unused"));
        Assert.Equal(HttpStatusCode.NotFound, unsupported.StatusCode);
        using var unsafePath = await host.PostFormAsync("/v1/skills", Form(Zip("daily-notes/../outside.txt")), ("x-openai-key", "skill-key"));
        Assert.Equal(HttpStatusCode.BadRequest, unsafePath.StatusCode);
        using var broken = await host.PostFormAsync("/v1/skills", Form([1, 2, 3]), ("x-openai-key", "skill-key"));
        Assert.Equal(HttpStatusCode.BadRequest, broken.StatusCode);
        Assert.Empty(host.Requests);

        using var uploaded = await host.PostFormAsync("/v1/skills", Form(), ("x-openai-key", "skill-key"), ("Authorization", "Bearer decoy"));
        Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);
        Assert.Equal("openai/skill_1", (await uploaded.Content.ReadFromJsonAsync<JsonNode>())!["id"]!.GetValue<string>());
        var sent = Assert.Single(host.Requests);
        Assert.Equal("Bearer skill-key", sent.Headers["Authorization"]);
        Assert.Contains("name=files", sent.RawBody);
        Assert.Contains("filename=skill.zip", sent.RawBody);
        Assert.Contains("daily-notes/SKILL.md", sent.RawBody);
    }

    internal static MultipartFormDataContent Form(byte[]? bytes = null)
    {
        var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(bytes ?? Zip()), "files", "example.zip");
        return form;
    }

    [Fact]
    public async Task Authenticated_host_uses_selector_and_host_key_not_identity_or_byok_header()
    {
        const string scheme = "skill-fixture";
        await using var host = await FixtureHost.StartAsync(request => request.Path == "/v1/skills" && request.Method == "POST"
            ? FixtureHost.Json(Skill) : throw new InvalidOperationException(request.Path),
            configureHost: builder =>
            {
                builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Providers:openai:ApiKey"] = "server-skill-key" });
                builder.Services.AddAuthentication(scheme).AddScheme<AuthenticationSchemeOptions, CallerAuth>(scheme, null);
                builder.Services.AddAuthorizationBuilder().AddPolicy(scheme, policy => policy.RequireAuthenticatedUser());
            }, authorization: scheme);
        const string path = "/v1/skills";
        var authorization = ("Authorization", "Bearer fixture-caller");
        var selector = ("X-Streaik-Provider", "openai");
        using var missingIdentity = await host.PostFormAsync(path, Form(), selector);
        Assert.Equal(HttpStatusCode.Unauthorized, missingIdentity.StatusCode);
        using var missingDestination = await host.PostFormAsync(path, Form(), authorization, ("x-openai-key", "decoy"));
        Assert.Equal(HttpStatusCode.BadRequest, missingDestination.StatusCode);
        Assert.Empty(host.Requests);
        using var targets = await host.GetAsync(path + "/providers", authorization);
        Assert.Equal("provider-header", (await targets.Content.ReadFromJsonAsync<JsonNode>())!["auth"]!.GetValue<string>());
        using var upload = await host.PostFormAsync(path, Form(), authorization, selector, ("x-openai-key", "decoy"));
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        var sent = Assert.Single(host.Requests);
        Assert.Equal("Bearer server-skill-key", sent.Headers["Authorization"]);
        Assert.False(sent.Headers.ContainsKey("x-openai-key"));
        Assert.False(sent.Headers.ContainsKey("X-Streaik-Provider"));
    }

    private sealed class CallerAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
            => Task.FromResult(Request.Headers.Authorization == "Bearer fixture-caller"
                ? AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, "fixture-caller")], Scheme.Name)), Scheme.Name))
                : AuthenticateResult.NoResult());
    }

    private static byte[] Zip(string? extra = null)
    {
        using var data = new MemoryStream();
        using (var zip = new ZipArchive(data, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("daily-notes/SKILL.md").Open()))
                writer.Write("---\nname: daily-notes\ndescription: Summarize meeting notes.\n---\n\n# Notes\n");
            if (extra is not null) zip.CreateEntry(extra);
        }
        return data.ToArray();
    }
}
