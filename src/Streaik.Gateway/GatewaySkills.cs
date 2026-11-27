using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;
using Streaik.Providers;
using Streaik.Tools;

namespace Streaik.Gateway;

public static partial class StreaikGateway
{
    private const string UploadProviderHeader = "X-Streaik-Provider";

    private static void MapSkills(IEndpointRouteBuilder endpoints)
    {
        var skills = endpoints.MapGroup("/v1/skills").WithTags("OpenAI");
        skills.MapGet("", GatewayHandler(ListSkillsAsync)).Produces<GatewaySkillPage<ProviderSkill>>();
        skills.MapGet("/providers", GatewayHandler(ListSkillDestinationsAsync)).Produces<SkillDestinations>();
        skills.MapGet("/{providerId}/{skillId}/versions", GatewayHandler(ListSkillVersionsAsync)).Produces<GatewaySkillPage<ProviderSkillVersion>>();
        skills.MapGet("/{providerId}/{skillId}/content", GatewayHandler(DownloadSkillAsync)).Produces(200, contentType: "application/zip");
        skills.MapGet("/{providerId}/{skillId}/versions/{version}/content", GatewayHandler(DownloadSkillAsync)).Produces(200, contentType: "application/zip");
        skills.MapPost("", GatewayHandler(UploadSkillAsync))
            .WithMetadata(new GatewayRequestBodyMetadata(null, GatewayMultipartBody.SkillUpload)).Produces<ProviderSkill>();
    }

    private static IEnumerable<string> SkillProviderIds(HttpContext context)
        => context.RequestServices.GetServices<ProviderRegistration>().Select(x => x.Id);

    private static bool CredentialFreeSkill(HttpContext context, string id)
        => context.RequestServices.GetKeyedService<ILocalFixtureProvider>(id) is not null;

    private static bool HasSkillAccess(HttpContext context, string id)
        => CredentialFreeSkill(context, id) || !string.IsNullOrWhiteSpace(context.RequestServices.GetService<IProviderCredentials>()?.GetApiKey(id));

    private static string SkillRouteValue(HttpContext context, string name)
        => context.Request.RouteValues[name] is string value && ValidSkillSegment(value) ? value
            : throw new ContractException($"Invalid {name}.");

    private static bool ValidSkillSegment(string? value)
        => !string.IsNullOrWhiteSpace(value) && value.Length <= 512 && value is not ("." or "..")
            && !value.Any(c => char.IsControl(c) || c is '/' or '\\');

    private static (string ProviderId, string SkillId, ISkillReadProvider Reader) ResolveSkill(HttpContext context)
    {
        var id = SkillRouteValue(context, "providerId");
        var skillId = SkillRouteValue(context, "skillId");
        var reader = context.RequestServices.GetKeyedService<ISkillReadProvider>(id)
            ?? throw new ContractException("The provider does not support skill reads.", 404);
        context.Items[GatewayHeaderAuth.ActiveProvider] = id;
        return (id, skillId, reader);
    }

    private static async Task ListSkillsAsync(HttpContext context)
    {
        var query = SkillQuery(context);
        var ct = context.RequestAborted;
        var data = new List<ProviderSkill>();
        foreach (var id in SkillProviderIds(context))
        {
            if (!HasSkillAccess(context, id) || context.RequestServices.GetKeyedService<ISkillReadProvider>(id) is not { } reader) continue;
            try
            {
                await foreach (var skill in reader.ListSkillsAsync(ct).WithCancellation(ct))
                    if (ValidSkillSegment(skill.Id)) data.Add(skill with { Id = $"{id}/{skill.Id}" });
            }
            catch (Exception error) when (!ct.IsCancellationRequested && error is ProviderRequestException or HttpRequestException or JsonException or IOException or OperationCanceledException)
            {
                context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Streaik.Skills")
                    .LogWarning("Skill listing failed for {ProviderId}.", id);
            }
        }
        var sorted = SortSkills(data.DistinctBy(x => x.Id), x => x.CreatedAt, x => x.Id, query.Ascending);
        await WriteSkillsAsync(context, SkillPage(sorted, x => x.Id, query.After, query.Limit));
    }

    private static async Task ListSkillVersionsAsync(HttpContext context)
    {
        var query = SkillQuery(context);
        var (id, skillId, reader) = ResolveSkill(context);
        var ct = context.RequestAborted;
        var data = new List<ProviderSkillVersion>();
        await foreach (var version in reader.ListSkillVersionsAsync(skillId, ct).WithCancellation(ct))
            if (!string.IsNullOrWhiteSpace(version.Id) && ValidSkillSegment(version.Version))
                data.Add(version with { SkillId = $"{id}/{skillId}" });
        var sorted = SortSkills(data.DistinctBy(x => x.Id), x => x.CreatedAt, x => x.Id, query.Ascending);
        await WriteSkillsAsync(context, SkillPage(sorted, x => x.Id, query.After, query.Limit));
    }

