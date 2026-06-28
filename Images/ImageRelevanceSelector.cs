using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using WPAIPoster.BlogPost;
using WPAIPoster.Config;
using WPAIPoster.Llm;
using WPAIPoster.Prompts;

namespace WPAIPoster.Images;

/// <summary>
/// A single image chosen for the post, with its relevance score, featured flag, and the theme it best
/// matched (the theme it was assigned to, or its strongest theme when used as a fill).
/// </summary>
public sealed record SelectedImage(string Path, double Score, bool IsFeatured, string? Theme = null);

/// <summary>A scored candidate: its per-theme relevance scores and perceptual hash.</summary>
public sealed record ScoredImage(string Path, IReadOnlyList<double> Scores, ulong Hash);

/// <summary>
/// Scores candidate library images against each of the post's themes using a vision model, then selects
/// a diverse set: the best <em>distinct</em> image for each theme (filling any remaining slots with the
/// next-best images), skipping ones that are perceptually near-identical to an already-chosen image.
/// The highest single score becomes the featured image.
/// </summary>
public sealed partial class ImageRelevanceSelector(ILlmClient visionClient, string promptTemplate)
{
    /// <summary>Convenience constructor that loads the bundled image-relevance prompt.</summary>
    public static ImageRelevanceSelector Create(ILlmClient visionClient)
        => new(visionClient, PromptLoader.Load(PromptLoader.ImageRelevancePromptFile).GetPromptText());

    /// <summary>
    /// Scores each candidate against every theme (one vision call per image), then returns up to
    /// <paramref name="count"/> diverse images via <see cref="Select"/>. Candidates that fail to
    /// load/score are skipped. <paramref name="onScored"/> is invoked after each image with
    /// (index, total, fileName, bestThemeScore, bestThemeName); a <see cref="double.NaN"/> score signals
    /// a skip (and a null theme name).
    /// </summary>
    public async Task<IReadOnlyList<SelectedImage>> SelectAsync(
        IReadOnlyList<string> candidatePaths,
        IReadOnlyList<ImageTheme> imageThemes,
        string postTitle,
        string postSummary,
        int count,
        int hammingThreshold = AppLimits.DefaultImageDedupThreshold,
        double minRelevance = AppLimits.DefaultMinImageRelevance,
        Action<int, int, string, double, string?>? onScored = null,
        IReadOnlySet<ulong>? recentFeaturedHashes = null,
        int recentFeaturedThreshold = AppLimits.DefaultRecentFeaturedHammingThreshold,
        IReadOnlyDictionary<string, double>? userTagAffinity = null,
        double selectionWeight = AppLimits.DefaultUserTagSelectionWeight,
        double featuredWeight = AppLimits.DefaultUserTagFeaturedWeight)
    {
        // With no themes, fall back to a single combined pseudo-theme (legacy single-score behaviour).
        IReadOnlyList<ImageTheme> themes = imageThemes.Count > 0
            ? imageThemes
            : new[] { new ImageTheme("the blog post topic", string.IsNullOrWhiteSpace(postTitle) ? "the blog post topic" : postTitle) };

        string prompt = BuildPrompt(promptTemplate, themes, postTitle, postSummary);
        int themeCount = themes.Count;
        var subjects = themes.Select(t => t.Subject).ToList(); // concise labels for display + selection

        var scored = new List<ScoredImage>();
        int total = candidatePaths.Count;

        for (int i = 0; i < total; i++)
        {
            string path = candidatePaths[i];
            double best = double.NaN;
            string? bestTheme = null;
            try
            {
                var (b64, mime) = ImagePreparer.MakeVisionThumbnailBase64(path);
                string? reply = await visionClient.SendAsync(prompt, new[] { (b64, mime) });
                double[] scores = ParseScores(reply, themeCount);
                ulong hash = PerceptualHash.Compute(path);
                scored.Add(new ScoredImage(path, scores, hash));

                if (scores.Length > 0)
                {
                    int bi = 0;
                    for (int t = 1; t < scores.Length; t++)
                        if (scores[t] > scores[bi]) bi = t;
                    best = scores[bi];
                    bestTheme = subjects[bi];
                }
                else
                {
                    best = 0;
                }
            }
            catch
            {
                // Unreadable/undecodable image — skip it (reported as NaN below).
            }

            onScored?.Invoke(i + 1, total, Path.GetFileName(path), best, bestTheme);
        }

        return Select(scored, subjects, count, hammingThreshold, minRelevance,
            recentFeaturedHashes, recentFeaturedThreshold,
            userTagAffinity, selectionWeight, featuredWeight);
    }

