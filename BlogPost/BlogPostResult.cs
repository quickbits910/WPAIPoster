using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WPAIPoster.BlogPost;

/// <summary>
/// A visual theme for the post's imagery: a short, taggable <see cref="Subject"/> (used for the
/// keyword/tag pre-filter) and a richer <see cref="Description"/> that disambiguates it for the vision
/// model (e.g. subject "network" → description "interconnected computer network, servers and cables").
/// </summary>
public sealed record ImageTheme(string Subject, string Description);

/// <summary>
/// Reads <c>imageThemes</c> tolerantly: each element may be a plain string (legacy: used as both subject
/// and description) or an object with <c>subject</c>/<c>description</c> (plus common synonyms). The
/// description falls back to the subject when missing, and vice versa.
/// </summary>
public sealed class ImageThemeListConverter : JsonConverter<List<ImageTheme>>
{
    public override List<ImageTheme> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var list = new List<ImageTheme>();
        if (reader.TokenType == JsonTokenType.Null)
            return list;
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            reader.Skip();
            return list;
        }

        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.String:
                    string s = (reader.GetString() ?? string.Empty).Trim();
                    if (s.Length > 0) list.Add(new ImageTheme(s, s));
                    break;

                case JsonTokenType.StartObject:
                    AddObject(ref reader, list);
                    break;

                default:
                    reader.Skip();
                    break;
            }
        }

        return list;
    }

    private static void AddObject(ref Utf8JsonReader reader, List<ImageTheme> list)
    {
        using JsonDocument doc = JsonDocument.ParseValue(ref reader);
        string? subject = null, description = null;

        foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.String)
                continue;

            string name = Normalize(prop.Name);
            string val = (prop.Value.GetString() ?? string.Empty).Trim();
            if (val.Length == 0)
                continue;

            if (subject is null && name is "subject" or "theme" or "name" or "keyword" or "noun")
                subject = val;
            else if (description is null && name is "description" or "desc" or "detail" or "details" or "phrase" or "context")
                description = val;
        }

        if (subject is null && description is null)
            return;

        subject ??= description!;
        description ??= subject;
        list.Add(new ImageTheme(subject, description));
    }

    private static string Normalize(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
            if (char.IsLetterOrDigit(c))
                sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    public override void Write(Utf8JsonWriter writer, List<ImageTheme> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (ImageTheme t in value)
        {
            writer.WriteStartObject();
            writer.WriteString("subject", t.Subject);
            writer.WriteString("description", t.Description);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }
}

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
    [JsonConverter(typeof(ImageThemeListConverter))]
    public List<ImageTheme> ImageThemes { get; set; } = new();

    [JsonPropertyName("internalLinks")]
    public List<InternalLink> InternalLinks { get; set; } = new();

    [JsonPropertyName("cta")]
    public string Cta { get; set; } = string.Empty;

    /// <summary>Post tags (WordPress <c>post_tag</c> terms). Capped to 5 when applied.</summary>
    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = new();

    /// <summary>Post categories (WordPress <c>category</c> terms). Defaults to the configured default when empty.</summary>
    [JsonPropertyName("categories")]
    public List<string> Categories { get; set; } = new();
}
