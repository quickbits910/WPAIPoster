using System.Globalization;
using System.Text.RegularExpressions;
using WPAIPoster.Llm;
using WPAIPoster.Prompts;

namespace WPAIPoster.Images;

/// <summary>A single image chosen for the post, with its relevance score and featured flag.</summary>
public sealed record SelectedImage(string Path, double Score, bool IsFeatured);

/// <summary>
/// Scores candidate library images against the post's themes using a vision model and selects the
/// most relevant ones. The highest-scoring image becomes the featured image.
/// </summary>
public sealed partial class ImageRelevanceSelector(ILlmClient visionClient, string promptTemplate)
{
    /// <summary>Convenience constructor that loads the bundled image-relevance prompt.</summary>
    public static ImageRelevanceSelector Create(ILlmClient visionClient)
        => new(visionClient, PromptLoader.Load(PromptLoader.ImageRelevancePromptFile).GetPromptText());

    /// <summary>
    /// Scores each candidate via the vision model and returns the top <paramref name="count"/> by
    /// relevance (featured = highest). Candidates that fail to load/score are skipped.
    /// <paramref name="onScored"/> is invoked after each image with (index, total, fileName, score);
    /// a <see cref="double.NaN"/> score signals the image was skipped (unreadable).
    /// </summary>
    public async Task<IReadOnlyList<SelectedImage>> SelectAsync(
        IReadOnlyList<string> candidatePaths,
        IReadOnlyList<string> imageThemes,
        int count,
        Action<int, int, string, double>? onScored = null)
    {
        string prompt = promptTemplate.Replace("{IMAGE_THEMES}", string.Join(", ", imageThemes));
        var scored = new List<(string Path, double Score)>();
        int total = candidatePaths.Count;

        for (int i = 0; i < total; i++)
        {
            string path = candidatePaths[i];
            double score = double.NaN;
            try
            {
                var (b64, mime) = ImagePreparer.MakeVisionThumbnailBase64(path);
                string? reply = await visionClient.SendAsync(prompt, new[] { (b64, mime) });
                score = ParseScore(reply);
                scored.Add((path, score));
            }
            catch
            {
                // Unreadable/undecodable image — skip it (reported as NaN below).
            }

            onScored?.Invoke(i + 1, total, Path.GetFileName(path), score);
        }

        return Select(scored, count);
    }

    /// <summary>Pure selection step: order by score desc, take <paramref name="count"/>, mark featured.</summary>
    public static IReadOnlyList<SelectedImage> Select(
        IReadOnlyList<(string Path, double Score)> scored, int count)
    {
        return scored
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.Path, StringComparer.Ordinal)
            .Take(Math.Max(0, count))
            .Select((s, i) => new SelectedImage(s.Path, s.Score, i == 0))
            .ToList();
    }

    /// <summary>Extracts the first number in the model reply and clamps it to [0, 1]. Defaults to 0.</summary>
    public static double ParseScore(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply))
            return 0;

        Match m = NumberRegex().Match(reply);
        if (!m.Success || !double.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
            return 0;

        return Math.Clamp(v, 0, 1);
    }

    [GeneratedRegex(@"\d+(\.\d+)?")]
    private static partial Regex NumberRegex();
}
