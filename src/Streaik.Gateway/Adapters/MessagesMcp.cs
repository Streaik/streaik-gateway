using System.Net.Http.Headers;
using System.Text.Json;
using Streaik.Messages;
using Streaik.Tools;

namespace Streaik.Gateway;

internal static class MessagesMcp
{
    private const string Beta = "mcp-client-2025-11-20";
    internal const string ListingBeta = "mcp-client-2026-09-15";

    internal static bool CanReplayListings(IReadOnlyDictionary<string, string> headers) => HasBeta(headers, ListingBeta);

    private static bool HasBeta(IReadOnlyDictionary<string, string> headers, string beta)
        => headers.GetValueOrDefault("anthropic-beta")?.Split(',', StringSplitOptions.TrimEntries).Contains(beta, StringComparer.Ordinal) == true;

    internal static IReadOnlyDictionary<string, string> Add(McpToolDefinition[] servers, MessagesRequest request,
        IReadOnlyDictionary<string, string> headers)
    {
        var extra = request.AdditionalProperties ??= new(StringComparer.Ordinal);
        if (extra.Keys.Any(key => key.Equals("mcp_servers", StringComparison.OrdinalIgnoreCase))
            || extra.TryGetValue("tools", out var native) && native.EnumerateArray().Any(tool => tool.ValueKind == JsonValueKind.Object
                && tool.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.ValueEquals("mcp_toolset")))
            throw new ContractException("Named provider MCP tools cannot be combined with raw Anthropic MCP configuration.", 422);
        var labels = new HashSet<string>(StringComparer.Ordinal);
        List<Dictionary<string, object>> endpoints = [];
        List<JsonElement> toolsets = [];
        foreach (var server in servers)
        {
            if (!labels.Add(server.ServerLabel)) throw new ContractException("Provider MCP server labels must be distinct.");
            if (server.ServerDescription is not null || server.AllowedCallers is not null || server.RequireApproval is not (null or McpApprovalPolicy.Never)
                || server.AllowedTools is McpAllowedTools.Filter { Value.ReadOnly: not null })
                throw new ContractException("Anthropic Messages cannot represent this MCP description, caller, filter, or approval option.", 422);
            var token = server.Authorization;
            if (server.Headers is { Count: > 0 } supplied)
            {
                var header = supplied.First();
                if (supplied.Count != 1 || token is not null || !header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
                    || !AuthenticationHeaderValue.TryParse(header.Value, out var authorization)
                    || !authorization.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase)
                    || authorization.Parameter is not { Length: > 0 } bearer || bearer.Any(c => c <= ' ' || c >= '\u007f'))
                    throw new ContractException("Anthropic Messages supports only a Bearer token for provider MCP, not arbitrary MCP headers.", 422);
                token = bearer;
            }
            var endpoint = new Dictionary<string, object> { ["type"] = "url", ["name"] = server.ServerLabel, ["url"] = server.ServerUrl.AbsoluteUri };
            if (token is not null) endpoint["authorization_token"] = token;
            endpoints.Add(endpoint);
            var toolset = new Dictionary<string, object> { ["type"] = "mcp_toolset", ["mcp_server_name"] = server.ServerLabel };
            var names = server.AllowedTools switch { McpAllowedTools.Names namesOnly => namesOnly.Value, McpAllowedTools.Filter filter => filter.Value.ToolNames, _ => null };
            var defaults = new Dictionary<string, bool>();
            if (server.DeferLoading is { } defer) defaults["defer_loading"] = defer;
            if (names is not null)
            {
                defaults["enabled"] = false;
                toolset["configs"] = names.Distinct(StringComparer.Ordinal).ToDictionary(name => name, _ => new { enabled = true }, StringComparer.Ordinal);
            }
            if (defaults.Count != 0) toolset["default_config"] = defaults;
            toolsets.Add(JsonSerializer.SerializeToElement(toolset));
        }
        extra["mcp_servers"] = JsonSerializer.SerializeToElement(endpoints);
        AppendTools(request, toolsets);
        var existing = headers.GetValueOrDefault("anthropic-beta");
        var older = HasBeta(headers, Beta);
        var newer = CanReplayListings(headers);
        if (newer && older)
            return new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase)
            { ["anthropic-beta"] = string.Join(",", existing!.Split(',', StringSplitOptions.TrimEntries).Where(beta => beta != Beta)) };
        if (newer || older) return headers;
        return new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase)
        { ["anthropic-beta"] = string.IsNullOrWhiteSpace(existing) ? Beta : $"{existing},{Beta}" };
    }

    internal static IEnumerable<JsonElement> WireTools(MessagesRequest request) => request.AdditionalProperties?.TryGetValue("tools", out var raw) == true
        ? raw.EnumerateArray().ToArray()
        : (request.Tools ?? []).Select(tool => JsonSerializer.SerializeToElement(tool, MessagesJson.Default));

    internal static void AppendTools(MessagesRequest request, IEnumerable<JsonElement> additional)
    {
        var tools = WireTools(request).Concat(additional).ToArray();
        (request.AdditionalProperties ??= new(StringComparer.Ordinal))["tools"] = JsonSerializer.SerializeToElement(tools);
        request.Tools = null;
    }
}
