using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WPAIPoster.BlogPost;
using WPAIPoster.Config;
using WPAIPoster.Llm;
using WPAIPoster.Prompts;

namespace WPAIPoster.Images;

/// <summary>
/// First-pass image selection using keyword tags, structured to keep the shortlist <em>balanced across
/// themes</em>. It ranks the catalog once <em>per theme</em> (<see cref="TagMatcher.RankPerTheme"/>) so no
/// tag-dense theme can crowd the others out, then asks the text model to pick the best candidates
/// <em>per theme</em> and interleaves those picks round-robin. Falls back to the per-theme code ranking
/// (still round-robin, never a single global order) if the model returns nothing parseable. Returns the
/// selected image paths (theme-balanced, best-first); empty when no image tags match at all.
/// </summary>
public sealed class TagBasedImageSelector(ILlmClient client, string promptTemplate)
{
    /// <summary>Convenience constructor that loads the bundled tag-to-body prompt.</summary>
    public static TagBasedImageSelector Create(ILlmClient client)
        => new(client, PromptLoader.Load(PromptLoader.TagToBodyPromptFile).GetPromptText());

    /// <summary>
    /// Ranks the catalog per theme (top <paramref name="candidateLimit"/> spread across themes), asks the
    /// model to pick the best images for EACH theme, and returns a theme-balanced, round-robin-interleaved
    /// list of paths. Author-supplied <paramref name="userTags"/> and the post tags act as a cross-cutting
    /// boost so a strongly-tagged image is surfaced under every theme. Returns empty if no image tags match
    /// the content at all.
    /// </summary>
    public async Task<IReadOnlyList<string>> SelectAsync(
        ImageTagCatalog catalog, BlogPostResult post, int candidateLimit, IReadOnlyList<string>? userTags = null)
    {
        // Fall back to a single pseudo-theme (H1) when the model proposed none.
        IReadOnlyList<ImageTheme> themes = post.ImageThemes.Count > 0
            ? post.ImageThemes
            : new[] { new ImageTheme("the post topic", post.H1) };

        var themeTokenGroups = themes
            .Select(t => TagMatcher.TokenizeWords(new[] { t.Subject, t.Description }))
            .ToList();
        // Author tags + post tags boost every theme so [TAGS:]-preferred imagery still surfaces.
        IReadOnlyCollection<string> crossCut =
            TagMatcher.TokenizeWords((userTags ?? Array.Empty<string>()).Concat(post.Tags));

        int perThemeLimit = Math.Max(3, (int)Math.Ceiling(candidateLimit / (double)themes.Count));
        IReadOnlyList<IReadOnlyList<TaggedImage>> perTheme =
            TagMatcher.RankPerTheme(catalog, themeTokenGroups, crossCut, perThemeLimit);

        // Deduped candidate union, numbered 1..N in theme-major order.
        var union = new List<TaggedImage>();
        var indexOf = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (IReadOnlyList<TaggedImage> list in perTheme)
            foreach (TaggedImage img in list)
                if (indexOf.TryAdd(img.Path, union.Count))
                    union.Add(img);

        if (union.Count == 0)
            return Array.Empty<string>();

        string prompt = BuildPrompt(promptTemplate, post, themes, union, perTheme);
        string? reply = await client.SendAsync(prompt, null, null);

        IReadOnlyList<IReadOnlyList<int>> byTheme = ParseSelectedByTheme(reply, themes.Count, union.Count);

        // Model picks (candidate numbers → paths) per theme, or the per-theme code ranking as a balanced
        // fallback when the reply is unparseable — never collapse to a single global order.
        IReadOnlyList<IReadOnlyList<string>> selectionByTheme =
            byTheme.Any(l => l.Count > 0)
                ? byTheme.Select(nums => (IReadOnlyList<string>)nums.Select(n => union[n - 1].Path).ToList()).ToList()
                : perTheme.Select(list => (IReadOnlyList<string>)list.Select(i => i.Path).ToList()).ToList();

        return InterleaveRoundRobin(selectionByTheme);
    }

