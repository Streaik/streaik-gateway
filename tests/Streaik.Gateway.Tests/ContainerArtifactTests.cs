using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;

namespace Streaik.Gateway.Tests;

public sealed class ContainerArtifactTests
{
    private const string Responses = "/v1/responses";
    private const string Chat = "/api/chat";
    private const string Container = "/v1/containers/cntr_demo";
    private const string List = Container + "/files?limit=100&order=desc";
    private const string Cited = Container + "/files/cfile_demo/content";
    private static readonly (string, string) Key = ("X-OpenAI-Key", "caller-key");
    private const string Generated = """
        {"id":"resp_1","object":"response","model":"test","status":"completed","created_at":1700000000,
        "output":[{"type":"code_interpreter_call","id":"ci_1","container_id":"cntr_demo","code":"print(1)","outputs":null,"status":"completed"},
        {"type":"message","id":"msg_1","role":"assistant","status":"completed","content":[{"type":"output_text","text":"file","annotations":[{"type":"container_file_citation","container_id":"cntr_demo","file_id":"cfile_demo","filename":"report.pdf","start_index":0,"end_index":4}]}]}]}
        """;

    [Fact]
    public async Task Native_responses_embed_the_mcp_result_and_replay_is_credential_bound_across_hosts()
    {
        var keys = new EphemeralDataProtectionProvider();
        void Configure(IServiceCollection services) => services.AddStreaikReferences().AddSingleton<IDataProtectionProvider>(keys);
        string token;
        JsonArray output;
        await using (var first = await FixtureHost.StartAsync(Reply, Configure))
        {
            using var reply = await first.PostAsync(Responses, Prompt().ToJsonString(), Key);
            var result = JsonNode.Parse(await GatewayTests.Success(reply))!;
            output = result["output"]!.AsArray();
            Assert.Null(result["metadata"]!["streaik"]!["files"]);
            var call = Assert.Single(output, item => item?["type"]?.GetValue<string>() == "mcp_call")!;
            Assert.Equal("download_file", call["name"]!.GetValue<string>());
            Assert.Equal("streaik", call["server_label"]!.GetValue<string>());
            Assert.Equal("completed", call["status"]!.GetValue<string>());
            AssertPdf(ParseOutput(call));
            Assert.All(first.Requests, upstream => Assert.Equal("Bearer caller-key", upstream.Headers["Authorization"]));
            token = result["metadata"]!["streaik"]!["containers"]![0]!["reference"]!.GetValue<string>();
        }
        await using var fresh = await FixtureHost.StartAsync(Reply, Configure);
        var next = Prompt();
        next["input"] = output.DeepClone();
        next["input"]!.AsArray().Add(JsonNode.Parse("""{"type":"message","role":"user","content":"Continue"}"""));
        next["tools"]![0]!["streaik_container_ref"] = token;
        using var retired = await fresh.GetAsync("/api/files/openai/containers/retired", Key);
        Assert.Equal(HttpStatusCode.NotFound, retired.StatusCode);
        using var forged = await fresh.PostAsync(Responses, next.ToJsonString(), ("X-OpenAI-Key", "other"));
        Assert.Equal(HttpStatusCode.NotFound, forged.StatusCode);
        Assert.Empty(fresh.Requests);
        using var continued = await fresh.PostAsync(Responses, next.ToJsonString(), Key);
        await GatewayTests.Success(continued);
        var sent = Assert.Single(fresh.Requests, x => x.Path == Responses).Body!;
        Assert.Equal("cntr_demo", sent["tools"]![0]!["container"]!.GetValue<string>());
        Assert.Null(sent["tools"]![0]!["streaik_container_ref"]);
        Assert.DoesNotContain("download_file", sent.ToJsonString());
    }

