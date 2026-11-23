using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Streaik.Gateway.Sample;

internal static class GatewayCapabilityOpenApi
{
    public const string DocumentName = "capabilities";

    private static readonly (string Name, string[] Routes)[] Capabilities =
    [
        ("Chat & Responses", ["/v1/chat/completions", "/v1/responses", "/v1/messages", "/api/chat"]),
        ("Decisions", ["/v1/decisions", "/api/decisions"]),
        ("Images", ["/v1/images", "/api/images"]),
        ("Videos", ["/api/videos"]),
        ("Speech", ["/v1/audio/speech", "/api/speech"]),
        ("Transcriptions", ["/v1/audio/transcriptions", "/api/transcriptions"]),
        ("Realtime & Live", ["/v1/realtime", "/v1/live", "/api/realtime"]),
        ("Embeddings", ["/v1/embeddings", "/api/embeddings"]),
        ("Rerank", ["/api/rerank"]),
        ("Model discovery", ["/v1/models"]),
        ("Skills", ["/v1/skills", "/v1/skills/providers", "/v1/skills/{providerId}/{skillId}/versions",
            "/v1/skills/{providerId}/{skillId}/content", "/v1/skills/{providerId}/{skillId}/versions/{version}/content"])
    ];

    public static void Configure(OpenApiOptions options)
    {
        GatewayOpenApi.Configure(options);
        options.AddDocumentTransformer((document, _, _) =>
        {
            GroupOperations(document);
            return Task.CompletedTask;
        });
    }

    private static void GroupOperations(OpenApiDocument document)
    {
        const string other = "Other";
        var pathsByCapability = document.Paths
            .Where(item => item.Value.Operations is { Count: > 0 })
            .ToLookup(item => Capabilities.FirstOrDefault(capability => capability.Routes.Any(route =>
                item.Key == route || item.Key.StartsWith(route + "/", StringComparison.Ordinal))).Name ?? other);

        var groups = new JsonArray();
        document.Tags = new HashSet<OpenApiTag>();
        foreach (var capability in Capabilities.Select(item => item.Name).Append(other))
        {
            var sectionNames = new JsonArray();
            var sections = pathsByCapability[capability].GroupBy(item => RoutePrefix(item.Key))
                .OrderBy(section => section.Key switch { "/v1" => 0, "/api" => 1, _ => 2 })
                .ThenBy(section => section.Key, StringComparer.Ordinal);
            foreach (var section in sections)
            {
                // Tag names are document-wide identifiers; Scalar displays only the route prefix.
                var name = $"{capability} {section.Key}";
                document.Tags.Add(new OpenApiTag
                {
                    Name = name,
                    Extensions = new Dictionary<string, IOpenApiExtension>
                    {
                        ["x-displayName"] = new JsonNodeExtension(JsonValue.Create(section.Key)!)
                    }
                });
                sectionNames.Add(name);
                foreach (var path in section)
                    foreach (var operation in path.Value.Operations!.Values)
                        operation.Tags = new HashSet<OpenApiTagReference> { new(name, document) };
            }

            if (sectionNames.Count > 0)
                groups.Add(new JsonObject { ["name"] = capability, ["tags"] = sectionNames });
        }

        (document.Extensions ??= new Dictionary<string, IOpenApiExtension>())["x-tagGroups"] = new JsonNodeExtension(groups);
    }

    private static string RoutePrefix(string path)
    {
        var separator = path.IndexOf('/', 1);
        return separator < 0 ? path : path[..separator];
    }
}
