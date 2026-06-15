using System.Text;
using System.Text.Json;

namespace WPAIPoster.BlogPost;

/// <summary>
/// Parses the JSON envelope returned by the model into a <see cref="BlogPostResult"/>.
/// Tolerant of common LLM noise: leading/trailing prose, ```json fenced code blocks, and field-name
/// variations (e.g. <c>body</c>/<c>content</c> instead of <c>bodyHtml</c>, or a body returned as an
/// array of lines). Throws if no post body can be found, surfacing the raw response.
/// </summary>
public static class BlogPostParser
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private static readonly JsonDocumentOptions DocOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    public static BlogPostResult Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            throw new FormatException("The model returned an empty response.");

        string json = ExtractJsonObject(raw);

        BlogPostResult result;
        try
        {
            result = JsonSerializer.Deserialize<BlogPostResult>(json, Options)
                     ?? throw new FormatException("The model response deserialized to null.");

            using JsonDocument doc = JsonDocument.Parse(json, DocOptions);
            Backfill(result, doc.RootElement);
        }
        catch (JsonException ex)
        {
            throw new FormatException($"Could not parse the model response as a blog post: {ex.Message}", ex);
        }

        if (string.IsNullOrWhiteSpace(result.BodyHtml))
            throw new FormatException(
                "The model response contained no post body (looked for bodyHtml/body/content). " +
                "Raw response:\n" + Truncate(raw!, 2000));

        return result;
    }

    /// <summary>Fills empty fields from alternately-named keys the model may have used.</summary>
    private static void Backfill(BlogPostResult r, JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return;

        if (string.IsNullOrWhiteSpace(r.BodyHtml))
            r.BodyHtml = FindText(root, "bodyhtml", "body", "bodycontent", "content",
                                        "postcontent", "html", "articlebody", "article") ?? r.BodyHtml;

        if (string.IsNullOrWhiteSpace(r.H1))
            r.H1 = FindText(root, "h1", "posttitle", "heading", "headline") ?? r.H1;

        if (string.IsNullOrWhiteSpace(r.Cta))
            r.Cta = FindText(root, "cta", "calltoaction") ?? r.Cta;

        if (r.ImageThemes.Count == 0)
        {
            List<string>? themes = FindStringList(root, "imagethemes", "themes", "imagekeywords", "keywords");
            if (themes is { Count: > 0 })
                r.ImageThemes = themes;
        }
    }

    /// <summary>Finds the first property whose normalized name matches, as a string or joined string array.</summary>
    private static string? FindText(JsonElement root, params string[] names)
    {
        var wanted = new HashSet<string>(names);
        foreach (JsonProperty prop in root.EnumerateObject())
        {
            if (!wanted.Contains(Normalize(prop.Name)))
                continue;

            switch (prop.Value.ValueKind)
            {
                case JsonValueKind.String:
                    string? s = prop.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) return s;
                    break;
                case JsonValueKind.Array:
                    string joined = string.Join("\n", prop.Value.EnumerateArray()
                        .Where(e => e.ValueKind == JsonValueKind.String)
                        .Select(e => e.GetString())
                        .Where(x => !string.IsNullOrEmpty(x)));
                    if (!string.IsNullOrWhiteSpace(joined)) return joined;
                    break;
            }
        }
        return null;
    }

    private static List<string>? FindStringList(JsonElement root, params string[] names)
    {
        var wanted = new HashSet<string>(names);
        foreach (JsonProperty prop in root.EnumerateObject())
        {
            if (wanted.Contains(Normalize(prop.Name)) && prop.Value.ValueKind == JsonValueKind.Array)
            {
                var list = prop.Value.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .ToList();
                if (list.Count > 0) return list;
            }
        }
        return null;
    }

    /// <summary>Lower-cases and strips non-alphanumerics so "body_html"/"Body HTML" → "bodyhtml".</summary>
    private static string Normalize(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
            if (char.IsLetterOrDigit(c))
                sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";

    /// <summary>Strips ```json fences and isolates the outermost JSON object in the text.</summary>
    internal static string ExtractJsonObject(string raw)
    {
        string text = raw.Trim();

        // Strip a fenced code block if present (```json ... ``` or ``` ... ```).
        if (text.StartsWith("```"))
        {
            int firstNewline = text.IndexOf('\n');
            if (firstNewline >= 0)
                text = text[(firstNewline + 1)..];
            int fenceEnd = text.LastIndexOf("```", StringComparison.Ordinal);
            if (fenceEnd >= 0)
                text = text[..fenceEnd];
            text = text.Trim();
        }

        int start = text.IndexOf('{');
        int end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
            throw new FormatException("No JSON object found in the model response.");

        return text[start..(end + 1)];
    }
}
