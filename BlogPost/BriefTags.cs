using System.Text.RegularExpressions;

namespace WPAIPoster.BlogPost;

/// <summary>
/// Parses an optional author directive of the form <c>[TAGS: Agent, Workflow, MCP]</c> out of the
/// user's brief. The extracted tags become the highest-priority "UserProvided" signal for image
/// selection (see <see cref="WPAIPoster.Images.TagBasedImageSelector"/>); the directive itself is
/// stripped from the brief so it never leaks into the generated post. Pure and unit-tested.
/// </summary>
public static partial class BriefTags
{
    [GeneratedRegex(@"\[TAGS:\s*([^\]]*)\]", RegexOptions.IgnoreCase)]
    private static partial Regex TagsDirectiveRegex();

    [GeneratedRegex(@"[ \t]{2,}")]
    private static partial Regex ExtraSpacesRegex();

    /// <summary>
    /// Returns the distinct author tags from any <c>[TAGS: …]</c> directive(s) in
    /// <paramref name="brief"/> (comma-separated, trimmed, deduped case-insensitively in first-seen
    /// order) and the brief with those directives removed and surrounding whitespace collapsed.
    /// </summary>
    public static (IReadOnlyList<string> Tags, string Brief) Parse(string? brief)
    {
        if (string.IsNullOrWhiteSpace(brief))
            return (Array.Empty<string>(), brief ?? string.Empty);

        var tags = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in TagsDirectiveRegex().Matches(brief))
        {
            foreach (string raw in m.Groups[1].Value.Split(','))
            {
                string tag = raw.Trim();
                if (tag.Length > 0 && seen.Add(tag))
                    tags.Add(tag);
            }
        }

        string cleaned = TagsDirectiveRegex().Replace(brief, " ");
        cleaned = ExtraSpacesRegex().Replace(cleaned, " ").Trim();
        return (tags, cleaned);
    }
}