    /// <summary>
    /// Interleaves per-theme ordered lists round-robin (1st of each theme, then 2nd, …), deduping across
    /// themes in first-seen order — so the head of the result is balanced across themes and the downstream
    /// vision-scoring cap keeps a diverse set.
    /// </summary>
    public static IReadOnlyList<string> InterleaveRoundRobin(IReadOnlyList<IReadOnlyList<string>> perTheme)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int maxLen = perTheme.Count == 0 ? 0 : perTheme.Max(l => l.Count);
        for (int round = 0; round < maxLen; round++)
            foreach (IReadOnlyList<string> list in perTheme)
                if (round < list.Count && seen.Add(list[round]))
                    result.Add(list[round]);
        return result;
    }

    /// <summary>
    /// Fills the prompt tokens: the numbered themes (subject — description), and the numbered candidate
    /// union with each image's tags and a hint of which theme number(s) surfaced it.
    /// </summary>
    public static string BuildPrompt(
        string template, BlogPostResult post, IReadOnlyList<ImageTheme> themes,
        IReadOnlyList<TaggedImage> candidates, IReadOnlyList<IReadOnlyList<TaggedImage>> perTheme)
    {
        var themeList = new StringBuilder();
        for (int i = 0; i < themes.Count; i++)
            themeList.Append(i + 1).Append(". ").Append(themes[i].Subject)
                     .Append(" — ").AppendLine(themes[i].Description);

        // Which theme number(s) each candidate matched (for a hint only; the model may pick any candidate).
        var indexOf = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < candidates.Count; i++)
            indexOf[candidates[i].Path] = i;
        var members = new List<int>[candidates.Count];
        for (int i = 0; i < candidates.Count; i++)
            members[i] = new List<int>();
        for (int t = 0; t < perTheme.Count; t++)
            foreach (TaggedImage img in perTheme[t])
                if (indexOf.TryGetValue(img.Path, out int idx))
                    members[idx].Add(t + 1);

        var list = new StringBuilder();
        for (int i = 0; i < candidates.Count; i++)
            list.Append(i + 1).Append(". [themes ").Append(string.Join(",", members[i])).Append("] ")
                .AppendLine(string.Join(", ", candidates[i].Tags));

        return template
            .Replace("{TITLE}", post.H1)
            .Replace("{THEMES}", themeList.ToString().TrimEnd())
            .Replace("{BODY}", BodyContext(post.BodyHtml))
            .Replace("{TAGGED_IMAGES}", list.ToString().TrimEnd());
    }

    /// <summary>Strips HTML and collapses whitespace, truncating to keep the prompt small.</summary>
    internal static string BodyContext(string? bodyHtml, int max = 1500)
    {
        string text = Regex.Replace(bodyHtml ?? string.Empty, "<[^>]+>", " ");
        text = Regex.Replace(text, "\\s+", " ").Trim();
        return text.Length <= max ? text : text[..max] + "…";
    }

    /// <summary>
    /// Parses the per-theme selection object <c>{"1":[4,19], "2":[], "3":[1,15]}</c> (theme number → image
    /// numbers). Tolerant of surrounding prose/fences (isolates the first <c>{…}</c>); keys must be theme
    /// numbers in <c>[1, themeCount]</c> and values image numbers in <c>[1, candidateCount]</c> (deduped,
    /// first-seen). Returns a list of empty lists (triggering the code fallback) on any unparseable reply
    /// or a non-object (e.g. the model returned a bare array). Index-aligned to the themes.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<int>> ParseSelectedByTheme(
        string? reply, int themeCount, int candidateCount)
    {
        var result = new List<List<int>>();
        for (int i = 0; i < Math.Max(0, themeCount); i++)
            result.Add(new List<int>());

        if (string.IsNullOrWhiteSpace(reply) || themeCount <= 0 || candidateCount <= 0)
            return Cast(result);

        int lb = reply.IndexOf('{');
        int rb = reply.LastIndexOf('}');
        if (lb < 0 || rb <= lb)
            return Cast(result);

        try
        {
            using JsonDocument doc = JsonDocument.Parse(reply[lb..(rb + 1)]);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return Cast(result);

            foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
            {
                if (!int.TryParse(prop.Name.Trim(), out int k) || k < 1 || k > themeCount)
                    continue;
                if (prop.Value.ValueKind != JsonValueKind.Array)
                    continue;

                var seen = new HashSet<int>();
                foreach (JsonElement el in prop.Value.EnumerateArray())
                {
                    int n;
                    if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out n)) { }
                    else if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out n)) { }
                    else continue;

                    if (n >= 1 && n <= candidateCount && seen.Add(n))
                        result[k - 1].Add(n);
                }
            }
        }
        catch (JsonException)
        {
            // Unparseable object — leave empty lists so SelectAsync falls back to the code ranking.
        }

        return Cast(result);
    }

    private static IReadOnlyList<IReadOnlyList<int>> Cast(List<List<int>> lists)
        => lists.Select(l => (IReadOnlyList<int>)l).ToList();

    /// <summary>
    /// Extracts 1-based image numbers from a bare model reply (preferring the contents of the first JSON
    /// array), keeping only those in [1, count], deduped in first-seen order. Retained for callers/tests
    /// that expect a single flat pick list.
    /// </summary>
    public static IReadOnlyList<int> ParseSelectedIndices(string? reply, int count)
    {
        if (string.IsNullOrWhiteSpace(reply) || count <= 0)
            return Array.Empty<int>();

        // Prefer the contents of the first [...] array to avoid stray numbers in any prose.
        string scope = reply;
        int lb = reply.IndexOf('[');
        if (lb >= 0)
        {
            int rb = reply.IndexOf(']', lb + 1);
            if (rb > lb)
                scope = reply[(lb + 1)..rb];
        }

        var result = new List<int>();
        var seen = new HashSet<int>();
        foreach (Match m in Regex.Matches(scope, "\\d+"))
        {
            if (int.TryParse(m.Value, out int n) && n >= 1 && n <= count && seen.Add(n))
                result.Add(n);
        }
        return result;
    }
}
