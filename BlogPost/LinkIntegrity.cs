using System.Text;
using System.Text.RegularExpressions;

namespace WPAIPoster.BlogPost;

/// <summary>A single link the integrity guard changed: repaired to a known-good URL, or removed
/// (the anchor unwrapped to plain text). <see cref="NewHref"/> is null for a removal.</summary>
public sealed record LinkFix(string OriginalHref, string? NewHref)
{
    public bool IsRemoval => NewHref is null;
}

/// <summary>
/// Validates every <c>&lt;a href&gt;</c> in the generated body against the set of URLs we actually
/// handed the model (existing-post URLs + brief source links), and repairs the corruption models
/// routinely introduce into URLs they were given verbatim. Two failure modes seen in the wild:
/// a host mangled with injected/duplicated characters (e.g. <c>lukeos Osborne.au</c> for
/// <c>lukeosborne.au</c>) and a wrong TLD (e.g. <c>huggingface.com</c> for <c>huggingface.co</c>).
///
/// Strategy, per http(s) anchor (relative/anchor/mailto links are left untouched):
/// <list type="number">
/// <item>Exact match (ignoring scheme, <c>www.</c>, case, trailing slash) to a known URL → canonicalise
///   to the authoritative known-good string.</item>
/// <item>Otherwise, if the anchor's <em>path</em> exactly equals a known URL's path and the hosts are a
///   near-match (small edit distance), it is a corrupted copy of that URL → repair the whole href.</item>
/// <item>Otherwise, if the href is malformed (embedded whitespace, unparseable, or no host) it is a dead
///   link the model invented → unwrap the anchor to its inner text.</item>
/// <item>Otherwise (a well-formed URL we can't disprove) → leave it alone.</item>
/// </list>
/// Pure and unit-tested. Run before <see cref="BriefLinks.EnsureLinksPresent"/> so a repaired inline
/// brief link is recognised as present and not duplicated into a Sources section.
/// </summary>
public static partial class LinkIntegrity
{
    [GeneratedRegex(@"<a\b(?<attrs>[^>]*)>(?<inner>.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex AnchorElementRegex();

    [GeneratedRegex(@"href\s*=\s*(?<q>[""'])(?<url>.*?)\k<q>", RegexOptions.IgnoreCase)]
    private static partial Regex HrefRegex();

    private sealed record Known(string Raw, string Norm, string Host, string Path);

    /// <summary>
    /// Returns <paramref name="bodyHtml"/> with corrupted web links repaired against
    /// <paramref name="knownUrls"/> and irreparable dead links unwrapped. <paramref name="fixes"/>
    /// lists every change made (empty when nothing changed).
    /// </summary>
    public static string Guard(string? bodyHtml, IEnumerable<string> knownUrls, out IReadOnlyList<LinkFix> fixes)
    {
        var applied = new List<LinkFix>();
        fixes = applied;

        string body = bodyHtml ?? string.Empty;
        if (body.Length == 0)
            return body;

        var known = new List<Known>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string url in knownUrls)
        {
            if (string.IsNullOrWhiteSpace(url))
                continue;
            string norm = Normalize(url);
            if (norm.Length == 0 || !seen.Add(norm))
                continue;
            (string host, string path) = SplitHostPath(norm);
            known.Add(new Known(url.Trim(), norm, host, path));
        }

        return AnchorElementRegex().Replace(body, match =>
        {
            string attrs = match.Groups["attrs"].Value;
            Match href = HrefRegex().Match(attrs);
            if (!href.Success)
                return match.Value;

            string original = href.Groups["url"].Value;
            if (!IsWebLink(original))
                return match.Value; // relative, #anchor, mailto:, tel: — not our concern

            string? repaired = FindRepair(original, known);
            if (repaired is not null)
            {
                if (string.Equals(repaired, original.Trim(), StringComparison.Ordinal))
                    return match.Value; // already exactly the known-good form

                applied.Add(new LinkFix(original, repaired));
                char q = href.Groups["q"].Value[0];
                string newAttrs = attrs.Remove(href.Index, href.Length)
                    .Insert(href.Index, $"href={q}{repaired}{q}");
                return $"<a{newAttrs}>{match.Groups["inner"].Value}</a>";
            }

            if (IsMalformed(original))
            {
                // A broken link matching nothing we gave the model — drop the anchor, keep the text.
                applied.Add(new LinkFix(original, null));
                return match.Groups["inner"].Value;
            }

            return match.Value; // well-formed, unknown — can't prove it's wrong, so leave it
        });
    }

    /// <summary>Returns the known-good URL this href should be, or null if it matches none.</summary>
    private static string? FindRepair(string href, IReadOnlyList<Known> known)
    {
        string norm = Normalize(href);

        foreach (Known k in known)
            if (k.Norm == norm)
                return k.Raw; // exact match modulo scheme/www/case/trailing-slash

        (string host, string path) = SplitHostPath(norm);
        if (path.Length <= 1) // no distinctive path to match on ("" or "/")
            return null;

        Known? best = null;
        int bestDistance = int.MaxValue;
        foreach (Known k in known)
        {
            if (k.Path.Length <= 1 || !string.Equals(k.Path, path, StringComparison.Ordinal))
                continue; // paths must match exactly — the strong signal that it's the same link

            int distance = Levenshtein(host, k.Host);
            int tolerance = Math.Max(3, (int)Math.Ceiling(0.34 * Math.Max(host.Length, k.Host.Length)));
            if (distance <= tolerance && distance < bestDistance)
            {
                best = k;
                bestDistance = distance;
            }
        }

        return best?.Raw;
    }

    /// <summary>True for an absolute http(s) link — the only kind this guard inspects.</summary>
    private static bool IsWebLink(string href)
    {
        string h = href.TrimStart();
        return h.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || h.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True if a web link is structurally broken: embedded whitespace, unparseable, or no host.</summary>
    private static bool IsMalformed(string href)
    {
        string h = href.Trim();
        if (h.Any(char.IsWhiteSpace))
            return true;
        return !Uri.TryCreate(h, UriKind.Absolute, out Uri? uri) || string.IsNullOrEmpty(uri.Host);
    }

    /// <summary>Lower-cases and strips scheme, leading <c>www.</c>, all whitespace, and the trailing slash.</summary>
    private static string Normalize(string url)
    {
        string s = url.Trim();
        int scheme = s.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
            s = s[(scheme + 3)..];
        if (s.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
            s = s[4..];

        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
            if (!char.IsWhiteSpace(c))
                sb.Append(char.ToLowerInvariant(c));

        return sb.ToString().TrimEnd('/');
    }

    /// <summary>Splits a normalized URL into its host and its path (the path retains its leading slash, "" if none).</summary>
    private static (string Host, string Path) SplitHostPath(string normalized)
    {
        int slash = normalized.IndexOf('/');
        return slash >= 0
            ? (normalized[..slash], normalized[slash..])
            : (normalized, string.Empty);
    }

    /// <summary>Standard iterative Levenshtein edit distance between two strings.</summary>
    private static int Levenshtein(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++)
            prev[j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(prev[j] + 1, curr[j - 1] + 1), prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }

        return prev[b.Length];
    }
}
