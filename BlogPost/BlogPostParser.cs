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
    /// a <c>"</c> is treated as the end of a string based on the next non-whitespace char, but the test
    /// depends on whether the current string is an object <em>key</em> or a <em>value</em>: a key ends
    /// before <c>:</c>, a value ends before <c>, } ]</c>. Tracking this lets a value string legitimately
    /// contain a <c>":</c> sequence — e.g. the Gutenberg block attribute <c>{"ordered":true}</c> embedded
    /// in body HTML — without the inner quote being mistaken for the closing one.
    /// </summary>
    internal static string RepairJson(string json)
    {
        var sb = new StringBuilder(json.Length + 16);
        bool inString = false;
        bool stringIsValue = false;            // is the string we're inside a value (vs an object key)?
        var containerIsArray = new Stack<bool>(); // true = array, false = object
        char lastStructural = '\0';            // last non-whitespace char seen outside any string

        for (int i = 0; i < json.Length; i++)
        {
            char c = json[i];

            if (!inString)
            {
                // Drop / repair bare non-JSON garbage the model leaked into a structural position
                // (e.g. a stray "<em>," array element). Outside a string the only legal bare tokens
                // are the literals true/false/null and numbers; anything else is junk that would
                // otherwise sink the whole envelope even after every string value was repaired.
                if (!char.IsWhiteSpace(c) && c is not ('{' or '}' or '[' or ']' or ':' or ',' or '"'))
                {
                    int tokenEnd = i;
                    while (tokenEnd < json.Length)
                    {
                        char t = json[tokenEnd];
                        if (char.IsWhiteSpace(t) || t is '{' or '}' or '[' or ']' or ':' or ',' or '"') break;
                        tokenEnd++;
                    }
                    string token = json[i..tokenEnd];

                    if (IsJsonLiteral(token))
                    {
                        sb.Append(token);
                        lastStructural = token[^1];
                    }
                    else if (LastSignificant(sb) == ':')
                    {
                        // Garbage in object-value position — substitute null so the object stays well-formed.
                        sb.Append("null");
                        lastStructural = 'l';
                    }
                    else
                    {
                        // Garbage as an array element / value — drop it, and swallow a now-redundant
                        // following comma so the container isn't left with an empty element ([a, ,b]).
                        int j = tokenEnd;
                        while (j < json.Length && char.IsWhiteSpace(json[j])) j++;
                        if (j < json.Length && json[j] == ',')
                        {
                            tokenEnd = j + 1;
                            lastStructural = ',';
                        }
                    }

                    i = tokenEnd - 1;
                    continue;
                }

                sb.Append(c);
                switch (c)
                {
                    case '"':
                        inString = true;
                        // A string is an object key only when it opens directly after '{' or ','
                        // inside an object; everything else (after ':', array elements, top level)
                        // is a value.
                        bool inObject = containerIsArray.Count > 0 && !containerIsArray.Peek();
                        stringIsValue = !(inObject && (lastStructural is '{' or ','));
                        break;
                    case '{': containerIsArray.Push(false); lastStructural = c; break;
                    case '[': containerIsArray.Push(true); lastStructural = c; break;
                    case '}' or ']':
                        if (containerIsArray.Count > 0) containerIsArray.Pop();
                        lastStructural = c;
                        break;
                    default:
                        if (!char.IsWhiteSpace(c)) lastStructural = c;
                        break;
                }
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
                    // A key string always terminates at its first quote (model keys are fixed field
                    // names, never containing literal quotes); a value string ends only before ,}].
                    // Treating the key quote as the terminator is what lets the missing-colon repair
                    // below fire even though no ':' follows.
                    if (stringIsValue ? IsStringEnd(json, i, true) : true)
                    {
                        sb.Append('"');
                        inString = false;

                        // An object key must be followed by ':'. Some models drop it (and the value's
                        // opening quote too), e.g. "bodyHtml"<p>…</p>" instead of "bodyHtml": "<p>…</p>".
                        // Repair the missing colon — and, when the value is bare, synthesise its opening
                        // quote and resume in value-string mode so the rest is escaped/terminated normally.
                        if (!stringIsValue)
                        {
                            int k = i + 1;
                            while (k < json.Length && char.IsWhiteSpace(json[k])) k++;
                            if (k < json.Length && json[k] != ':')
                            {
                                char vc = json[k];
                                if (vc is '"' or '{' or '[' or '-' or 't' or 'f' or 'n' || char.IsDigit(vc))
                                {
                                    // Value is present and well-formed — only the colon was missing.
                                    sb.Append(':');
                                    lastStructural = ':';
                                }
                                else if (vc is '}' or ']' or ',')
                                {
                                    // Key with no value at all — substitute null so the object stays valid.
                                    sb.Append(": null");
                                    lastStructural = 'l';
                                }
                                else
                                {
                                    // Bare (unquoted) value, typically HTML beginning with '<' — open a
                                    // value string and resume scanning from the first value char.
                                    sb.Append(": \"");
                                    inString = true;
                                    stringIsValue = true;
                                    i = k - 1;
                                }
                            }
                        }
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

    /// <summary>The last non-whitespace char already written, or '\0' if none — used to tell whether
    /// dropped garbage sat in object-value position (after a <c>:</c>).</summary>
    private static char LastSignificant(StringBuilder sb)
    {
        for (int k = sb.Length - 1; k >= 0; k--)
            if (!char.IsWhiteSpace(sb[k])) return sb[k];
        return '\0';
    }

    /// <summary>True if a bare token outside any string is a legal JSON literal (true/false/null or a number).</summary>
    private static bool IsJsonLiteral(string token)
    {
        if (token is "true" or "false" or "null") return true;
        if (token.Length == 0 || token[0] is not ('-' or '+' or (>= '0' and <= '9'))) return false;
        return double.TryParse(token,
            System.Globalization.NumberStyles.Float | System.Globalization.NumberStyles.AllowLeadingSign,
            System.Globalization.CultureInfo.InvariantCulture, out _);
    }

    /// <summary>
    /// True if the quote at <paramref name="quoteIndex"/> looks like a string terminator. A value
    /// string ends before a <c>,</c> <c>}</c> or <c>]</c>; an object key ends before a <c>:</c>.
    /// </summary>
    private static bool IsStringEnd(string s, int quoteIndex, bool isValue)
    {
        for (int j = quoteIndex + 1; j < s.Length; j++)
        {
            char c = s[j];
            if (char.IsWhiteSpace(c)) continue;
            return isValue ? c is ',' or '}' or ']' : c is ':';
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
            // Fallback for alternate key names that arrive as a plain string list (the primary
            // "imageThemes" object/string form is handled by ImageThemeListConverter during deserialize).
            List<string>? themes = FindStringList(root, "themes", "imagekeywords", "keywords");
            if (themes is { Count: > 0 })
                r.ImageThemes = themes.Select(t => new ImageTheme(t, t)).ToList();
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
