using System.Text.Json;
using System.Text.Json.Serialization;

namespace WPAIPoster.Config;

/// <summary>
/// SEO plugin meta keys used to write the SEO meta title/description as post meta.
/// Defaults target Yoast SEO. When the whole object is null, the SEO meta title falls
/// back to the post title and the meta description to the post excerpt only.
/// </summary>
public sealed class SeoMetaKeys
{
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }
}

/// <summary>
/// User-facing runtime configuration loaded from <c>app.settings.json</c>.
/// Nullable properties mean "not set — fall through to the CLI argument or hard-coded default".
/// Priority: settings file &gt; CLI argument &gt; hard-coded default.
/// </summary>
public sealed class AppSettings
{
    /// <summary>LLM provider. Supported: lmstudio | ollama | openai-compatible | openai | anthropic</summary>
    [JsonPropertyName("provider")]
    public string? Provider { get; set; }

    /// <summary>Model used to write the blog post text.</summary>
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    /// <summary>Vision-capable model used to score library images for relevance. Falls back to <see cref="Model"/>.</summary>
    [JsonPropertyName("visionModel")]
    public string? VisionModel { get; set; }

    /// <summary>Base URL override for the provider endpoint.</summary>
    [JsonPropertyName("baseUrl")]
    public string? BaseUrl { get; set; }

    /// <summary>API key. Prefer ANTHROPIC_API_KEY / OPENAI_API_KEY environment variables over storing here.</summary>
    [JsonPropertyName("apiKey")]
    public string? ApiKey { get; set; }

    /// <summary>Absolute path to the local folder of candidate images.</summary>
    [JsonPropertyName("imageLibrary")]
    public string? ImageLibrary { get; set; }

    /// <summary>When true, the post is published immediately; when false/null it is created as a draft.</summary>
    [JsonPropertyName("autoPublish")]
    public bool? AutoPublish { get; set; }

    /// <summary>Remote folder (relative to the SSH login directory, or absolute) holding the WordPress install.</summary>
    [JsonPropertyName("wordPressFolder")]
    public string? WordPressFolder { get; set; }

    /// <summary>Maximum number of library images to send to the vision model per post. Null uses the default.</summary>
    [JsonPropertyName("maxImagesToScore")]
    public int? MaxImagesToScore { get; set; }

    /// <summary>How many images to attach to the post (including the featured image). Null uses the default.</summary>
    [JsonPropertyName("imagesPerPost")]
    public int? ImagesPerPost { get; set; }

    /// <summary>Maximum number of library images to read tags from when indexing. Null uses the default (1000).</summary>
    [JsonPropertyName("maxImagesToIndex")]
    public int? MaxImagesToIndex { get; set; }

    /// <summary>Keyword prefix ImageTagger writes (e.g. "AI."); stripped before tag matching. Null uses the default.</summary>
    [JsonPropertyName("tagPrefix")]
    public string? TagPrefix { get; set; }

    /// <summary>Cap on the tag-matched shortlist sent to the tag-selection model. Null uses the default (40).</summary>
    [JsonPropertyName("tagCandidateLimit")]
    public int? TagCandidateLimit { get; set; }

    /// <summary>
    /// Max perceptual-hash Hamming distance at which two selected images are treated as near-duplicates
    /// and not both attached. Higher = more aggressive dedup. Null uses the default (6).
    /// </summary>
    [JsonPropertyName("imageDedupThreshold")]
    public int? ImageDedupThreshold { get; set; }

    /// <summary>
    /// Minimum vision relevance score [0.00-1.00] an image must exceed to be attached. Images at or below
    /// this are never selected. Null uses the default (0.0, which drops only zero-scoring images).
    /// </summary>
    [JsonPropertyName("minImageRelevance")]
    public double? MinImageRelevance { get; set; }

    /// <summary>Category applied when the model returns none. Null uses the default ("Blog").</summary>
    [JsonPropertyName("defaultCategory")]
    public string? DefaultCategory { get; set; }

    /// <summary>When true, an Editor LLM scores the draft and drives rewrites below the threshold. Default false.</summary>
    [JsonPropertyName("enableEditorReviewer")]
    public bool? EnableEditorReviewer { get; set; }

    /// <summary>Minimum Editor score [0.00-1.00] a draft must reach to be accepted. Null uses the default (0.80).</summary>
    [JsonPropertyName("editorReviewerThreshold")]
    public double? EditorReviewerThreshold { get; set; }

    /// <summary>Optional SEO plugin meta keys for writing the SEO meta title/description.</summary>
    [JsonPropertyName("seoMetaKeys")]
    public SeoMetaKeys? SeoMetaKeys { get; set; }

    /// <summary>Folder where each run writes its log file. Null uses the default ("./Output").</summary>
    [JsonPropertyName("outputFolder")]
    public string? OutputFolder { get; set; }

    /// <summary>Absolute path of the settings file that was loaded, or null if none was found.</summary>
    [JsonIgnore]
    public string? LoadedFrom { get; private set; }

    // ---- Loading ----

    private const string FileName = "app.settings.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Searches for <c>app.settings.json</c> in the current working directory, then in the
    /// application base directory, and loads the first one found. Returns an all-null instance if none.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
    public static AppSettings Load()
    {
        string cwd = Path.Combine(Directory.GetCurrentDirectory(), FileName);
        string base_ = Path.Combine(AppContext.BaseDirectory, FileName);

        string? found = File.Exists(cwd) ? cwd :
                        File.Exists(base_) ? base_ : null;

        return Load(found);
    }

    /// <summary>
    /// Loads settings from an explicit <paramref name="path"/>.
    /// Returns an all-null instance when <paramref name="path"/> is null or the file does not exist.
    /// </summary>
    public static AppSettings Load(string? path)
    {
        if (path is null || !File.Exists(path))
            return new AppSettings();

        try
        {
            string json = File.ReadAllText(path);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions)
                           ?? new AppSettings();
            settings.LoadedFrom = path;
            return settings;
        }
        catch
        {
            Console.WriteLine("Warning: app.settings.json could not be parsed — using defaults.");
            return new AppSettings();
        }
    }
}