    private static async Task DownloadSkillAsync(HttpContext context)
    {
        var (_, skillId, reader) = ResolveSkill(context);
        var version = context.Request.RouteValues.ContainsKey("version") ? SkillRouteValue(context, "version") : null;
        var archive = await reader.DownloadSkillAsync(skillId, version, context.RequestAborted);
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        await Results.File(archive, "application/zip", version is null ? $"{skillId}.zip" : $"{skillId}-{version}.zip").ExecuteAsync(context);
    }

    internal static async ValueTask<Stream> DownloadHostedSkillAsync(HttpContext context, SkillReference reference, CancellationToken ct)
    {
        if (reference.SkillId.Split('/') is not [var provider, var skill] || !ValidSkillSegment(provider) || !ValidSkillSegment(skill)
            || reference.Version is { } version && !ValidSkillSegment(version))
            throw new ContractException("A hosted skill reference needs a provider-qualified skill ID and an optional version.");
        var reader = context.RequestServices.GetKeyedService<ISkillReadProvider>(provider)
            ?? throw new ContractException("The provider does not support skill reads.", 404);
        // Keep the inference provider binding intact; cross-provider skill reads need that provider's scoped credential.
        if (!HasSkillAccess(context, provider)) throw new ProviderCredentialException(provider);
        var archive = await reader.DownloadSkillAsync(skill, reference.Version, ct);
        return new MemoryStream(archive, writable: false);
    }

    private static Task ListSkillDestinationsAsync(HttpContext context)
    {
        var byok = context.RequestServices.GetService<IProviderCredentials>() is IHeaderProviderCredentials;
        var destinations = byok || context.User.Identity?.IsAuthenticated == true
            ? SkillProviderIds(context).Where(id => HasSkillAccess(context, id) &&
                context.RequestServices.GetKeyedService<ISkillUploadProvider>(id) is not null).Order(StringComparer.Ordinal).ToArray()
            : [];
        return WriteSkillsAsync(context, new SkillDestinations(byok ? "provider-key" : "provider-header", destinations));
    }

    private static async Task UploadSkillAsync(HttpContext context)
    {
        var credentials = context.RequestServices.GetService<IProviderCredentials>();
        var headers = context.Request.Headers;
        var selector = headers[UploadProviderHeader];
        string id;
        if (credentials is IHeaderProviderCredentials)
        {
            var selected = SkillProviderIds(context).Where(p => headers.ContainsKey($"x-{p}-key")).ToArray();
            if (selector.Count == 1 && selector[0] is { } local && selected.Length == 0 && CredentialFreeSkill(context, local)) id = local;
            else
            {
                if (selector.Count != 0) throw new ContractException("Select an upload destination with its provider key header only.");
                if (selected.Length != 1) throw new ContractException("Send exactly one provider key header for this upload.");
                id = selected[0];
            }
        }
        else
        {
            if (context.User.Identity?.IsAuthenticated != true) throw new ContractException("Skill uploads require authentication.", 401);
            if (selector.Count != 1 || !SkillProviderIds(context).Contains(selector[0], StringComparer.Ordinal))
                throw new ContractException($"Select a registered provider with {UploadProviderHeader}.");
            id = selector[0]!;
        }

        var provider = context.RequestServices.GetKeyedService<ISkillUploadProvider>(id)
            ?? throw new ContractException("The selected provider does not accept skill uploads.", 404);
        if (!HasSkillAccess(context, id)) throw new ProviderCredentialException(id);
        // Don't mark an ActiveProvider: Authorization is never an upstream skill upload key.
        var archive = await ReadSkillUploadAsync(context.Request, context.RequestAborted);
        var skill = await provider.UploadSkillAsync(archive, context.RequestAborted);
        if (!ValidSkillSegment(skill.Id)) throw new ContractException("The provider returned an invalid skill ID.", 502);
        await WriteSkillsAsync(context, skill with { Id = $"{id}/{skill.Id}" });
    }

