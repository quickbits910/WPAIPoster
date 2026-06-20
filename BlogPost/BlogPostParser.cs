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

        // Models frequently emit string values (especially bodyHtml) containing unescaped quotes,
        // newlines, or control chars, which are invalid JSON. Try the response as-is first, then
        // fall back to a repaired copy that re-escapes those characters.
        BlogPostResult result =
            TryDeserialize(json, out BlogPostResult? parsed, out JsonException? firstError) && parsed is not null
                ? parsed
                : TryDeserialize(RepairJson(json), out BlogPostResult? repaired, out _) && repaired is not null
                    ? repaired
                    : throw new FormatException(
                        $"Could not parse the model response as a blog post: {firstError!.Message}", firstError);

        if (string.IsNullOrWhiteSpace(result.BodyHtml))
            throw new FormatException(
                "The model response contained no post body (looked for bodyHtml/body/content). " +
                "Raw response:\n" + Truncate(raw!, 2000));

        return result;
    }

    private static bool TryDeserialize(string json, out BlogPostResult? result, out JsonException? error)
    {
        error = null;
        result = null;
        try
        {
            result = JsonSerializer.Deserialize<BlogPostResult>(json, Options);
            if (result is null)
                return false;

            using JsonDocument doc = JsonDocument.Parse(json, DocOptions);
            Backfill(result, doc.RootElement);
            return true;
        }
        catch (JsonException ex)
        {
            error = ex;
            result = null;
            return false;
        }
    }

    /// <summary>
    /// Best-effort repair of JSON that string values broke: re-escapes unescaped double-quotes,
    /// raw newlines/tabs, and other control characters that appear inside string literals. Heuristic —
    /// a <c>"</c> is treated as the end of a string only when the next non-whitespace char is structural
    /// (<c>: , } ]</c>) or end-of-input; otherwise it is escaped as part of the content.
    /// </summary>
    internal static string RepairJson(string json)
    {
        var sb = new StringBuilder(json.Length + 16);
        bool inString = false;

        for (int i = 0; i < json.Length; i++)
        {
            char c = json[i];

            if (!inString)
            {
                sb.Append(c);
                if (c == '"') inString = true;
                continue;
            }

            switch (c)
            {
                case '\\':
                    char next = i + 1 < json.Length ? json[i + 1] : '\0';
                    if (IsValidEscape(next))
                    {
                        // Genuine escape sequence — copy backslash + escape char verbatim.
                        sb.Append(c).Append(next);
                        i++;
                    }
                    // Otherwise the backslash is a bogus escape the model invented (e.g. \' or \%);
                    // drop it and let the following char be processed normally on the next iteration.
                    break;
                case '"':
                    if (IsStringEnd(json, i))
                    {
                        sb.Append('"');
                        inString = false;
                    }
                    else
                    {
                        sb.Append("\\\"");
                    }
                    break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }

        return sb.ToString();
    }

    /// <summary>The characters that legally follow a backslash inside a JSON string.</summary>
    private static bool IsValidEscape(char c)
        => c is '"' or '\\' or '/' or 'b' or 'f' or 'n' or 'r' or 't' or 'u';

    /// <summary>True if the quote at <paramref name="quoteIndex"/> looks like a string terminator.</summary>
    private static bool IsStringEnd(string s, int quoteIndex)
    {
        for (int j = quoteIndex + 1; j < s.Length; j++)
        {
            char c = s[j];
            if (char.IsWhiteSpace(c)) continue;
            return c is ':' or ',' or '}' or ']';
        }
        return true; // end of input → must be the closing quote
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

        if (r.Tags.Count == 0)
        {
            List<string>? tags = FindStringList(root, "tags", "posttags", "tag");
            if (tags is { Count: > 0 })
                r.Tags = tags;
        }

        if (r.Categories.Count == 0)
        {
            List<string>? cats = FindStringList(root, "categories", "category", "cats");
            if (cats is { Count: > 0 })
                r.Categories = cats;
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