    [Fact]
    public async Task Expired_container_requires_original_files_and_does_not_replay_stale_code()
    {
        var expired = false;
        var keys = new EphemeralDataProtectionProvider();
        await using var host = await FixtureHost.StartAsync(request => expired && request.Path == Container
            ? FixtureHost.Json("""{"status":"expired"}""") : Reply(request),
            s => s.AddStreaikReferences().AddSingleton<IDataProtectionProvider>(keys));
        using var first = await host.PostAsync(Responses, Prompt().ToJsonString(), Key);
        var result = JsonNode.Parse(await GatewayTests.Success(first))!;
        var request = Prompt();
        request["input"] = new JsonArray(result["output"]![0]!.DeepClone(), JsonNode.Parse("""{"type":"message","role":"user","content":"next"}"""));
        request["tools"]![0]!["streaik_container_ref"] = result["metadata"]!["streaik"]!["containers"]![0]!["reference"]!.DeepClone();
        expired = true;
        using var withoutFiles = await host.PostAsync(Responses, request.ToJsonString(), Key);
        Assert.Equal(HttpStatusCode.Gone, withoutFiles.StatusCode);
        Assert.Single(host.Requests, x => x.Path == Responses);
        request["tools"]![0]!["container"]!["file_ids"] = new JsonArray("file-explicit");
        using var withFiles = await host.PostAsync(Responses, request.ToJsonString(), Key);
        await GatewayTests.Success(withFiles);
        var sent = host.Requests.Last(x => x.Path == Responses).Body!;
        Assert.DoesNotContain("ci_1", sent.ToJsonString());
        Assert.Equal("file-explicit", sent["tools"]![0]!["container"]!["file_ids"]![0]!.GetValue<string>());
    }

    [Fact]
    public async Task Vercel_replays_signed_code_and_round_trips_explicit_container_attachment()
    {
        var keys = new EphemeralDataProtectionProvider();
        void Configure(IServiceCollection services) => services.AddStreaikReferences().AddSingleton<IDataProtectionProvider>(keys);
        var request = GatewayTests.Request(Chat, "openai/test", true);
        request["providerMetadata"] = JsonNode.Parse("""{"openai":{"tools":[{"type":"code_interpreter","container":{"type":"auto"}}]}}""");
        JsonObject interpreter, download, file;
        await using (var first = await FixtureHost.StartAsync(Reply, Configure))
        {
            using var response = await first.PostAsync(Chat, request.ToJsonString(), Key);
            var stream = await GatewayTests.Success(response);
            var frames = Frames(stream);
            var available = Assert.Single(frames, p => Is(p, "tool-output-available") && p["toolCallId"]!.GetValue<string>() == "ci_1");
            interpreter = new() { ["type"] = "dynamic-tool", ["toolName"] = "code_interpreter", ["toolCallId"] = "ci_1", ["providerExecuted"] = true,
                ["state"] = "output-available", ["resultProviderMetadata"] = available["providerMetadata"]!.DeepClone() };
            var input = Assert.Single(frames, p => Is(p, "tool-input-available") && p["toolName"]!.GetValue<string>() == "download_file");
            var output = Assert.Single(frames, p => Is(p, "tool-output-available") && JsonNode.DeepEquals(p["toolCallId"], input["toolCallId"]));
            Assert.True(input["providerExecuted"]!.GetValue<bool>());
            Assert.True(output["providerExecuted"]!.GetValue<bool>());
            Assert.True(Array.IndexOf(frames, input) < Array.IndexOf(frames, output));
            Assert.DoesNotContain(frames, p => Is(p, "file"));
            Assert.Equal("stop", frames.Last(p => Is(p, "finish"))["finishReason"]!.GetValue<string>());
            var result = output["output"]!;
            AssertPdf(result);
            var artifact = result["structuredContent"]!;
            file = new() { ["type"] = "file", ["url"] = artifact["uri"]!.DeepClone(),
                ["mediaType"] = artifact["media_type"]!.DeepClone(), ["filename"] = artifact["filename"]!.DeepClone(),
                ["providerReference"] = new JsonObject { ["openai"] = result["_meta"]!["streaik/gateway"]!["file_reference"]!.DeepClone() } };
            download = new() { ["type"] = "dynamic-tool", ["toolName"] = "download_file", ["toolCallId"] = input["toolCallId"]!.DeepClone(),
                ["providerExecuted"] = true, ["state"] = "output-available", ["input"] = input["input"]!.DeepClone(), ["output"] = result.DeepClone() };
        }
        request["messages"]!.AsArray().Add(new JsonObject { ["role"] = "assistant", ["id"] = "assistant", ["parts"] = new JsonArray(interpreter, download) });
        request["messages"]!.AsArray().Add(new JsonObject { ["role"] = "user", ["id"] = "user", ["parts"] = new JsonArray(file) });
        await using var fresh = await FixtureHost.StartAsync(Reply, Configure);
        using var wrongKey = await fresh.PostAsync(Chat, request.ToJsonString(), ("X-OpenAI-Key", "other"));
        Assert.Equal(HttpStatusCode.NotFound, wrongKey.StatusCode);
        Assert.Empty(fresh.Requests);
        using var next = await fresh.PostAsync(Chat, request.ToJsonString(), Key);
        await GatewayTests.Success(next);
        var body = Assert.Single(fresh.Requests, x => x.Path == Responses).Body!;
        Assert.Contains("data:application/pdf;base64,AQID", body.ToJsonString());
        Assert.Contains("ci_1", body.ToJsonString());
        Assert.DoesNotContain("download_file", body.ToJsonString());
        Assert.Equal("cntr_demo", body["tools"]![0]!["container"]!.GetValue<string>());
    }

