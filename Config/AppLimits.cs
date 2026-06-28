namespace WPAIPoster.Config;

public static class AppLimits
{
    /// <summary>Hard cap on the size of each image attached to a post (WordPress-friendly).</summary>
    public const long MaxImageBytes = 500 * 1024;          // 500 KB per image

    /// <summary>Longest-side pixel dimension used when down-scaling an image to meet the size cap.</summary>
    public const int MaxImageDimension = 2016;             // px

    /// <summary>Longest-side pixel dimension of the thumbnail sent to the vision model for relevance scoring.</summary>
    public const int VisionThumbnailDimension = 768;       // px — small + fast for scoring

    /// <summary>Default number of library images sent to the vision model per post.</summary>
    public const int DefaultMaxImagesToScore = 60;

    /// <summary>Default number of images attached to a post (best under H1, then under 2nd/3rd H2, then bottom).</summary>
    public const int DefaultImagesPerPost = 4;

    /// <summary>Default maximum number of library images to read tags from when indexing.</summary>
    public const int DefaultMaxImagesToIndex = 1000;

    /// <summary>Default prefix ImageTagger writes on keywords; stripped before tag matching.</summary>
    public const string DefaultTagPrefix = "AI.";

    /// <summary>Default cap on the tag-matched shortlist sent to the tag-selection model.</summary>
    public const int DefaultTagCandidateLimit = 40;

    // Per-source weights used when ranking library images for the vision-scoring candidate set
    // (see TagMatcher.Rank / TagBasedImageSelector). Priority: author-supplied tags > post tags >
    // image themes > categories > the post's H1/body text (lowest, background signal only).

    /// <summary>Weight of author-supplied tags (the brief's <c>[TAGS: …]</c> directive) in image ranking.</summary>
    public const int TagWeightUserProvided = 5;

    /// <summary>Weight of the post's tags in image ranking.</summary>
    public const int TagWeightTags = 4;

    /// <summary>Weight of the image-theme subjects in image ranking.</summary>
    public const int TagWeightThemes = 3;

    /// <summary>Weight of the post's categories in image ranking.</summary>
    public const int TagWeightCategories = 2;

    /// <summary>Weight of the post's H1 + body text in image ranking (lowest — background signal).</summary>
    public const int TagWeightBodyBackground = 1;

    /// <summary>
    /// How much an image's author-tag affinity (fraction of <c>[TAGS:]</c> matched, 0-1) boosts its
    /// vision score when filling leftover image slots. Modest — vision relevance still dominates.
    /// </summary>
    public const double DefaultUserTagSelectionWeight = 0.25;

    /// <summary>
    /// How much an image's author-tag affinity (0-1) weighs in the featured-image blend
    /// (<c>visionScore + weight × affinity</c>). Larger than the selection weight so author tags pull
    /// harder on the hero pick, but not so large that a far-better vision match is overridden.
    /// </summary>
    public const double DefaultUserTagFeaturedWeight = 0.50;

    /// <summary>
    /// Default max Hamming distance (over the 64-bit perceptual dHash) at which two images are treated
    /// as near-identical and not both selected. ~0-6 ≈ visually the same image.
    /// </summary>
    public const int DefaultImageDedupThreshold = 6;

    /// <summary>
    /// Default minimum vision relevance score an image must exceed to be attached. Images scoring at or
    /// below this are never selected (a theme is left uncovered rather than padded with an irrelevant
    /// image). 0.0 drops only zero-scoring images; raise it to be stricter.
    /// </summary>
    public const double DefaultMinImageRelevance = 0.0;

    /// <summary>
    /// When true, the featured image is steered away from any image matching a recent post's featured
    /// image (by perceptual hash), so consecutive posts don't reuse the same hero image.
    /// </summary>
    public const bool DefaultAvoidRecentFeaturedImages = true;

    /// <summary>Default number of recent published posts whose featured images are avoided.</summary>
    public const int DefaultRecentFeaturedHistoryCount = 10;

    /// <summary>
    /// Default max Hamming distance at which a candidate is treated as the SAME image as a recent
    /// featured one (and therefore not re-featured). Tighter than the in-post dedup threshold — we want
    /// "the same hero image", not merely "similar". ~0-4 ≈ the same image after WP's re-encode.
    /// </summary>
    public const int DefaultRecentFeaturedHammingThreshold = 4;

    /// <summary>Maximum number of post tags applied to a published post.</summary>
    public const int MaxPostTags = 5;

    /// <summary>Default category applied when the model returns none.</summary>
    public const string DefaultCategory = "Blog";

    /// <summary>Default minimum Editor score [0,1] a draft must reach to be accepted without a rewrite.</summary>
    public const double DefaultEditorReviewerThreshold = 0.80;

    /// <summary>Maximum number of Editor-driven rewrites before publishing the best draft anyway.</summary>
    public const int MaxEditorRevisions = 2;

    /// <summary>Default folder (relative to the working directory) where per-run log files are written.</summary>
    public const string DefaultOutputFolder = "Output";

    /// <summary>Cap on the LLM response length we will attempt to parse (defensive).</summary>
    public const int MaxLlmResponseLength = 1 * 1024 * 1024; // 1 MB
}