    private static async Task<byte[]> ReadSkillUploadAsync(HttpRequest request, CancellationToken ct)
    {
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var type) || !string.Equals(type.MediaType.Value, "multipart/form-data", StringComparison.OrdinalIgnoreCase))
            throw new ContractException("Expected multipart/form-data with a ZIP file in 'files'.", 415);
        var boundary = HeaderUtilities.RemoveQuotes(type.Boundary).Value;
        if (string.IsNullOrWhiteSpace(boundary) || boundary.Length > 70) throw new ContractException("Invalid multipart boundary.");
        if (request.ContentLength > SkillArchiveLimits.MaxBytes + 16384) throw new ContractException("Skill ZIP is too large.", 413);
        try
        {
            var reader = new MultipartReader(boundary, request.Body) { BodyLengthLimit = SkillArchiveLimits.MaxBytes + 1L };
            var part = await reader.ReadNextSectionAsync(ct);
            if (part is null || !ContentDispositionHeaderValue.TryParse(part.ContentDisposition, out var disposition)
                || !disposition.IsFileDisposition() || HeaderUtilities.RemoveQuotes(disposition.Name).Value != "files")
                throw new ContractException("Send exactly one ZIP file in 'files'.");
            using var data = new MemoryStream();
            var buffer = new byte[65536];
            for (int read; (read = await part.Body.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, SkillArchiveLimits.MaxBytes + 1L - data.Length)), ct)) != 0;)
            {
                if (data.Length + read > SkillArchiveLimits.MaxBytes) throw new ContractException("Skill ZIP is too large.", 413);
                data.Write(buffer, 0, read);
            }
            if (await reader.ReadNextSectionAsync(ct) is not null) throw new ContractException("Send exactly one ZIP file in 'files'.");
            using var zip = new ZipArchive(data, ZipArchiveMode.Read, leaveOpen: true);
            ValidateSkillZip(zip);
            return data.ToArray();
        }
        catch (Exception error) when (error is IOException or InvalidDataException) { throw new ContractException("Invalid skill ZIP or multipart upload."); }
    }

    private static void ValidateSkillZip(ZipArchive zip)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? root = null;
        var manifests = 0;
        var expanded = 0L;
        foreach (var entry in zip.Entries)
        {
            var path = entry.FullName.EndsWith('/') ? entry.FullName[..^1] : entry.FullName;
            var segments = path.Split('/');
            if (path.Length > 512 || segments.Any(part => !ValidSkillSegment(part) || part.Contains(':'))
                || !names.Add(path) || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new ContractException("The skill ZIP contains an unsafe or duplicate path.");
            root ??= segments[0];
            if (root != segments[0] || (segments.Length == 1 && !entry.FullName.EndsWith('/')))
                throw new ContractException("The ZIP must contain a single skill folder.");
            if (segments.Length == 2 && segments[1] == "SKILL.md" && entry.Length > 0) manifests++;
            if (names.Count > SkillArchiveLimits.MaxFiles * 2 || entry.Length > SkillArchiveLimits.MaxBytes - expanded)
                throw new ContractException("Expanded skill ZIP exceeds the archive limits.", 413);
            expanded += entry.Length;
        }
        if (manifests != 1) throw new ContractException("The skill folder must contain SKILL.md.");
    }

    private static (bool Ascending, string? After, int? Limit) SkillQuery(HttpContext context)
    {
        var query = context.Request.Query;
        int? limit = null;
        if (query.TryGetValue("limit", out var values))
        {
            if (values.Count != 1 || !int.TryParse(values[0], out var number) || number < 1)
                throw new ContractException("'limit' must be a positive integer.");
            limit = number;
        }
        return (string.Equals(query["order"].ToString(), "asc", StringComparison.OrdinalIgnoreCase), query["after"].FirstOrDefault(), limit);
    }

    private static IOrderedEnumerable<T> SortSkills<T>(IEnumerable<T> items, Func<T, long?> date, Func<T, string> id, bool asc)
        => (asc ? items.OrderBy(date) : items.OrderByDescending(date)).ThenBy(id, StringComparer.Ordinal);

    private static GatewaySkillPage<T> SkillPage<T>(IEnumerable<T> sorted, Func<T, string> id, string? after, int? limit)
    {
        var all = sorted.ToList();
        var offset = after is null ? 0 : all.FindIndex(x => id(x) == after) + 1;
        var page = all.Skip(offset).Take(limit ?? all.Count).ToArray();
        return new(page, page.Length == 0 ? "" : id(page[0]), page.Length == 0 ? "" : id(page[^1]), offset + page.Length < all.Count);
    }

    private static Task WriteSkillsAsync<T>(HttpContext context, T data)
    {
        context.Response.Headers.CacheControl = "no-store";
        return context.Response.WriteAsJsonAsync(data, JsonSerializerOptions.Web, context.RequestAborted);
    }

    private sealed record GatewaySkillPage<T>(T[] Data,
        [property: JsonPropertyName("first_id")] string FirstId,
        [property: JsonPropertyName("last_id")] string LastId,
        [property: JsonPropertyName("has_more")] bool HasMore)
    {
        public string Object => "list";
    }

    private sealed record SkillDestinations(string Auth, string[] Providers);
}
