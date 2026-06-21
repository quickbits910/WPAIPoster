using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WPAIPoster.Llm;
using WPAIPoster.Prompts;

namespace WPAIPoster.BlogPost;

/// <summary>
/// The Editor's verdict on a draft post: a quality score in [0, 1] and actionable feedback. A
/// <see cref="double.NaN"/> <see cref="Score"/> means the reply could not be parsed (treat as "could
/// not review").
/// </summary>
public sealed record EditorReview(double Score, string Feedback)
{
    /// <summary>True when the reviewer reply could not be parsed into a usable score.</summary>
    public bool IsUnscored => double.IsNaN(Score);
}

/// <summary>
/// A critical "Editor" pass over a generated draft: asks the text model to score the post for
/// publish-readiness and return improvement feedback. Used to gate publishing and to drive rewrites.
/// </summary>
public sealed partial class EditorReviewer(ILlmClient client, string promptTemplate)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>Convenience constructor that loads the bundled editor-reviewer prompt.</summary>
    public static EditorReviewer Create(ILlmClient client)
        => new(client, PromptLoader.Load(PromptLoader.EditorReviewerPromptFile).GetPromptText());

    /// <summary>Reviews <paramref name="post"/> against the original <paramref name="userInput"/> brief.</summary>
    public async Task<EditorReview> ReviewAsync(string userInput, BlogPostResult post)
    {
        string prompt = BuildPrompt(promptTemplate, userInput, post);
        string? reply = await client.SendAsync(prompt, null, null);
        return ParseReview(reply);
    }

    /// <summary>
    /// Combines feedback from successive review rounds into a single block for the next rewrite, so earlier
    /// (possibly unaddressed) notes are retained. Each round is labelled; the model is told some points may
    /// already be addressed and to satisfy them all. Returns the single note unchanged when there's only one.
    /// </summary>
    public static string CombineFeedback(IReadOnlyList<string> notes)
    {
        var cleaned = notes
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .ToList();

        if (cleaned.Count == 0)
            return string.Empty;
        if (cleaned.Count == 1)
            return cleaned[0];

        var sb = new StringBuilder();
        sb.AppendLine(
            "These notes are from successive review rounds (earliest first); some may already be addressed — " +
            "ensure EVERY point below is satisfied in this rewrite:");
        for (int i = 0; i < cleaned.Count; i++)
        {
            sb.AppendLine();
            sb.AppendLine($"Review round {i + 1}:");
            sb.AppendLine(cleaned[i]);
        }
        return sb.ToString().Trim();
    }

    /// <summary>Fills the reviewer prompt tokens from the brief and the draft. Pure and testable.</summary>
    public static string BuildPrompt(string template, string userInput, BlogPostResult post)
        => template
            .Replace("{USER_INPUT}", userInput)
            .Replace("{META_TITLE}", post.MetaTitle)
            .Replace("{META_DESCRIPTION}", post.MetaDescription)
            .Replace("{H1}", post.H1)
            .Replace("{BODY}", post.BodyHtml)
            .Replace("{CTA}", post.Cta);

    /// <summary>
    /// Parses the reviewer's JSON envelope into an <see cref="EditorReview"/>. Tolerant of fences and
    /// surrounding prose (reuses <see cref="BlogPostParser"/>'s extraction/repair). Returns an unscored
    /// review (<see cref="double.NaN"/>) when no score can be found, so a flaky reviewer never blocks
    /// the pipeline.
    /// </summary>
    public static EditorReview ParseReview(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return new EditorReview(double.NaN, string.Empty);

        try
        {
            string json = BlogPostParser.ExtractJsonObject(raw);
            if (TryParse(json, out EditorReview review) || TryParse(BlogPostParser.RepairJson(json), out review))
                return review;
        }
        catch (FormatException)
        {
            // No JSON object in the reply.
        }

        return new EditorReview(double.NaN, string.Empty);
    }

    private static bool TryParse(string json, out EditorReview review)
    {
        review = new EditorReview(double.NaN, string.Empty);
        try
        {
            ReviewDto? dto = JsonSerializer.Deserialize<ReviewDto>(json, Options);
            if (dto is null)
                return false;

            double score = ParseScore(dto.Score);
            review = new EditorReview(score, dto.Feedback?.Trim() ?? string.Empty);
            return !double.IsNaN(score);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Reads the score whether the model returned it as a JSON number or a quoted string.</summary>
    private static double ParseScore(JsonElement score)
    {
        string? text = score.ValueKind switch
        {
            JsonValueKind.Number => score.GetRawText(),
            JsonValueKind.String => score.GetString(),
            _ => null,
        };
        if (string.IsNullOrWhiteSpace(text))
            return double.NaN;

        Match m = NumberRegex().Match(text);
        if (!m.Success || !double.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
            return double.NaN;

        return Math.Clamp(v, 0, 1);
    }

    private sealed class ReviewDto
    {
        [System.Text.Json.Serialization.JsonPropertyName("score")]
        public JsonElement Score { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("feedback")]
        public string? Feedback { get; set; }
    }

    [GeneratedRegex(@"\d+(\.\d+)?")]
    private static partial Regex NumberRegex();
}
