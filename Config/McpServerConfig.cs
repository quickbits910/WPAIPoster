using System.Text.Json;
using System.Text.Json.Serialization;

namespace WPAIPoster.Config;

/// <summary>
/// One optional MCP server entry under <c>web-search-mcp</c>. The JSON key is used to locate the matching
/// research prompt file, e.g. key <c>duck-duck-go-search</c> loads <c>Prompts/duck-duck-go-search.json</c>.
/// </summary>
public sealed class McpServerConfig
{
    /// <summary>Transport hint. <c>sse</c> forces SSE; otherwise the client auto-detects streamable HTTP first.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    /// <summary>Remote MCP endpoint URL.</summary>
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    /// <summary>When false, this entry is ignored. Null defaults to enabled.</summary>
    [JsonPropertyName("enabled")]
    public bool? Enabled { get; set; }

    [JsonIgnore]
    public string? EffectiveUrl => Url?.Trim();

    [JsonIgnore]
    public bool EffectiveEnabled => Enabled ?? true;

    [JsonIgnore]
    public bool IsSseEndpoint =>
        string.Equals(Type?.Trim().ToLowerInvariant(), "sse")
        || (EffectiveUrl?.ToLowerInvariant().EndsWith("/sse") ?? false);
}
