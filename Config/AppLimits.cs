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

    /// <summary>Default number of images attached to a post (including the featured image).</summary>
    public const int DefaultImagesPerPost = 3;

    /// <summary>Cap on the LLM response length we will attempt to parse (defensive).</summary>
    public const int MaxLlmResponseLength = 1 * 1024 * 1024; // 1 MB
}
