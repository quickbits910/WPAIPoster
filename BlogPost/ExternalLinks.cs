using System.Text.RegularExpressions;

namespace WPAIPoster.BlogPost;

/// <summary>
/// Post-processes a post body so links to <em>external</em> sites open in a new tab. "External" is
/// decided relative to the blog's own domain (derived from <c>wordPressFolder</c>, e.g. "abc.au"):
/// a link is internal if it is relative, an in-page anchor, or its host is the site domain (or a
/// subdomain of it). External http(s) links get <c>target='_blank' rel='noopener noreferrer'</c>.
/// Pure and unit-tested.
/// </summary>
public static partial class ExternalLinks
{
    [GeneratedRegex(@"<a\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex AnchorTagRegex();

    [GeneratedRegex(@"href\s*=\s*(?<q>[""'])(?<url>.*?)\k<q>", RegexOptions.IgnoreCase)]
    private static partial Regex HrefRegex();

    /// <summary>
    /// Adds <c>target='_blank' rel='noopener noreferrer'</c> to every external-site anchor in
    /// <paramref name="bodyHtml"/>. Anchors that already declare a <c>target</c> are left untouched.
    /// </summary>
    public static string MarkExternalLinksNewTab(string? bodyHtml, string? wordPressFolder)
    {
        if (string.IsNullOrEmpty(bodyHtml))
            return bodyHtml ?? string.Empty;

        string siteHost = SiteHost(wordPressFolder);

        return AnchorTagRegex().Replace(bodyHtml, match =>
        {
            string tag = match.Value;
            if (tag.Contains("target=", StringComparison.OrdinalIgnoreCase))
                return tag; // respect an explicit target the model already set

            Match href = HrefRegex().Match(tag);
            if (!href.Success || !IsExternalHref(href.Groups["url"].Value, siteHost))
                return tag;

            return string.Concat(tag.AsSpan(0, tag.Length - 1), " target='_blank' rel='noopener noreferrer'>");
        });
    }

    /// <summary>Reduces a <c>wordPressFolder</c> value to a bare host ("https://www.x.au/blog" → "x.au"). Empty when it isn't a domain.</summary>
    public static string SiteHost(string? wordPressFolder)
    {
        string s = (wordPressFolder ?? string.Empty).Trim();
        if (s.Length == 0)
            return string.Empty;

        int scheme = s.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
            s = s[(scheme + 3)..];

        int cut = s.IndexOfAny(new[] { '/', '?', '#' });
        if (cut >= 0)
            s = s[..cut];

        if (s.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
            s = s[4..];

        return s.ToLowerInvariant().TrimEnd('.');
    }

    /// <summary>True if <paramref name="href"/> is an absolute http(s) link to a host other than the site.</summary>
    public static bool IsExternalHref(string href, string siteHost)
    {
        string u = (href ?? string.Empty).Trim();

        // Only absolute http(s) links open in a new tab; relative paths, #anchors, mailto:, tel: stay in-tab.
        if (!u.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return false;

        string rest = u[(u.IndexOf("://", StringComparison.Ordinal) + 3)..];
        int cut = rest.IndexOfAny(new[] { '/', '?', '#' });
        string host = (cut >= 0 ? rest[..cut] : rest);

        int port = host.IndexOf(':');
        if (port >= 0)
            host = host[..port];
        if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
            host = host[4..];
        host = host.ToLowerInvariant();

        if (siteHost.Length == 0)
            return true; // site domain unknown → treat absolute links as external

        return host != siteHost && !host.EndsWith("." + siteHost, StringComparison.Ordinal);
    }
}
