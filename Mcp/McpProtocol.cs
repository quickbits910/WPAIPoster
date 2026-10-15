using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WPAIPoster.Mcp;

public sealed class McpInitializeRequest
{
    [JsonPropertyName("jsonrpc")]
    public string Jsonrpc { get => "2.0"; }

    [JsonPropertyName("id")]
    public int Id { get => id; set => id = value; }

    [JsonPropertyName("method")]
    public string Method { get => "initialize"; }

    [JsonPropertyName("params")]
    public McpInitializeParams Params { get => inner; }

    private int id = 1;
    private McpInitializeParams inner = new();
}

public sealed class McpInitializeParams
{
    [JsonPropertyName("protocolVersion")]
    public string ProtocolVersion { get => "2025-06-18"; }

    [JsonPropertyName("clientInfo")]
    public McpClientInfo ClientInfo { get => clientInfo; }

    [JsonPropertyName("capabilities")]
    public McpClientCapabilities ClientCapabilities { get => capabilities; }

    private McpClientInfo clientInfo = new();
    private McpClientCapabilities capabilities = new();
}

public sealed class McpClientCapabilities
{
}

public sealed class McpClientInfo
{
    [JsonPropertyName("name")]
    public string Name { get => "WPAIPoster"; }

    [JsonPropertyName("version")]
    public string Version { get => "1.0"; }
}

public sealed class McpToolCallRequest
{
    [JsonPropertyName("jsonrpc")]
    public string Jsonrpc { get => "2.0"; }

    [JsonPropertyName("id")]
    public int Id { get => id; set => id = value; }

    [JsonPropertyName("method")]
    public string Method { get => "tools/call"; }

    [JsonPropertyName("params")]
    public McpToolCallParams Params { get => inner; }

    private int id;
    private McpToolCallParams inner = new();
}

public sealed class McpToolCallParams
{
    [JsonPropertyName("name")]
    public string Name { get => name; set => name = value; }

    [JsonPropertyName("arguments")]
    public McpFetchArguments Arguments { get => arguments; }

    private string name = "";
    private McpFetchArguments arguments = new();
}

public sealed class McpFetchArguments
{
    [JsonPropertyName("url")]
    public string Url { get => url; set => url = value; }

    [JsonPropertyName("max_length")]
    public int MaxLength { get => maxLength; set => maxLength = value; }

    private string url = "";
    private int maxLength;
}

public static class McpProtocol
{
    public const string ProtocolVersion = "2025-06-18";
    public const string FetchContentToolName = "fetch_content";

    private const int MaxBodyExcerpt = 500;

    public static string BuildInitializeRequest(int id = 1)
        => JsonSerializer.Serialize(new McpInitializeRequest { Id = id });

    public static string BuildToolCallRequest(int id, string toolName, string url, int maxLength)
    {
        var request = new McpToolCallRequest { Id = id };
        request.Params.Name = toolName;
        request.Params.Arguments.Url = url;
        request.Params.Arguments.MaxLength = maxLength;
        return JsonSerializer.Serialize(request);
    }

