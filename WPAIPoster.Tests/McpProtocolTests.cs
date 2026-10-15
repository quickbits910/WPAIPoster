using System.Text.Json;
using WPAIPoster.Mcp;

namespace WPAIPoster.Tests;

public class McpProtocolTests
{
    [Fact]
    public void BuildInitializeRequest_SerializesJsonRpcEnvelope()
    {
        string json = McpProtocol.BuildInitializeRequest(1);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("2.0", doc.RootElement.GetProperty("jsonrpc").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("id").GetInt32());
        Assert.Equal("initialize", doc.RootElement.GetProperty("method").GetString());
        Assert.Equal("2025-06-18", doc.RootElement.GetProperty("params").GetProperty("protocolVersion").GetString());
        Assert.Equal("WPAIPoster", doc.RootElement.GetProperty("params").GetProperty("clientInfo").GetProperty("name").GetString());
    }

    [Fact]
    public void BuildToolCallRequest_SerializesFetchContentArguments()
    {
        string json = McpProtocol.BuildToolCallRequest(2, McpProtocol.FetchContentToolName, "https://a.test/page", 8000);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("2.0", doc.RootElement.GetProperty("jsonrpc").GetString());
        Assert.Equal(2, doc.RootElement.GetProperty("id").GetInt32());
        Assert.Equal("tools/call", doc.RootElement.GetProperty("method").GetString());
        Assert.Equal("fetch_content", doc.RootElement.GetProperty("params").GetProperty("name").GetString());
        Assert.Equal("https://a.test/page", doc.RootElement.GetProperty("params").GetProperty("arguments").GetProperty("url").GetString());
        Assert.Equal(8000, doc.RootElement.GetProperty("params").GetProperty("arguments").GetProperty("max_length").GetInt32());
    }

    [Fact]
    public void ParseToolResultText_ReadsContentText()
    {
        string? text = McpProtocol.ParseToolResultText("""
            {
              "jsonrpc": "2.0",
              "id": 2,
              "result": {
                "content": [
                  { "type": "text", "text": "First page." },
                  { "type": "text", "text": "Second page." }
                ]
              }
            }
            """);

        Assert.Equal("First page.\nSecond page.", text);
    }

    [Fact]
    public void ParseToolResultText_ReadsDirectText()
    {
        string? text = McpProtocol.ParseToolResultText("""{ "result": { "text": "Direct." } }""");
        Assert.Equal("Direct.", text);
    }

    [Fact]
    public void ParseToolResultText_MissingResult_ReturnsNull()
    {
        Assert.Null(McpProtocol.ParseToolResultText("""{ "id": 2 }"""));
    }

    [Fact]
    public void ParseJsonRpcError_ReadsMessage()
    {
        string? error = McpProtocol.ParseJsonRpcError("""{ "error": { "code": -123, "message": "Tool failed" } }""");
        Assert.Equal("Tool failed", error);
    }

    [Fact]
    public void ParseJsonRpcError_NoError_ReturnsNull()
    {
        Assert.Null(McpProtocol.ParseJsonRpcError("""{ "result": { "content": [] } }"""));
    }

    [Fact]
    public void ParseSseResponse_FindsDataById()
    {
        string sse = """
            event: message
            data: {"jsonrpc":"2.0","id":1,"result":{}}

            event: message
            data: {"jsonrpc":"2.0","id":2,"result":{"content":[{"type":"text","text":"ok"}]}}
            """;

        string? data = McpProtocol.ParseSseResponse(sse, 2);
        Assert.Contains("\"id\":2", data);
        Assert.Contains("ok", data);
    }

    [Fact]
    public void ParseSseResponse_NoMatch_ReturnsLastData()
    {
        string sse = """
            data: {"jsonrpc":"2.0","id":1,"result":{}}

            data: {"jsonrpc":"2.0","id":3,"result":{}}
            """;

        string? data = McpProtocol.ParseSseResponse(sse, 2);
        Assert.Contains("\"id\":3", data);
    }

    [Fact]
    public void LooksLikeSse_DetectsEventStream()
    {
        Assert.True(McpProtocol.LooksLikeSse("event: message\ndata: {}"));
        Assert.False(McpProtocol.LooksLikeSse("""{ "jsonrpc": "2.0" }"""));
    }

    [Theory]
    [InlineData(404, "https://x.test/mcp", true)]
    [InlineData(405, "https://x.test/mcp", true)]
    [InlineData(406, "https://x.test/mcp", true)]
    [InlineData(421, "https://x.test/mcp", true)]
    [InlineData(422, "https://x.test/mcp", true)]
    [InlineData(200, "https://x.test/mcp", false)]
    [InlineData(500, "https://x.test/mcp", false)]
    [InlineData(500, "https://x.test/sse", true)]
    public void ShouldFallbackToSse_StatusAndEndpoint(int statusCode, string endpoint, bool expected)
    {
        Assert.Equal(expected, McpProtocol.ShouldFallbackToSse(statusCode, endpoint));
    }

    [Fact]
    public void BuildHttpErrorMessage_IncludesServerDetail()
    {
        string message = McpProtocol.BuildHttpErrorMessage(
            400, "Bad Request", """{ "error": { "message": "Tool unavailable" } }""");

        Assert.Contains("MCP server returned 400", message);
        Assert.Contains("Tool unavailable", message);
    }
}
