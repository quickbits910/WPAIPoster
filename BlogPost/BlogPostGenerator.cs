using WPAIPoster.Llm;
using WPAIPoster.Prompts;

namespace WPAIPoster.BlogPost;

/// <summary>
/// Turns a user brief into a <see cref="BlogPostResult"/> by filling the blog-post prompt template
/// and sending it to the configured <see cref="ILlmClient"/>.
/// </summary>
public sealed class BlogPostGenerator(ILlmClient client, string promptTemplate)
{
    /// <summary>
    /// Substitutes the template tokens. Pure and side-effect free for easy testing. When
    /// <paramref name="editorFeedback"/> is supplied (a rewrite), the Editor's notes are appended with
    /// an instruction to address them.
    /// </summary>
    public static string BuildPrompt(
        string template, string userInput, string existingPostsText, string? editorFeedback = null,
        IReadOnlyList<string>? sourceLinks = null)
    {
        string posts = string.IsNullOrWhiteSpace(existingPostsText)
            ? "(none available)"
            : existingPostsText;

        string links = sourceLinks is { Count: > 0 }
            ? string.Join("\n", sourceLinks.Select(u => $"- {u}"))
            : "(none)";

        string prompt = template
            .Replace("{USER_INPUT}", userInput)
            .Replace("{EXISTING_POSTS}", posts)
            .Replace("{SOURCE_LINKS}", links);

        if (!string.IsNullOrWhiteSpace(editorFeedback))
            prompt += "\n\n--- Editor revision notes (this is a rewrite; address ALL of these) ---\n"
                      + editorFeedback.Trim();

        return prompt;
    }

    /// <summary>Convenience constructor that loads the bundled blog-post prompt template.</summary>
    public static BlogPostGenerator Create(ILlmClient client)
        => new(client, PromptLoader.Load(PromptLoader.BlogPostPromptFile).GetPromptText());

    /// <summary>
    /// Generates and parses a blog post. <paramref name="existingPostsText"/> may be empty. Pass
    /// <paramref name="editorFeedback"/> to request a rewrite that addresses the Editor's notes.
    /// </summary>
    public async Task<BlogPostResult> GenerateAsync(
        string userInput, string existingPostsText, string? editorFeedback = null,
        IReadOnlyList<string>? sourceLinks = null)
    {
        string prompt = BuildPrompt(promptTemplate, userInput, existingPostsText, editorFeedback, sourceLinks);
        string? raw = await client.SendAsync(prompt, null, null);
        return BlogPostParser.Parse(raw);
    }
}