    /// <summary>
    /// Builds the scoring prompt: injects the post title/summary for context and the theme
    /// <em>descriptions</em> as a numbered list (matching the per-theme score array the model returns).
    /// </summary>
    public static string BuildPrompt(
        string template, IReadOnlyList<ImageTheme> themes, string postTitle, string postSummary)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < themes.Count; i++)
            sb.Append(i + 1).Append(". ").AppendLine(themes[i].Description);

        return template
            .Replace("{POST_TITLE}", postTitle ?? string.Empty)
            .Replace("{POST_SUMMARY}", postSummary ?? string.Empty)
            .Replace("{IMAGE_THEMES}", sb.ToString().TrimEnd());
    }

    /// <summary>
    /// Pure selection step. Assigns the best <em>distinct, non-duplicate</em> image to each theme,
    /// fills remaining slots with the next-best images (by max-across-themes score), and marks the
    /// single highest-scoring image as featured. Only images scoring strictly above
    /// <paramref name="minRelevance"/> are ever selected — an irrelevant image is never used to pad a
    /// theme, so fewer than <paramref name="count"/> images may be returned. Dedup is best-effort:
    /// it is relaxed (but the relevance floor is not) before the result is allowed to shrink.
    /// <para>
    /// When <paramref name="recentFeaturedHashes"/> is supplied, the <em>featured</em> pick is steered
    /// away from any chosen image within <paramref name="recentFeaturedThreshold"/> bits of a recent
    /// post's featured image, so consecutive posts don't reuse the same hero image. This only influences
    /// which chosen image is featured — colliding images may still be attached inline — and falls back
    /// to the highest-scoring pick if every chosen image collides.
    /// </para>
    /// <para>
    /// When <paramref name="userTagAffinity"/> is supplied (path → fraction of the author's
    /// <c>[TAGS:]</c> matched, 0-1), it boosts an image's effective score when filling leftover slots
    /// (by <paramref name="selectionWeight"/>) and, more strongly, in the featured blend
    /// (<c>visionScore + <paramref name="featuredWeight"/> × affinity</c>). Theme coverage stays purely
    /// vision-driven; affinity defaults to 0 for any path not in the map, so an absent map reproduces the
    /// pure-vision behaviour exactly.
    /// </para>
    /// </summary>
    public static IReadOnlyList<SelectedImage> Select(
        IReadOnlyList<ScoredImage> scored, IReadOnlyList<string> themes, int count, int hammingThreshold,
        double minRelevance = 0.0,
        IReadOnlySet<ulong>? recentFeaturedHashes = null,
        int recentFeaturedThreshold = AppLimits.DefaultRecentFeaturedHammingThreshold,
        IReadOnlyDictionary<string, double>? userTagAffinity = null,
        double selectionWeight = AppLimits.DefaultUserTagSelectionWeight,
        double featuredWeight = AppLimits.DefaultUserTagFeaturedWeight)
    {
        count = Math.Max(0, count);
        if (count == 0 || scored.Count == 0)
            return Array.Empty<SelectedImage>();

        int themeCount = Math.Max(1, themes.Count);
        var chosen = new List<(int Img, int Theme)>();   // (image index, winning theme index), in order
        var usedImage = new bool[scored.Count];
        var coveredTheme = new bool[themeCount];

        double ScoreOf(int img, int theme) =>
            theme < scored[img].Scores.Count ? scored[img].Scores[theme] : 0.0;

        double MaxScore(int img) =>
            scored[img].Scores.Count > 0 ? scored[img].Scores.Max() : 0.0;

        // Author-tag affinity for an image (0 when no map / path absent). Boosts fill ordering and the
        // featured blend, but never theme coverage or the relevance floor.
        double Aff(int img) => userTagAffinity?.GetValueOrDefault(scored[img].Path) ?? 0.0;

        int BestTheme(int img)
        {
            int bi = 0;
            for (int t = 1; t < themeCount; t++)
                if (ScoreOf(img, t) > ScoreOf(img, bi)) bi = t;
            return bi;
        }

        bool Eligible(int img) => MaxScore(img) > minRelevance;

        bool IsDup(int img) =>
            chosen.Any(c => PerceptualHash.HammingDistance(scored[c.Img].Hash, scored[img].Hash) <= hammingThreshold);

        void Add(int img, int theme, bool cover)
        {
            usedImage[img] = true;
            if (cover) coveredTheme[theme] = true;
            chosen.Add((img, theme));
        }

        void FillBy(Func<int, bool> ok)
        {
            foreach (int img in Enumerable.Range(0, scored.Count)
                         .Where(img => Eligible(img) && ok(img))
                         .OrderByDescending(img => MaxScore(img) + selectionWeight * Aff(img))
                         .ThenBy(img => scored[img].Path, StringComparer.Ordinal))
            {
                if (chosen.Count >= count) return;
                Add(img, BestTheme(img), cover: false); // fill picks display their strongest theme
            }
        }

        // 1) All (image, theme, score) triples, best first; tie-break by path then theme for determinism.
        var triples = new List<(int Img, int Theme, double Score)>(scored.Count * themeCount);
        for (int img = 0; img < scored.Count; img++)
            for (int t = 0; t < themeCount; t++)
                triples.Add((img, t, ScoreOf(img, t)));

        triples.Sort((a, b) =>
        {
            int c = b.Score.CompareTo(a.Score);
            if (c != 0) return c;
            c = string.CompareOrdinal(scored[a.Img].Path, scored[b.Img].Path);
            return c != 0 ? c : a.Theme.CompareTo(b.Theme);
        });

        // 2) Greedy per-theme assignment: best distinct, non-duplicate, sufficiently-relevant image per theme.
        foreach (var (img, theme, score) in triples)
        {
            if (chosen.Count >= count) break;
            if (score <= minRelevance) break; // triples are sorted desc — nothing past here qualifies
            if (coveredTheme[theme] || usedImage[img] || IsDup(img)) continue;
            Add(img, theme, cover: true);
        }

        // 3) Fill leftover slots (themeCount < count, or themes with no usable image) with distinct,
        //    non-duplicate images by their best score.
        if (chosen.Count < count)
            FillBy(img => !usedImage[img] && !IsDup(img));

        // 4) If still short purely because of dedup, relax the duplicate check (relevance floor stays).
        if (chosen.Count < count)
            FillBy(img => !usedImage[img]);

        if (chosen.Count == 0)
            return Array.Empty<SelectedImage>();

        // 5) Featured = highest-blend chosen image (vision score + author-tag affinity), but steered away
        //    from any image matching a recent post's featured image (by perceptual hash). If every chosen
        //    image collides — or no history was supplied — fall back to the plain highest-blend pick.
        var byScore = chosen
            .OrderByDescending(c => MaxScore(c.Img) + featuredWeight * Aff(c.Img))
            .ThenBy(c => scored[c.Img].Path, StringComparer.Ordinal)
            .ToList();

        int featured = (recentFeaturedHashes is { Count: > 0 }
            ? byScore.FirstOrDefault(
                c => !PerceptualHash.IsWithinAny(scored[c.Img].Hash, recentFeaturedHashes, recentFeaturedThreshold),
                byScore[0])
            : byScore[0]).Img;

        string? ThemeName(int idx) => idx >= 0 && idx < themes.Count ? themes[idx] : null;

        return chosen
            .Select(c => new SelectedImage(scored[c.Img].Path, MaxScore(c.Img), c.Img == featured, ThemeName(c.Theme)))
            .ToList();
    }

    /// <summary>
    /// Extracts up to <paramref name="themeCount"/> scores from the model reply, in order, each clamped
    /// to [0, 1]. Missing scores default to 0; extra numbers are ignored. Prefers the contents of the
    /// first JSON array to avoid stray numbers in any surrounding prose.
    /// </summary>
    public static double[] ParseScores(string? reply, int themeCount)
    {
        var result = new double[Math.Max(0, themeCount)];
        if (result.Length == 0 || string.IsNullOrWhiteSpace(reply))
            return result;

        string scope = reply;
        int lb = reply.IndexOf('[');
        if (lb >= 0)
        {
            int rb = reply.IndexOf(']', lb + 1);
            if (rb > lb) scope = reply[(lb + 1)..rb];
        }

        MatchCollection matches = NumberRegex().Matches(scope);
        for (int i = 0; i < result.Length && i < matches.Count; i++)
            if (double.TryParse(matches[i].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                result[i] = Math.Clamp(v, 0, 1);

        return result;
    }

    /// <summary>Extracts a single relevance score in [0, 1] from the model reply. Defaults to 0.</summary>
    public static double ParseScore(string? reply) => ParseScores(reply, 1)[0];

    [GeneratedRegex(@"\d+(\.\d+)?")]
    private static partial Regex NumberRegex();
}
