using System.Text;
using System.Text.RegularExpressions;

namespace WPAIPoster.BlogPost;

/// <summary>
/// Extracts URLs from the user's brief and guarantees they survive into the final post. The model is
/// asked to weave them in naturally; this provides the deterministic backstop — any brief URL the model
/// failed to include is appended to the body under a "Sources" heading. Pure and unit-tested.
/// </summary>
public static partial class BriefLinks
{
    [GeneratedRegex(@"https?://[^\s<>""'\)\]]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    /// <summary>Returns the distinct http(s) URLs found in <paramref name="brief"/>, in first-seen order.</summary>
    public static IReadOnlyList<string> ExtractUrls(string? brief)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(brief))
            return result;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in UrlRegex().Matches(brief))
        {
            string url = TrimTrailingPunctuation(m.Value);
            if (url.Length == 0)
                continue;
            if (seen.Add(Normalize(url)))
                result.Add(url);
        }
        return result;
    }

    /// <summary>Compact, human-readable anchor for a URL: scheme and leading "www." stripped, no trailing slash.</summary>
    public static string ReadableAnchor(string url)
    {
        string s = url;
        int scheme = s.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
            s = s[(scheme + 3)..];
        if (s.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
            s = s[4..];
        s = s.TrimEnd('/');
        return s.Length == 0 ? url : s;
    }

    /// <summary>
    /// Ensures every URL in <paramref name="urls"/> appears in <paramref name="bodyHtml"/>. Any that are
    /// missing are appended under an <c>&lt;h2&gt;Sources&lt;/h2&gt;</c> list. Returns the body unchanged
    /// when there are no URLs or all are already present.
    /// </summary>
    public static string EnsureLinksPresent(string? bodyHtml, IReadOnlyList<string> urls)
    {
        string body = bodyHtml ?? string.Empty;
        if (urls.Count == 0)
            return body;

        var missing = urls.Where(u => !ContainsUrl(body, u)).ToList();
        if (missing.Count == 0)
            return body;

        var sb = new StringBuilder(body.TrimEnd());
        sb.Append("\n<h2>Sources</h2>\n<ul>\n");
        foreach (string u in missing)
            sb.Append($"<li><a href='{Escape(u)}'>{Escape(ReadableAnchor(u))}</a></li>\n");
        sb.Append("</ul>\n");
        return sb.ToString();
    }

    private static bool ContainsUrl(string body, string url)
        => body.Contains(Normalize(url), StringComparison.OrdinalIgnoreCase);

    private static string TrimTrailingPunctuation(string url)
        => url.TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '}', '"', '\'');

    private static string Normalize(string url) => url.TrimEnd('/');

    private static string Escape(string s)
        => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
