using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using WPAIPoster.Config;
using WPAIPoster.Llm;
using WPAIPoster.Prompts;

namespace WPAIPoster.Images;

/// <summary>A single image chosen for the post, with its relevance score and featured flag.</summary>
public sealed record SelectedImage(string Path, double Score, bool IsFeatured);

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
    /// (index, total, fileName, bestThemeScore); a <see cref="double.NaN"/> score signals a skip.
    /// </summary>
    public async Task<IReadOnlyList<SelectedImage>> SelectAsync(
        IReadOnlyList<string> candidatePaths,
        IReadOnlyList<string> imageThemes,
        int count,
        int hammingThreshold = AppLimits.DefaultImageDedupThreshold,
        Action<int, int, string, double>? onScored = null)
    {
        // With no themes, fall back to a single combined pseudo-theme (legacy single-score behaviour).
        IReadOnlyList<string> themes = imageThemes.Count > 0
            ? imageThemes
            : new[] { "the blog post topic" };

        string prompt = BuildPrompt(promptTemplate, themes);
        int themeCount = themes.Count;

        var scored = new List<ScoredImage>();
        int total = candidatePaths.Count;

        for (int i = 0; i < total; i++)
        {
            string path = candidatePaths[i];
            double best = double.NaN;
            try
            {
                var (b64, mime) = ImagePreparer.MakeVisionThumbnailBase64(path);
                string? reply = await visionClient.SendAsync(prompt, new[] { (b64, mime) });
                double[] scores = ParseScores(reply, themeCount);
                ulong hash = PerceptualHash.Compute(path);
                scored.Add(new ScoredImage(path, scores, hash));
                best = scores.Length > 0 ? scores.Max() : 0;
            }
            catch
            {
                // Unreadable/undecodable image — skip it (reported as NaN below).
            }

            onScored?.Invoke(i + 1, total, Path.GetFileName(path), best);
        }

        return Select(scored, themeCount, count, hammingThreshold);
    }

    /// <summary>Builds the scoring prompt, injecting the themes as a numbered list.</summary>
    public static string BuildPrompt(string template, IReadOnlyList<string> themes)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < themes.Count; i++)
            sb.Append(i + 1).Append(". ").AppendLine(themes[i]);
        return template.Replace("{IMAGE_THEMES}", sb.ToString().TrimEnd());
    }

    /// <summary>
    /// Pure selection step. Assigns the best <em>distinct, non-duplicate</em> image to each theme,
    /// fills remaining slots with the next-best images (by max-across-themes score), and marks the
    /// single highest-scoring image as featured. Dedup is best-effort: it is relaxed rather than ever
    /// returning fewer than <c>min(count, candidate count)</c> images.
    /// </summary>
    public static IReadOnlyList<SelectedImage> Select(
        IReadOnlyList<ScoredImage> scored, int themeCount, int count, int hammingThreshold)
    {
        count = Math.Max(0, count);
        if (count == 0 || scored.Count == 0)
            return Array.Empty<SelectedImage>();

        int themes = Math.Max(1, themeCount);
        var chosen = new List<int>();                 // indices into scored, in selection order
        var usedImage = new bool[scored.Count];
        var coveredTheme = new bool[themes];

        double ScoreOf(int img, int theme) =>
            theme < scored[img].Scores.Count ? scored[img].Scores[theme] : 0.0;

        double MaxScore(int img) =>
            scored[img].Scores.Count > 0 ? scored[img].Scores.Max() : 0.0;

        bool IsDup(int img) =>
            chosen.Any(c => PerceptualHash.HammingDistance(scored[c].Hash, scored[img].Hash) <= hammingThreshold);

        void Add(int img, int? theme)
        {
            usedImage[img] = true;
            if (theme is int t) coveredTheme[t] = true;
            chosen.Add(img);
        }

        void FillBy(Func<int, bool> ok)
        {
            foreach (int img in Enumerable.Range(0, scored.Count)
                         .Where(ok)
                         .OrderByDescending(MaxScore)
                         .ThenBy(img => scored[img].Path, StringComparer.Ordinal))
            {
                if (chosen.Count >= count) return;
                Add(img, theme: null);
            }
        }

        // 1) All (image, theme, score) triples, best first; tie-break by path then theme for determinism.
        var triples = new List<(int Img, int Theme, double Score)>(scored.Count * themes);
        for (int img = 0; img < scored.Count; img++)
            for (int t = 0; t < themes; t++)
                triples.Add((img, t, ScoreOf(img, t)));

        triples.Sort((a, b) =>
        {
            int c = b.Score.CompareTo(a.Score);
            if (c != 0) return c;
            c = string.CompareOrdinal(scored[a.Img].Path, scored[b.Img].Path);
            return c != 0 ? c : a.Theme.CompareTo(b.Theme);
        });

        // 2) Greedy per-theme assignment: best distinct, non-duplicate image for each uncovered theme.
        foreach (var (img, theme, _) in triples)
        {
            if (chosen.Count >= count) break;
            if (coveredTheme[theme] || usedImage[img] || IsDup(img)) continue;
            Add(img, theme);
        }

        // 3) Fill leftover slots (themeCount < count, or themes with no usable image) with distinct,
        //    non-duplicate images by their best score.
        if (chosen.Count < count)
            FillBy(img => !usedImage[img] && !IsDup(img));

        // 4) Last resort: if still short purely because of dedup, relax the duplicate check.
        if (chosen.Count < count)
            FillBy(img => !usedImage[img]);

        // 5) Featured = highest single score among the chosen images.
        int featured = chosen
            .OrderByDescending(MaxScore)
            .ThenBy(img => scored[img].Path, StringComparer.Ordinal)
            .First();

        return chosen
            .Select(img => new SelectedImage(scored[img].Path, MaxScore(img), img == featured))
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