    [Fact]
    public async Task Only_current_assistant_files_are_discovered_across_pages_without_duplicate_downloads()
    {
        await using var host = await FixtureHost.StartAsync(request => request.Path switch
        {
            List => FixtureHost.Json("""
                {"has_more":true,"last_id":"next page","data":[
                  {"id":"cfile_demo","path":"/mnt/data/duplicate.pdf","source":"assistant","created_at":1700000001},
                  {"id":"cfile_old","path":"old.pdf","source":"assistant","created_at":1699999999},
                  {"id":"cfile_upload","path":"upload.pdf","source":"user","created_at":1700000001},
                  {"id":"cfile_unknown","path":"unknown.pdf","source":"assistant"}]}
                """),
            List + "&after=next%20page" => FixtureHost.Json("""
                {"has_more":true,"last_id":"next page","data":[
                  {"id":"cfile_missing","path":"missing.txt","source":"assistant","created_at":1700000000},
                  {"id":"cfile_limited","path":"large.bin","source":"assistant","created_at":1700000000},
                  {"id":"cfile_text","path":"/mnt/data/notes.txt","source":"assistant","created_at":1700000000},
                  {"id":"cfile_image","path":"/mnt/data/plot.png","source":"assistant","created_at":1700000000}]}
                """),
            Container + "/files/cfile_text/content" => new(HttpStatusCode.OK) { Content = new StringContent("héllo") },
            Container + "/files/cfile_image/content" => Pdf(),
            Container + "/files/cfile_limited/content" => new(HttpStatusCode.OK)
                { Content = new ByteArrayContent([1]) { Headers = { ContentLength = 11 * 1024 * 1024 } } },
            _ => Reply(request)
        }, services => services.AddSingleton<TimeProvider>(new FixedClock()));
        using var reply = await host.PostAsync(Responses, Prompt().ToJsonString(), Key);
        var result = JsonNode.Parse(await GatewayTests.Success(reply))!;
        var calls = result["output"]!.AsArray().Where(p => p is not null && Is(p, "mcp_call")).Select(p => ParseOutput(p!)).ToArray();
        Assert.Equal(3, calls.Length);
        AssertPdf(calls[0]);
        Assert.Equal("notes.txt", calls[1]["structuredContent"]!["filename"]!.GetValue<string>());
        Assert.Equal("héllo", calls[1]["content"]![0]!["resource"]!["text"]!.GetValue<string>());
        Assert.Equal("image", calls[2]["content"]![0]!["type"]!.GetValue<string>());
        Assert.Equal("AQID", calls[2]["content"]![0]!["data"]!.GetValue<string>());
        Assert.Null(calls[0]["_meta"]!["streaik/gateway"]!["file_reference"]);
        Assert.Equal([Responses, Cited, List, List + "&after=next%20page", Container + "/files/cfile_missing/content",
            Container + "/files/cfile_limited/content", Container + "/files/cfile_text/content", Container + "/files/cfile_image/content"],
            host.Requests.Select(x => x.Path));
    }

