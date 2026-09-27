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

/// <summary>How a single candidate fared during vision scoring.</summary>
public enum ScoringOutcome
{
    /// <summary>The vision model replied and the image was scored.</summary>
    Scored,
    /// <summary>The local image file couldn't be loaded/decoded (thumbnail or hash failed).</summary>
    Unreadable,
    /// <summary>The vision request itself failed (HTTP error, timeout, server down).</summary>
    RequestFailed,
    /// <summary>Not attempted: scoring stopped after repeated request failures.</summary>
    NotAttempted
}

/// <summary>
/// Per-candidate progress report from <see cref="ImageRelevanceSelector.SelectAsync"/>. <see cref="Score"/>
/// is the best theme score (<see cref="double.NaN"/> unless <see cref="Outcome"/> is
/// <see cref="ScoringOutcome.Scored"/>); <see cref="Error"/> describes why a candidate wasn't scored.
/// </summary>
public sealed record ScoringProgress(
    int Index, int Total, string FileName, ScoringOutcome Outcome,
    double Score = double.NaN, string? Theme = null, string? Error = null);

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
    /// load/score are skipped. <paramref name="onScored"/> is invoked once per candidate with a
    /// <see cref="ScoringProgress"/> that distinguishes an unreadable local file from a failed vision
    /// request (with the error message). After <paramref name="maxConsecutiveRequestFailures"/> request
    /// failures in a row the endpoint is treated as down (e.g. the LLM server crashed) and the remaining
    /// candidates are reported as <see cref="ScoringOutcome.NotAttempted"/> rather than sent.
    /// </summary>
    public async Task<IReadOnlyList<SelectedImage>> SelectAsync(
        IReadOnlyList<string> candidatePaths,
        IReadOnlyList<ImageTheme> imageThemes,
        string postTitle,
        string postSummary,
        int count,
        int hammingThreshold = AppLimits.DefaultImageDedupThreshold,
        double minRelevance = AppLimits.DefaultMinImageRelevance,
        Action<ScoringProgress>? onScored = null,
        IReadOnlySet<ulong>? recentFeaturedHashes = null,
        int recentFeaturedThreshold = AppLimits.DefaultRecentFeaturedHammingThreshold,
        IReadOnlyDictionary<string, double>? userTagAffinity = null,
        double selectionWeight = AppLimits.DefaultUserTagSelectionWeight,
        double featuredWeight = AppLimits.DefaultUserTagFeaturedWeight,
        double coverageFloor = AppLimits.DefaultThemeCoverageFloor,
        TimeSpan? firstVisionRetryDelay = null,
        int maxConsecutiveRequestFailures = AppLimits.MaxConsecutiveVisionFailures)
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
        bool isFirstVisionAttempt = true;

        async Task<string?> SendVisionAsync(string promptText, IReadOnlyList<(string Base64, string MimeType)> images)
        {
            bool canRetry = isFirstVisionAttempt && firstVisionRetryDelay.GetValueOrDefault() > TimeSpan.Zero;
            isFirstVisionAttempt = false;

            try
            {
                return await visionClient.SendAsync(promptText, images);
            }
            catch when (canRetry)
            {
                await Task.Delay(firstVisionRetryDelay!.Value);
                return await visionClient.SendAsync(promptText, images);
            }
        }

        int consecutiveRequestFailures = 0;
        string? lastRequestError = null;

        for (int i = 0; i < total; i++)
        {
            string path = candidatePaths[i];
            string fileName = Path.GetFileName(path);

            // The endpoint has failed repeatedly (e.g. the LLM server ran out of memory and died) — stop
            // hammering it and report the rest as not attempted instead of mislabelling them.
            if (maxConsecutiveRequestFailures > 0 && consecutiveRequestFailures >= maxConsecutiveRequestFailures)
            {
                onScored?.Invoke(new ScoringProgress(i + 1, total, fileName, ScoringOutcome.NotAttempted,
                    Error: $"stopped after {consecutiveRequestFailures} consecutive vision request failures (last: {lastRequestError})"));
                continue;
            }

            // Local prep first, so a bad file is distinguishable from a failed request.
            string b64, mime;
            ulong hash;
            try
            {
                (b64, mime) = ImagePreparer.MakeVisionThumbnailBase64(path);
                hash = PerceptualHash.Compute(path);
            }
            catch (Exception ex)
            {
                onScored?.Invoke(new ScoringProgress(i + 1, total, fileName, ScoringOutcome.Unreadable,
                    Error: Describe(ex)));
                continue;
            }

            string? reply;
            try
            {
                reply = await SendVisionAsync(prompt, new[] { (b64, mime) });
                consecutiveRequestFailures = 0;
            }
            catch (Exception ex)
            {
                consecutiveRequestFailures++;
                lastRequestError = Describe(ex);
                onScored?.Invoke(new ScoringProgress(i + 1, total, fileName, ScoringOutcome.RequestFailed,
                    Error: lastRequestError));
                continue;
            }

            double[] scores = ParseScores(reply, themeCount);
            scored.Add(new ScoredImage(path, scores, hash));

            double best = 0;
            string? bestTheme = null;
            if (scores.Length > 0)
            {
                int bi = 0;
                for (int t = 1; t < scores.Length; t++)
                    if (scores[t] > scores[bi]) bi = t;
                best = scores[bi];
                bestTheme = subjects[bi];
            }

            onScored?.Invoke(new ScoringProgress(i + 1, total, fileName, ScoringOutcome.Scored, best, bestTheme));
        }

        return Select(scored, subjects, count, hammingThreshold, minRelevance,
            recentFeaturedHashes, recentFeaturedThreshold,
            userTagAffinity, selectionWeight, featuredWeight, coverageFloor);
    }

    /// <summary>One-line error description (type + message, innermost cause appended) for the run log.</summary>
    public static string Describe(Exception ex)
    {
        string text = $"{ex.GetType().Name}: {ex.Message}";
        Exception root = ex.GetBaseException();
        return ReferenceEquals(root, ex) ? text : $"{text} ← {root.GetType().Name}: {root.Message}";
    }

    /// <summary>
    /// Builds the scoring prompt: injects the post title/summary for context and each theme as a numbered
    /// <c>subject — description</c> line (matching the per-theme score array the model returns). Including
    /// the broad <em>subject</em> — not just the specific description — lets a genuinely on-subject image
    /// score well even when it doesn't match every literal detail of the description (e.g. a developer's
    /// screen scoring on a "code" subject whose description named an exact split-screen layout).
    /// </summary>
    public static string BuildPrompt(
        string template, IReadOnlyList<ImageTheme> themes, string postTitle, string postSummary)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < themes.Count; i++)
            sb.Append(i + 1).Append(". ").Append(themes[i].Subject)
              .Append(" — ").AppendLine(themes[i].Description);

        return template
            .Replace("{POST_TITLE}", postTitle ?? string.Empty)
            .Replace("{POST_SUMMARY}", postSummary ?? string.Empty)
            .Replace("{IMAGE_THEMES}", sb.ToString().TrimEnd());
    }

    /// <summary>
    /// Pure selection step. Assigns the best <em>distinct, non-duplicate</em> image to each theme via a
    /// maximum-cardinality matching (so every theme that <em>can</em> be covered by a distinct image
    /// <em>is</em> — a strong theme never "steals" the sole decent image of a weaker theme), fills
    /// remaining slots with the next-best images (by max-across-themes score), and marks the single
    /// highest-scoring image as featured. Only images scoring strictly above <paramref name="minRelevance"/>
    /// are ever selected — an irrelevant image is never used to pad a theme, so fewer than
    /// <paramref name="count"/> images may be returned. Dedup is best-effort: it is relaxed (but the
    /// relevance floor is not) before the result is allowed to shrink.
    /// <para>
    /// <paramref name="coverageFloor"/> gates <em>theme coverage</em> only: a theme is worth covering
    /// (given its own distinct image) only when some image scores strictly above it, so slots are spent
    /// on a distinct theme in preference to a second/third image of an already-covered theme. It is
    /// independent of <paramref name="minRelevance"/> (which still governs the featured pick and the
    /// leftover-slot fill); a theme with no image above the floor is left for the fill stage rather than
    /// covered with a weak image.
    /// </para>
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
    /// (<c>visionScore + <paramref name="featuredWeight"/> × affinity</c>). An image with a non-zero
    /// affinity is also <em>exempt from the <paramref name="minRelevance"/> floor</em>, so a subject the
    /// author explicitly tagged can still be used even when the vision model scores it low (it competes on
    /// the affinity-blended fill ordering and is still deduped). Theme coverage stays purely vision-driven;
    /// affinity defaults to 0 for any path not in the map, so an absent map reproduces the pure-vision
    /// behaviour exactly.
    /// </para>
    /// </summary>
    public static IReadOnlyList<SelectedImage> Select(
        IReadOnlyList<ScoredImage> scored, IReadOnlyList<string> themes, int count, int hammingThreshold,
        double minRelevance = 0.0,
        IReadOnlySet<ulong>? recentFeaturedHashes = null,
        int recentFeaturedThreshold = AppLimits.DefaultRecentFeaturedHammingThreshold,
        IReadOnlyDictionary<string, double>? userTagAffinity = null,
        double selectionWeight = AppLimits.DefaultUserTagSelectionWeight,
        double featuredWeight = AppLimits.DefaultUserTagFeaturedWeight,
        double coverageFloor = 0.0)
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

        // Author-tag-matched images (affinity > 0) are exempt from the relevance floor: when the author
        // explicitly tagged a subject via [TAGS:], an image carrying that tag should be usable even if the
        // vision model scored it low against the generated themes — otherwise the tag the author typed can
        // be silently vetoed. Such images still compete on the affinity-blended fill ordering below and are
        // still subject to dedup, so this widens eligibility without forcing an irrelevant duplicate in.
        bool Eligible(int img) => MaxScore(img) > minRelevance || Aff(img) > 0;

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

        // 1-2) Per-theme coverage via a maximum-cardinality matching over eligible (image, theme) pairs
        //      (score strictly above the coverage floor). This guarantees every coverable theme gets a
        //      distinct image — a strong theme can't monopolise the sole decent image of a weaker one —
        //      unlike a greedy best-first pass, which would lock that shared image to the strong theme.
        foreach (var (img, theme) in AssignThemes(scored, themeCount, count, coverageFloor, hammingThreshold))
        {
            if (chosen.Count >= count) break;
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
    /// Assigns a distinct image to as many themes as possible via a maximum-cardinality bipartite matching
    /// (Kuhn's augmenting paths) over eligible pairs — an image is eligible for a theme when it scores
    /// strictly above <paramref name="coverageFloor"/> there. Each theme's candidates are tried in
    /// score-descending order to bias the (max-cardinality) matching toward higher scores. The result is
    /// returned score-desc; near-perceptual-duplicate cover picks are reconciled away (the lower-scored of
    /// a colliding pair is dropped, leaving that theme for the fill stage), and at most
    /// <paramref name="count"/> assignments are returned.
    /// </summary>
    private static IReadOnlyList<(int Img, int Theme)> AssignThemes(
        IReadOnlyList<ScoredImage> scored, int themeCount, int count,
        double coverageFloor, int hammingThreshold)
    {
        double ScoreOf(int img, int theme) =>
            theme < scored[img].Scores.Count ? scored[img].Scores[theme] : 0.0;

        // Eligible images per theme, score-desc then path for a deterministic augmenting order.
        var adj = new List<int>[themeCount];
        for (int t = 0; t < themeCount; t++)
        {
            int theme = t;
            adj[t] = Enumerable.Range(0, scored.Count)
                .Where(img => ScoreOf(img, theme) > coverageFloor)
                .OrderByDescending(img => ScoreOf(img, theme))
                .ThenBy(img => scored[img].Path, StringComparer.Ordinal)
                .ToList();
        }

        var imageToTheme = new int[scored.Count];
        Array.Fill(imageToTheme, -1);

        bool TryAssign(int theme, bool[] seen)
        {
            foreach (int img in adj[theme])
            {
                if (seen[img]) continue;
                seen[img] = true;
                if (imageToTheme[img] == -1 || TryAssign(imageToTheme[img], seen))
                {
                    imageToTheme[img] = theme;
                    return true;
                }
            }
            return false;
        }

        for (int t = 0; t < themeCount; t++)
            TryAssign(t, new bool[scored.Count]);

        // Score-desc so the strongest covers win the slot budget and are kept during dedup reconciliation.
        var assigned = Enumerable.Range(0, scored.Count)
            .Where(img => imageToTheme[img] != -1)
            .Select(img => (Img: img, Theme: imageToTheme[img]))
            .OrderByDescending(a => ScoreOf(a.Img, a.Theme))
            .ThenBy(a => scored[a.Img].Path, StringComparer.Ordinal);

        var kept = new List<(int Img, int Theme)>();
        foreach (var a in assigned)
        {
            if (kept.Count >= count) break;
            if (kept.Any(k => PerceptualHash.HammingDistance(scored[k.Img].Hash, scored[a.Img].Hash) <= hammingThreshold))
                continue; // near-duplicate of an already-kept cover — leave this theme for the fill stage
            kept.Add(a);
        }
        return kept;
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
