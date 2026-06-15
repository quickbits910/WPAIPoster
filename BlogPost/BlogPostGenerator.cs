using WPAIPoster.Llm;
using WPAIPoster.Prompts;

namespace WPAIPoster.BlogPost;

/// <summary>
/// Turns a user brief into a <see cref="BlogPostResult"/> by filling the blog-post prompt template
/// and sending it to the configured <see cref="ILlmClient"/>.
/// </summary>
public sealed class BlogPostGenerator(ILlmClient client, string promptTemplate)
{
    /// <summary>Substitutes the template tokens. Pure and side-effect free for easy testing.</summary>
    public static string BuildPrompt(string template, string userInput, string existingPostsText)
    {
        string posts = string.IsNullOrWhiteSpace(existingPostsText)
            ? "(none available)"
            : existingPostsText;

        return template
            .Replace("{USER_INPUT}", userInput)
            .Replace("{EXISTING_POSTS}", posts);
    }

    /// <summary>Convenience constructor that loads the bundled blog-post prompt template.</summary>
    public static BlogPostGenerator Create(ILlmClient client)
        => new(client, PromptLoader.Load(PromptLoader.BlogPostPromptFile).GetPromptText());

    /// <summary>Generates and parses a blog post. <paramref name="existingPostsText"/> may be empty.</summary>
    public async Task<BlogPostResult> GenerateAsync(string userInput, string existingPostsText)
    {
        string prompt = BuildPrompt(promptTemplate, userInput, existingPostsText);
        string? raw = await client.SendAsync(prompt, null, null);
        return BlogPostParser.Parse(raw);
    }
}
