using System.Text;
using System.Text.RegularExpressions;
using WPAIPoster.BlogPost;
using WPAIPoster.Config;
using WPAIPoster.Llm;
using WPAIPoster.Prompts;

namespace WPAIPoster.Images;

/// <summary>
/// First-pass image selection using keyword tags. Locally pre-filters the catalog with
/// <see cref="TagMatcher"/>, then asks the text model to contextually pick the most suitable images
/// from that shortlist. Returns the selected image paths (best-first); empty when no tags match.
/// </summary>
public sealed class TagBasedImageSelector(ILlmClient client, string promptTemplate)
{
    /// <summary>Convenience constructor that loads the bundled tag-to-body prompt.</summary>
    public static TagBasedImageSelector Create(ILlmClient client)
        => new(client, PromptLoader.Load(PromptLoader.TagToBodyPromptFile).GetPromptText());

    /// <summary>
    /// Ranks the catalog by weighted tag relevance to <paramref name="post"/> (and any author-supplied
    /// <paramref name="userTags"/>), asks the model to pick from the top <paramref name="candidateLimit"/>,
    /// and returns the chosen paths. Falls back to the local ranking if the model returns nothing parseable;
    /// returns empty if no image tags match the content at all. Sources are weighted highest-first:
    /// author tags, post tags, image themes, categories, then the H1/body text as a background signal.
    /// </summary>
    public async Task<IReadOnlyList<string>> SelectAsync(
        ImageTagCatalog catalog, BlogPostResult post, int candidateLimit, IReadOnlyList<string>? userTags = null)
    {
        var groups = new List<TagMatcher.WeightedTokens>
        {
            new(TagMatcher.TokenizeWords(userTags), AppLimits.TagWeightUserProvided),
            new(TagMatcher.TokenizeWords(post.Tags), AppLimits.TagWeightTags),
            new(TagMatcher.TokenizeWords(post.ImageThemes.Select(t => t.Subject)), AppLimits.TagWeightThemes),
            new(TagMatcher.TokenizeWords(post.Categories), AppLimits.TagWeightCategories),
            new(TagMatcher.Tokenize(post.H1, post.BodyHtml, null), AppLimits.TagWeightBodyBackground),
        };
        IReadOnlyList<TaggedImage> candidates = TagMatcher.Rank(catalog, groups, candidateLimit);
        if (candidates.Count == 0)
            return Array.Empty<string>();

        string prompt = BuildPrompt(promptTemplate, post, candidates);
        string? reply = await client.SendAsync(prompt, null, null);

        IReadOnlyList<int> picks = ParseSelectedIndices(reply, candidates.Count);
        if (picks.Count == 0)
            return candidates.Select(c => c.Path).ToList(); // model unhelpful → keep local ranking

        return picks.Select(i => candidates[i - 1].Path).ToList();
    }

    /// <summary>Fills the prompt tokens; the candidate list is numbered 1..N with each image's tags.</summary>
    public static string BuildPrompt(string template, BlogPostResult post, IReadOnlyList<TaggedImage> candidates)
    {
        var list = new StringBuilder();
        for (int i = 0; i < candidates.Count; i++)
            list.AppendLine($"{i + 1}. {string.Join(", ", candidates[i].Tags)}");

        return template
            .Replace("{TITLE}", post.H1)
            .Replace("{IMAGE_THEMES}", string.Join(", ", post.ImageThemes.Select(t => t.Subject)))
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
    /// Extracts 1-based image numbers from the model reply (preferring the contents of the first JSON
    /// array), keeping only those in [1, count], deduped in first-seen order.
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
