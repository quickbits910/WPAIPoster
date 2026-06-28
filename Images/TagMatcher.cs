using System.Text;
using System.Text.RegularExpressions;

namespace WPAIPoster.Images;

/// <summary>
/// Pure, fast local pre-filter that ranks tagged images by how well their tags match the post content.
/// Tokenizes title + body + themes (HTML stripped, stopwords/short tokens dropped) and scores each image
/// by the number of its tags that flexibly match a content token (case-insensitive, substring-either-way,
/// or shared stem). Used to bound the candidate set before the model/vision stages.
/// </summary>
public static partial class TagMatcher
{
    [GeneratedRegex("<[^>]+>")]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonWordRegex();

    /// <summary>Builds the distinct lower-cased content tokens (stopwords and tokens &lt;3 chars removed).</summary>
    public static IReadOnlyCollection<string> Tokenize(string? title, string? bodyHtml, IEnumerable<string>? themes)
    {
        var sb = new StringBuilder();
        sb.Append(title).Append(' ');
        sb.Append(HtmlTagRegex().Replace(bodyHtml ?? string.Empty, " ")).Append(' ');
        if (themes is not null)
            sb.Append(string.Join(' ', themes));

        return SplitTokens(sb.ToString());
    }

    /// <summary>Tokenizes a set of short phrases (e.g. tags/categories) the same way as <see cref="Tokenize"/>.</summary>
    public static IReadOnlyCollection<string> TokenizeWords(IEnumerable<string>? phrases)
        => phrases is null ? new HashSet<string>(StringComparer.Ordinal) : SplitTokens(string.Join(' ', phrases));

    private static IReadOnlyCollection<string> SplitTokens(string text)
        => NonWordRegex().Split(text.ToLowerInvariant())
            .Where(t => t.Length >= 3 && !StopWords.IsStopWord(t))
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>A group of content tokens carrying a relative importance <paramref name="Weight"/>.</summary>
    public sealed record WeightedTokens(IReadOnlyCollection<string> Tokens, int Weight);

    /// <summary>Ranks images by match score (desc), then newest, returning the top <paramref name="limit"/>.</summary>
    public static IReadOnlyList<TaggedImage> Rank(
        ImageTagCatalog catalog, IReadOnlyCollection<string> tokens, int limit)
    {
        if (tokens.Count == 0)
            return Array.Empty<TaggedImage>();

        var scored = new List<(TaggedImage Img, int Score)>();
        foreach (TaggedImage img in catalog.Images)
        {
            if (img.Tags.Count == 0)
                continue;
            int score = img.Tags.Count(tag => TagMatchesAny(tag, tokens));
            if (score > 0)
                scored.Add((img, score));
        }

        return scored
            .OrderByDescending(s => s.Score)
            .ThenByDescending(s => s.Img.ModifiedUtc)
            .Take(Math.Max(0, limit))
            .Select(s => s.Img)
            .ToList();
    }

    /// <summary>
    /// Weighted ranking: each image scores the sum, over its tags, of the highest weight among the
    /// <paramref name="groups"/> whose tokens match that tag — so an image matching a high-priority
    /// source (e.g. author-supplied tags) outranks one matching only a low-priority one. Ordering
    /// matches the unweighted overload: score desc, then newest; zero-scoring images dropped.
    /// </summary>
    public static IReadOnlyList<TaggedImage> Rank(
        ImageTagCatalog catalog, IReadOnlyList<WeightedTokens> groups, int limit)
    {
        if (groups.Count == 0)
            return Array.Empty<TaggedImage>();

        var scored = new List<(TaggedImage Img, int Score)>();
        foreach (TaggedImage img in catalog.Images)
        {
            if (img.Tags.Count == 0)
                continue;
            int score = img.Tags.Sum(tag => TagMatchWeight(tag, groups));
            if (score > 0)
                scored.Add((img, score));
        }

        return scored
            .OrderByDescending(s => s.Score)
            .ThenByDescending(s => s.Img.ModifiedUtc)
            .Take(Math.Max(0, limit))
            .Select(s => s.Img)
            .ToList();
    }

    /// <summary>Highest weight among the groups a tag matches (0 if it matches none).</summary>
    private static int TagMatchWeight(string tag, IReadOnlyList<WeightedTokens> groups)
    {
        int best = 0;
        foreach (WeightedTokens group in groups)
            if (group.Weight > best && TagMatchesAny(tag, group.Tokens))
                best = group.Weight;
        return best;
    }

    /// <summary>
    /// Fraction of distinct <paramref name="tokens"/> matched by any of <paramref name="imageTags"/>
    /// (flexible substring/plural/stem matching), in [0, 1]. 0 when there are no tokens. Used to rank
    /// images by how strongly they match the author's <c>[TAGS:]</c> keywords.
    /// </summary>
    public static double MatchFraction(IReadOnlyList<string> imageTags, IReadOnlyCollection<string> tokens)
    {
        if (tokens.Count == 0)
            return 0.0;

        int matched = tokens.Count(token =>
            imageTags.Any(tag => TagWords(tag).Any(word => WordsMatch(word, token))));
        return (double)matched / tokens.Count;
    }

    private static bool TagMatchesAny(string tag, IReadOnlyCollection<string> tokens)
    {
        foreach (string word in TagWords(tag))
            foreach (string token in tokens)
                if (WordsMatch(word, token))
                    return true;
        return false;
    }

    /// <summary>Lower-cases a tag, drops any "attribute|" label (ImageTagger format), and splits into words ≥3.</summary>
    public static IEnumerable<string> TagWords(string tag)
    {
        string t = tag.ToLowerInvariant();
        int bar = t.LastIndexOf('|');
        if (bar >= 0 && bar + 1 < t.Length)
            t = t[(bar + 1)..];

        return NonWordRegex().Split(t).Where(w => w.Length >= 3);
    }

    public static bool WordsMatch(string a, string b)
    {
        if (a == b) return true;
        if (a.Contains(b) || b.Contains(a)) return true;
        return Stem(a) == Stem(b);
    }

    private static string Stem(string w) => w.Length > 3 && w.EndsWith('s') ? w[..^1] : w;
}
