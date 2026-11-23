using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using Streaik.Providers;
using Streaik.Responses;
using Streaik.Responses.Output;
using Streaik.Responses.Streaming;

namespace Streaik.Gateway;

internal static class GatewayContainerArtifacts
{
    public const string ReplayKey = "streaik_container_ref";
    public const string ToolName = "download_file";
    private const string FilePurpose = "container-file";
    private const string ContainerPurpose = "container";
    private const string Namespace = "streaik";
    private const int ByteLimit = 10 * 1024 * 1024;
    private const int FileLimit = 16;
    private static readonly FileExtensionContentTypeProvider Types = new();
    private static readonly UTF8Encoding Utf8 = new(false, true);

    private sealed record Container(string ContainerId);
    private sealed record File(string ContainerId, string FileId, string Filename);

    public static bool Enabled(GatewayCall call) => call.Get<IContainerFilesProvider>() is not null
        && call.Context.RequestServices.GetService<ReferencesEnabled>() is not null;

    public static string SignContainer(GatewayCall call, string id) => new GatewayReferences(call, ContainerPurpose).Protect(new Container(id));

    public static async Task PrepareAsync(GatewayCall call, ResponseRequest request)
    {
        // Gateway output is for clients, never a real MCP invocation to send back to OpenAI.
        if (request.Input?.Items is { } history && history.Any(IsDownload))
            request.Input = new ResponseInput(history.Where(item => !IsDownload(item)).ToArray());
        if (!Enabled(call)) return;
        request.Metadata?.Remove(Namespace);
        if (request.AdditionalProperties?.ContainsKey("container") == true)
            throw new ContractException("Select Code Interpreter containers on the tool, not at the top level.");
        var input = request.Input?.Items;
        var code = input?.OfType<ResponseCodeInterpreterCallItem>().ToArray() ?? [];
        var tools = request.Tools?.Where(t => t.Type == "code_interpreter").ToArray() ?? [];
        if (tools.Length > 1) throw new ContractException("Only one Code Interpreter container can be selected.");
        var tool = tools.SingleOrDefault();
        var options = tool?.Extra;
        if (options?.Remove(ReplayKey, out var reference) != true)
        {
            if (code.Length > 0 || options?.TryGetValue("container", out var untrusted) == true && untrusted.ValueKind == JsonValueKind.String)
                throw new ContractException("Replaying Code Interpreter requires a Streaik container reference.");
            return;
        }
        if (reference.ValueKind != JsonValueKind.String) throw new ContractException("Invalid container reference.");
        var container = new GatewayReferences(call, ContainerPurpose).Read<Container>(reference.GetString()!);
        if (!Valid(container.ContainerId, "cntr_") || code.Any(item => item.ContainerId != container.ContainerId))
            throw new ContractException("Replay items do not belong to this container.", 422);
        if (await call.Require<IContainerFilesProvider>().IsContainerActiveAsync(container.ContainerId, call.Headers, call.Ct))
        {
            options["container"] = JsonSerializer.SerializeToElement(container.ContainerId);
            return;
        }

        // Only an explicitly supplied automatic container with reusable upstream Files API IDs
        // can rebuild an expired container. Never replay the old code against that fresh container.
        if (!options.TryGetValue("container", out var fallback) || fallback.ValueKind != JsonValueKind.Object
            || Text(fallback, "type") != "auto" || !fallback.TryGetProperty("file_ids", out var files) || files.ValueKind != JsonValueKind.Array
            || files.GetArrayLength() == 0 || files.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String || !Valid(x.GetString(), "file-")))
            throw new ContractException("The OpenAI container expired. Reattach original files or start a new turn without the expired reference.", 410);
        if (input is not null)
        {
            var surviving = input.Where(item => item is not ResponseCodeInterpreterCallItem).ToArray();
            foreach (var message in surviving.OfType<ResponseInputMessage>())
                foreach (var part in (message.Content.Parts ?? []).OfType<OutputTextPart>())
                    part.Annotations = part.Annotations.Where(x => x is not JsonElement annotation || Text(annotation, "type") != "container_file_citation").ToArray();
            request.Input = new ResponseInput(surviving);
        }
    }

    public static void AddResponseReferences(GatewayCall call, ResponseResult response)
    {
        if (!Enabled(call)) return;
        response.Metadata?.Remove(Namespace);
        var output = response.OutputItems.ToArray();
        var containers = CompletedContainers(output).Concat(output.OfType<ResponseInputMessage>().SelectMany(Citations).Select(f => f.ContainerId))
            .Distinct(StringComparer.Ordinal).ToArray();
        if (containers.Length == 0) return;
        (response.Metadata ??= [])[Namespace] = new
        {
            containers = containers.Select(id => new { container_id = id, reference = SignContainer(call, id) }).ToArray()
        };
    }

    private static bool IsDownload(ResponseInputItem item) => item is ResponseMcpCallItem { ServerLabel: Namespace, Name: ToolName };

    internal sealed record Download(string Id, JsonElement Input, CallToolResult Output)
    {
        public ResponseOutputMcpCall ToResponse(bool completed = true) => new()
        {
            Id = Id, Name = ToolName, ServerLabel = Namespace, Status = completed ? "completed" : "in_progress", Arguments = Input.GetRawText(),
            Output = completed ? JsonSerializer.Serialize(Output, JsonSerializerOptions.Web) : null
        };
    }

    internal sealed class Turn(GatewayCall call)
    {
        private readonly IContainerFilesProvider? provider = call.Get<IContainerFilesProvider>();
        private readonly long startedAt = (call.Context.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds();
        private readonly Dictionary<(string Container, string File), File> streamedCitations = [];

        public void Observe(ResponseStreamPart part)
        {
            if (provider is not null && streamedCitations.Count < FileLimit
                && part is ResponseOutputTextAnnotationAdded { Annotation: { Type: "container_file_citation", AdditionalProperties: { } data } }
                && Citation(data.GetValueOrDefault("container_id"), data.GetValueOrDefault("file_id"), data.GetValueOrDefault("filename")) is { } file)
                streamedCitations.TryAdd((file.ContainerId, file.FileId), file);
        }

        public async IAsyncEnumerable<Download> DownloadAsync(ResponseResult response)
        {
            if (provider is null) yield break;
            var output = response.OutputItems.ToArray();
            var seen = new HashSet<(string Container, string File)>();
            var remaining = ByteLimit;
            foreach (var file in output.OfType<ResponseInputMessage>().SelectMany(Citations).Concat(streamedCitations.Values))
            {
                if (!seen.Add((file.ContainerId, file.FileId))) continue;
                if (await TryDownloadAsync(file, remaining) is { } download)
                {
                    remaining -= download.Bytes;
                    yield return download.Tool;
                }
                if (seen.Count >= FileLimit || remaining == 0) yield break;
            }

            // A continued container may contain earlier outputs and user uploads. Only list
            // containers actually used by this response, and never infer a timestamp or source.
            foreach (var container in CompletedContainers(output).Distinct(StringComparer.Ordinal).Take(4))
            {
                await using var entries = provider.ListContainerFilesAsync(container, call.Headers, call.Ct).GetAsyncEnumerator(call.Ct);
                while (await TryMoveNextAsync(entries))
                {
                    var entry = entries.Current;
                    if (entry.Source != "assistant" || entry.CreatedAt is not { } created || created < startedAt || !ValidFileId(entry.Id)
                        || !seen.Add((container, entry.Id))) continue;
                    if (await TryDownloadAsync(new File(container, entry.Id, SafeFilename(entry.Path)), remaining) is { } download)
                    {
                        remaining -= download.Bytes;
                        yield return download.Tool;
                    }
                    if (seen.Count >= FileLimit || remaining == 0) yield break;
                }
            }
        }

        private async Task<bool> TryMoveNextAsync(IAsyncEnumerator<ContainerFileEntry> entries)
        {
            try { return await entries.MoveNextAsync(); }
            catch (Exception error) when (Unavailable(error)) { return false; }
        }

        private async Task<(Download Tool, int Bytes)?> TryDownloadAsync(File file, int limit)
        {
            byte[] bytes;
            try { bytes = await ReadBytesAsync(call, provider!, file, limit); }
            catch (Exception error) when (Unavailable(error)) { return null; }

            var media = MediaType(file.Filename);
            var uri = $"urn:openai:container-file:{file.ContainerId}:{file.FileId}";
            var input = JsonSerializer.SerializeToElement(new { container_id = file.ContainerId, file_id = file.FileId, filename = file.Filename });
            var metadata = new JsonObject { ["provider"] = call.ProviderId };
            if (CanAttach(media) && Enabled(call))
                metadata["file_reference"] = new GatewayReferences(call, FilePurpose).Protect(file);
            var result = new CallToolResult
            {
                Content = [Content(bytes, media, uri)],
                StructuredContent = JsonSerializer.SerializeToElement(new
                {
                    container_id = file.ContainerId, file_id = file.FileId, filename = file.Filename, media_type = media, uri, bytes = bytes.Length
                }),
                Meta = new JsonObject { ["streaik/gateway"] = metadata }
            };
            return (new Download($"mcp_{Guid.NewGuid():N}", input, result), bytes.Length);
        }

        private bool Unavailable(Exception error) => !call.Ct.IsCancellationRequested
            && error is HttpRequestException or IOException or JsonException or OperationCanceledException or ContractException or ProviderRequestException;
    }

    public static async Task<ResponseContentPart> ReplayFileAsync(GatewayCall call, string token)
    {
        if (!Enabled(call)) throw new ContractException("Unknown artifact.", 404);
        var file = new GatewayReferences(call, FilePurpose).Read<File>(token);
        var media = MediaType(file.Filename);
        if (!CanAttach(media)) throw new ContractException("This container file cannot be attached as model input.", 422);
        var bytes = await ReadBytesAsync(call, call.Require<IContainerFilesProvider>(), file, ByteLimit);
        var url = $"data:{media};base64,{Convert.ToBase64String(bytes)}";
        return media == "application/pdf" ? new InputFilePart { FileData = url, Filename = file.Filename } : new InputImagePart { ImageUrl = url };
    }

    private static async Task<byte[]> ReadBytesAsync(GatewayCall call, IContainerFilesProvider provider, File file, int limit)
    {
        using var download = await provider.DownloadContainerFileAsync(file.ContainerId, file.FileId, call.Headers, call.Ct);
        download.EnsureSuccessStatusCode();
        if (download.Content.Headers.ContentLength > limit) throw new ContractException("The container file exceeds the inline byte limit.", 413);
        await using var stream = await download.Content.ReadAsStreamAsync(call.Ct);
        using var output = new MemoryStream();
        var buffer = new byte[16384];
        while (await stream.ReadAsync(buffer, call.Ct) is > 0 and var count)
        {
            if (output.Length + count > limit) throw new ContractException("The container file exceeds the inline byte limit.", 413);
            output.Write(buffer, 0, count);
        }
        call.Ct.ThrowIfCancellationRequested();
        return output.ToArray();
    }

    private static ContentBlock Content(byte[] bytes, string media, string uri)
    {
        if (InlineImage(media)) return ImageContentBlock.FromBytes(bytes, media);
        if (media.StartsWith("text/", StringComparison.Ordinal) || media is "application/json" or "application/xml"
            || media.EndsWith("+json", StringComparison.Ordinal) || media.EndsWith("+xml", StringComparison.Ordinal))
        {
            try { return new EmbeddedResourceBlock { Resource = new TextResourceContents { Uri = uri, MimeType = media, Text = Utf8.GetString(bytes) } }; }
            catch (DecoderFallbackException) { /* Keep non-UTF-8 text lossless as a blob. */ }
        }
        return new EmbeddedResourceBlock { Resource = BlobResourceContents.FromBytes(bytes, uri, media) };
    }

    private static IEnumerable<string> CompletedContainers(IEnumerable<ResponseInputItem> items) => items.OfType<ResponseCodeInterpreterCallItem>()
        .Where(x => x.Status == "completed" && Valid(x.ContainerId, "cntr_")).Select(x => x.ContainerId!);

    private static IEnumerable<File> Citations(ResponseInputMessage message)
    {
        if (message.Role != ResponseRole.Assistant) yield break;
        foreach (var part in (message.Content.Parts ?? []).OfType<OutputTextPart>())
            foreach (var annotation in part.Annotations.OfType<JsonElement>())
                if (Text(annotation, "type") == "container_file_citation"
                    && Citation(Text(annotation, "container_id"), Text(annotation, "file_id"), Text(annotation, "filename")) is { } file)
                    yield return file;
    }

    private static File? Citation(JsonElement container, JsonElement file, JsonElement filename) => Citation(String(container), String(file), String(filename));
    private static File? Citation(string? container, string? file, string? filename) => Valid(container, "cntr_") && ValidFileId(file)
        ? new File(container!, file!, SafeFilename(filename)) : null;
    private static string? String(JsonElement element) => element.ValueKind == JsonValueKind.String ? element.GetString() : null;
    private static string? Text(JsonElement obj, string property) => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(property, out var val)
        && val.ValueKind == JsonValueKind.String ? val.GetString() : null;
    private static bool ValidFileId(string? id) => Valid(id, "cfile_") || Valid(id, "file-");
    private static bool Valid(string? id, string prefix) => id is not null && id.Length > prefix.Length && id.Length <= 160
        && id.StartsWith(prefix, StringComparison.Ordinal) && id.All(x => char.IsAsciiLetterOrDigit(x) || x is '_' or '-');
    private static string SafeFilename(string? original) => new string((original ?? "file").Split('/', '\\').Last().Where(c => !char.IsControl(c)).Take(150).ToArray()) is { Length: > 0 } name ? name : "file";
    private static string MediaType(string filename) => Types.TryGetContentType(filename, out var type) ? type : "application/octet-stream";
    private static bool InlineImage(string media) => media is "image/png" or "image/jpeg" or "image/gif" or "image/webp";
    private static bool CanAttach(string media) => media == "application/pdf" || InlineImage(media);
}
