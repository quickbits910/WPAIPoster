using System.Text.Json.Serialization;

namespace WPAIPoster.BlogPost;

/// <summary>An internal-link suggestion produced by the model (pointing at a real existing post).</summary>
public sealed class InternalLink
{
    [JsonPropertyName("anchor")]
    public string Anchor { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;
}

/// <summary>
/// Strongly-typed result of generating a WordPress blog post. Deserialized from the JSON envelope
/// the model returns (see <c>Prompts/blog-post-prompt.json</c>).
/// </summary>
public sealed class BlogPostResult
{
    [JsonPropertyName("metaTitle")]
    public string MetaTitle { get; set; } = string.Empty;

    [JsonPropertyName("metaDescription")]
    public string MetaDescription { get; set; } = string.Empty;

    [JsonPropertyName("h1")]
    public string H1 { get; set; } = string.Empty;

    [JsonPropertyName("bodyHtml")]
    public string BodyHtml { get; set; } = string.Empty;

    [JsonPropertyName("imageThemes")]
    public List<string> ImageThemes { get; set; } = new();

    [JsonPropertyName("internalLinks")]
    public List<InternalLink> InternalLinks { get; set; } = new();

    [JsonPropertyName("cta")]
    public string Cta { get; set; } = string.Empty;
}
