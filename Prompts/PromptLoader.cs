using System.Text.Json;
using System.Text.Json.Serialization;

namespace WPAIPoster.Prompts;

/// <summary>A prompt stored as an array of lines, joined with newlines (mirrors the ImageTagger format).</summary>
public sealed class PromptConfig
{
    [JsonPropertyName("prompt")]
    public List<string> Prompt { get; set; } = new();

    public string GetPromptText() => string.Join("\n", Prompt);
}

/// <summary>
/// Loads prompt JSON from the <c>Prompts/</c> folder next to the executable (then the current working
/// directory). The folder is copied to the build output, so prompts are end-user editable without a
/// rebuild. Guards against path traversal in the supplied file name.
/// </summary>
public static class PromptLoader
{
    public const string BlogPostPromptFile = "blog-post-prompt.json";
    public const string ImageRelevancePromptFile = "image-relevance-prompt.json";
    public const string TagToBodyPromptFile = "tag-to-blog-post-body-prompt.json";
    public const string EditorReviewerPromptFile = "editor-reviewer-prompt.json";

    public static PromptConfig Load(string fileName)
    {
        GuardFileName(fileName);

        var searched = new List<string>();
        foreach (string dir in DiskCandidates())
        {
            string path = Path.Combine(dir, fileName);
            searched.Add(path);
            if (File.Exists(path))
                return Deserialize(File.ReadAllText(path), fileName);
        }

        throw new FileNotFoundException(
            $"Prompt '{fileName}' not found. Looked in: {string.Join(", ", searched)}. " +
            "Ensure the Prompts folder sits next to the executable.");
    }

    /// <summary>Loads from an explicit prompts directory (used by tests).</summary>
    public static PromptConfig Load(string fileName, string promptsDir)
    {
        GuardFileName(fileName);

        string path = Path.Combine(promptsDir, fileName);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Prompt not found: {path}");

        return Deserialize(File.ReadAllText(path), fileName);
    }

    private static void GuardFileName(string fileName)
    {
        if (fileName.Contains("..") || fileName.Contains('/') || fileName.Contains('\\')
            || Path.IsPathRooted(fileName))
            throw new InvalidOperationException($"Unsafe prompt filename: '{fileName}'.");
    }

    private static IEnumerable<string> DiskCandidates()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "Prompts");
        yield return Path.Combine(Directory.GetCurrentDirectory(), "Prompts");
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private static PromptConfig Deserialize(string json, string fileName)
    {
        try
        {
            return JsonSerializer.Deserialize<PromptConfig>(json, JsonOptions)
                   ?? throw new InvalidOperationException($"Prompt file '{fileName}' is empty or invalid.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Prompt file '{fileName}' is not valid JSON ({ex.Message}). " +
                "Each entry must be a quoted JSON string; escape any double-quote inside it as \\\".", ex);
        }
    }
}