    public static bool IsSseEndpoint(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        string trimmed = url.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri))
            return uri.ToString().ToLowerInvariant().EndsWith("/sse");

        return trimmed.ToLowerInvariant().EndsWith("/sse");
    }

    public static bool ShouldFallbackToSse(int statusCode, string? endpoint)
    {
        if (IsSseEndpoint(endpoint))
            return true;

        return statusCode is 404 or 405 or 406 or 421 or 422;
    }

    public static bool LooksLikeSse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return false;

        string trimmed = body.TrimStart();
        return trimmed.StartsWith("event:")
            || trimmed.StartsWith("data:")
            || body.Contains("\ndata:")
            || body.Contains("\r\ndata:");
    }

    public static string? ParseSseResponse(string? sse, int expectedId)
    {
        if (string.IsNullOrWhiteSpace(sse))
            return null;

        string? fallback = null;
        foreach (string sseEvent in SplitSseEvents(sse))
        {
            string data = ExtractSseData(sseEvent);
            if (string.IsNullOrWhiteSpace(data))
                continue;

            fallback = data;
            try
            {
                using var doc = JsonDocument.Parse(data);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("id", out JsonElement id)
                    && id.ValueKind == JsonValueKind.Number
                    && id.GetInt32() == expectedId)
                {
                    return data;
                }
            }
            catch (JsonException)
            {
                // Keep the last non-empty data payload as a best-effort fallback.
            }
        }

        return fallback;
    }

    public static string? ParseJsonRpcError(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("error", out JsonElement error))
                return null;

            if (error.ValueKind == JsonValueKind.String)
                return error.GetString();

            if (error.ValueKind == JsonValueKind.Object)
            {
                if (error.TryGetProperty("message", out JsonElement message) && message.ValueKind == JsonValueKind.String)
                    return message.GetString();
                if (error.TryGetProperty("code", out JsonElement code) && code.ValueKind == JsonValueKind.Number)
                    return code.ToString();
            }
        }
        catch (JsonException)
        {
            // Not JSON — no structured JSON-RPC error to report.
        }

        return null;
    }

    public static string? ParseToolResultText(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("result", out JsonElement result))
                return null;

            return ExtractResultText(result);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string BuildHttpErrorMessage(int statusCode, string? reasonPhrase, string? body)
    {
        string reason = string.IsNullOrWhiteSpace(reasonPhrase)
            ? ((HttpStatusCode)statusCode).ToString()
            : reasonPhrase;
        string status = $"MCP server returned {statusCode} ({reason})";

        string? detail = ExtractHttpDetail(body);
        return string.IsNullOrEmpty(detail) ? status : $"{status}: {detail}";
    }

    private static List<string> SplitSseEvents(string sse)
        => sse.Replace("\r\n", "\n").Split("\n\n").Where(e => !string.IsNullOrWhiteSpace(e)).ToList();

    private static string ExtractSseData(string sseEvent)
    {
        var data = new List<string>();
        foreach (string line in sseEvent.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.StartsWith("data:"))
                data.Add(line[5..].TrimStart());
        }
        return string.Join("\n", data);
    }

    private static string? ExtractResultText(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
            return element.GetString();

        if (element.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (JsonElement item in element.EnumerateArray())
            {
                string? text = ExtractElementText(item);
                if (text is not null)
                    parts.Add(text);
            }
            return parts.Count == 0 ? null : string.Join("\n", parts);
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("text", out JsonElement text) && text.ValueKind == JsonValueKind.String)
                return text.GetString();

            if (element.TryGetProperty("content", out JsonElement content) && content.ValueKind == JsonValueKind.Array)
            {
                var parts = new List<string>();
                foreach (JsonElement item in content.EnumerateArray())
                {
                    string? part = ExtractElementText(item);
                    if (part is not null)
                        parts.Add(part);
                }
                return parts.Count == 0 ? null : string.Join("\n", parts);
            }
        }

        return null;
    }

    private static string? ExtractElementText(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
            return element.GetString();

        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty("text", out JsonElement text)
            && text.ValueKind == JsonValueKind.String)
            return text.GetString();

        return null;
    }

    private static string? ExtractHttpDetail(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("error", out JsonElement error))
            {
                if (error.ValueKind == JsonValueKind.String)
                    return error.GetString();
                if (error.ValueKind == JsonValueKind.Object &&
                    error.TryGetProperty("message", out JsonElement msg) && msg.ValueKind == JsonValueKind.String)
                    return msg.GetString();
            }
        }
        catch (JsonException)
        {
            // Not JSON — fall through to the raw excerpt.
        }

        string trimmed = body.Trim();
        return trimmed.Length <= MaxBodyExcerpt ? trimmed : trimmed[..MaxBodyExcerpt] + "…";
    }
}
