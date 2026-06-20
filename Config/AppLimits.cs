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

    /// <summary>
    /// Default max Hamming distance (over the 64-bit perceptual dHash) at which two images are treated
    /// as near-identical and not both selected. ~0-6 ≈ visually the same image.
    /// </summary>
    public const int DefaultImageDedupThreshold = 6;

    /// <summary>Maximum number of post tags applied to a published post.</summary>
    public const int MaxPostTags = 5;

    /// <summary>Default category applied when the model returns none.</summary>
    public const string DefaultCategory = "Blog";

    /// <summary>Cap on the LLM response length we will attempt to parse (defensive).</summary>
    public const int MaxLlmResponseLength = 1 * 1024 * 1024; // 1 MB
}