    [Fact]
    public async Task Responses_stream_projects_standard_mcp_events_before_the_terminal_snapshot()
    {
        const string annotation = """
            {"type":"response.output_text.annotation.added","sequence_number":6,"output_index":1,"item_id":"msg_1","content_index":0,"annotation_index":0,"annotation":{"type":"container_file_citation","container_id":"cntr_demo","file_id":"cfile_demo","filename":"report.pdf"}}
            """;
        var terminal = JsonNode.Parse(Generated)!;
        terminal["output"]![1]!["content"]![0]!["annotations"] = new JsonArray();
        await using var host = await FixtureHost.StartAsync(request => request.Path == Responses
            ? FixtureHost.Sse(annotation, Terminal(terminal.ToJsonString(), 7)) : Reply(request));
        var prompt = Prompt();
        prompt["stream"] = true;
        using var reply = await host.PostAsync(Responses, prompt.ToJsonString(), Key);
        var frames = Frames(await GatewayTests.Success(reply));
        Assert.Equal(["response.output_text.annotation.added", "response.output_item.added", "response.mcp_call_arguments.done",
            "response.output_item.done", "response.completed"], frames.Select(p => p["type"]!.GetValue<string>()));
        Assert.Equal([6, 7, 8, 9, 10], frames.Select(p => p["sequence_number"]!.GetValue<int>()));
        Assert.Equal("in_progress", frames[1]["item"]!["status"]!.GetValue<string>());
        Assert.Equal(2, frames[3]["output_index"]!.GetValue<int>());
        var done = frames[3]["item"]!;
        Assert.Equal(done["id"]!.GetValue<string>(), frames[2]["item_id"]!.GetValue<string>());
        Assert.Equal(done["arguments"]!.GetValue<string>(), frames[2]["arguments"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(done, frames[4]["response"]!["output"]![2]));
        AssertPdf(ParseOutput(done));
        Assert.Single(host.Requests, x => x.Path == Cited);
    }

    [Fact]
    public async Task Cancelled_request_cancels_the_in_flight_inline_download()
    {
        using var stream = new BlockingDownload();
        await using var host = await FixtureHost.StartAsync(request => request.Path == Cited
            ? new(HttpStatusCode.OK) { Content = new StreamContent(stream) } : Reply(request));
        using var request = new HttpRequestMessage(HttpMethod.Post, Responses) { Content = JsonContent.Create(Prompt()) };
        request.Headers.Add(Key.Item1, Key.Item2);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var call = host.Client.SendAsync(request, cancellation.Token);
        await stream.Started.Task.WaitAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        await stream.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.DoesNotContain(host.Requests, r => r.Path == List);
    }

    private sealed class BlockingDownload : MemoryStream
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, cancellationToken); return 0; }
            finally { Cancelled.TrySetResult(); }
        }
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(1700000000);
    }

    private static HttpResponseMessage Reply(FixtureHost.UpstreamRequest request) => request.Path switch
    {
        Responses => request.Stream ? FixtureHost.Sse(Terminal(Generated)) : FixtureHost.Json(Generated),
        Cited => Pdf(),
        Container => FixtureHost.Json("""{"id":"cntr_demo","status":"active"}"""),
        List => FixtureHost.Json("""{"data":[],"has_more":false}"""),
        _ => new(HttpStatusCode.NotFound)
    };

    private static HttpResponseMessage Pdf() => new(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
    private static string Terminal(string response, int sequence = 1) =>
        $$"""{"type":"response.completed","sequence_number":{{sequence}},"response":{{response.ReplaceLineEndings("")}}}""";
    private static JsonNode[] Frames(string sse) => sse.Split('\n').Where(line => line.StartsWith("data: {", StringComparison.Ordinal))
        .Select(line => JsonNode.Parse(line[6..])!).ToArray();
    private static bool Is(JsonNode node, string type) => node["type"]?.GetValue<string>() == type;
    private static JsonNode ParseOutput(JsonNode node) => JsonNode.Parse(node["output"]!.GetValue<string>())!;

    private static void AssertPdf(JsonNode result)
    {
        var parsed = result.Deserialize<CallToolResult>();
        var resource = Assert.IsType<BlobResourceContents>(Assert.IsType<EmbeddedResourceBlock>(Assert.Single(parsed!.Content)).Resource);
        Assert.Equal("application/pdf", resource.MimeType);
        Assert.Equal("urn:openai:container-file:cntr_demo:cfile_demo", resource.Uri);
        Assert.Equal("AQID", result["content"]![0]!["resource"]!["blob"]!.GetValue<string>());
        Assert.Equal("report.pdf", result["structuredContent"]!["filename"]!.GetValue<string>());
    }

    private static JsonNode Prompt() => JsonNode.Parse("""{"model":"openai/test","input":"generate a report","tools":[{"type":"code_interpreter","container":{"type":"auto"}}]}""")!;
}
