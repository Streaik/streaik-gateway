using System.Text.Json.Nodes;
using Xunit;

namespace Streaik.Gateway.Tests;

public class VercelAttachmentTests
{
    [Theory]
    [InlineData("openai")]
    [InlineData("anthropic")]
    public async Task Api_chat_maps_attachments_to_one_native_user_message(string provider)
    {
        var request = GatewayTests.Request("/api/chat", $"{provider}/test", true);
        request["messages"] = JsonNode.Parse("""
            [{"id":"user_1","role":"user","parts":[
              {"type":"text","text":"File: notes.md\nConverted content"},
              {"type":"file","filename":"sample.pdf","mediaType":"application/pdf","url":"data:application/pdf;base64,JVBERg=="},
              {"type":"file","mediaType":"image/png","url":"https://files.example.test/picture.png"},
              {"type":"file","mediaType":"application/pdf","url":"https://files.example.test/sample.pdf"},
              {"type":"file","mediaType":"text/html","url":"https://example.test/wiki"}
            ]}]
            """);
        await using var host = await FixtureHost.StartAsync();
        using var response = await host.PostAsync("/api/chat", request.ToJsonString(), ($"X-{provider}-Key", "fixture-key"));
        await GatewayTests.Success(response);
        var body = Assert.Single(host.Requests).Body!;
        var message = Assert.Single(body[provider == "openai" ? "input" : "messages"]!.AsArray())!;
        var blocks = message["content"]!.AsArray();
        Assert.Equal("user", message["role"]!.GetValue<string>());
        Assert.Equal("File: notes.md\nConverted content", blocks[0]!["text"]!.GetValue<string>());
        Assert.Equal("https://example.test/wiki", blocks[4]!["text"]!.GetValue<string>().Split("Link: ")[1]);
        if (provider == "openai")
        {
            Assert.Equal(new[] { "input_text", "input_file", "input_image", "input_file", "input_text" },
                blocks.Select(block => block!["type"]!.GetValue<string>()));
            Assert.Equal("sample.pdf", blocks[1]!["filename"]!.GetValue<string>());
            Assert.Equal("data:application/pdf;base64,JVBERg==", blocks[1]!["file_data"]!.GetValue<string>());
            Assert.Equal("https://files.example.test/picture.png", blocks[2]!["image_url"]!.GetValue<string>());
            Assert.Equal("https://files.example.test/sample.pdf", blocks[3]!["file_url"]!.GetValue<string>());
        }
        else
        {
            Assert.Equal(new[] { "text", "document", "image", "document", "text" },
                blocks.Select(block => block!["type"]!.GetValue<string>()));
            Assert.Equal("sample.pdf", blocks[1]!["title"]!.GetValue<string>());
            Assert.Equal("JVBERg==", blocks[1]!["source"]!["data"]!.GetValue<string>());
            Assert.Equal("application/pdf", blocks[1]!["source"]!["media_type"]!.GetValue<string>());
            Assert.Equal("https://files.example.test/picture.png", blocks[2]!["source"]!["url"]!.GetValue<string>());
            Assert.Equal("https://files.example.test/sample.pdf", blocks[3]!["source"]!["url"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task Api_chat_bridge_preserves_native_Chat_Completions_image_and_file_parts()
    {
        var request = GatewayTests.Request("/api/chat", "deepseek/model", true);
        request["messages"] = JsonNode.Parse("""
            [{"id":"user_1","role":"user","parts":[
              {"type":"file","mediaType":"image/png","url":"data:image/png;base64,aW1hZ2U="},
              {"type":"file","filename":"sample.pdf","mediaType":"application/pdf","url":"data:application/pdf;base64,JVBERg=="},
              {"type":"file","mediaType":"application/pdf","url":"https://files.example.test/sample.pdf"}
            ]}]
            """);
        await using var host = await FixtureHost.StartAsync(upstream => FixtureHost.Reply(upstream with { Path = "/v1/chat/completions" }), compatibleChat: true);
        using var response = await host.PostAsync("/api/chat", request.ToJsonString(), ("X-DeepSeek-Key", "fixture-key"));
        await GatewayTests.Success(response);
        var body = Assert.Single(host.Requests).Body!;
        var parts = Assert.Single(body["messages"]!.AsArray())!["content"]!.AsArray();
        Assert.Equal(new[] { "image_url", "file", "text" }, parts.Select(part => part!["type"]!.GetValue<string>()));
        Assert.Equal("data:image/png;base64,aW1hZ2U=", parts[0]!["image_url"]!["url"]!.GetValue<string>());
        Assert.Equal("data:application/pdf;base64,JVBERg==", parts[1]!["file"]!["file_data"]!.GetValue<string>());
        Assert.Equal("sample.pdf", parts[1]!["file"]!["filename"]!.GetValue<string>());
        Assert.Equal("Link: https://files.example.test/sample.pdf", parts[2]!["text"]!.GetValue<string>().Trim());
    }

    [Fact]
    public async Task Responses_accepts_a_raw_Word_attachment_without_a_text_prompt()
    {
        const string data = "data:application/vnd.openxmlformats-officedocument.wordprocessingml.document;base64,UEs=";
        var request = GatewayTests.Request("/api/chat", "openai/test", true);
        request["messages"] = JsonNode.Parse($$"""
            [{"id":"user_1","role":"user","parts":[{"type":"file","filename":"sample.docx",
            "mediaType":"application/vnd.openxmlformats-officedocument.wordprocessingml.document","url":"{{data}}"}]}]
            """);
        await using var host = await FixtureHost.StartAsync();
        using var response = await host.PostAsync("/api/chat", request.ToJsonString(), ("X-OpenAI-Key", "fixture-key"));
        await GatewayTests.Success(response);
        var body = Assert.Single(host.Requests).Body!;
        var file = Assert.Single(Assert.Single(body["input"]!.AsArray())!["content"]!.AsArray())!;
        Assert.Equal("input_file", file["type"]!.GetValue<string>());
        Assert.Equal(data, file["file_data"]!.GetValue<string>());
    }
}
