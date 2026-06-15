using System.Text;
using System.Text.Json;

namespace WPAIPoster.Wordpress;

/// <summary>A published post used as internal-link context for the model.</summary>
public sealed record ExistingPost(string Id, string Title, string Url);

/// <summary>
/// Fetches the list of published posts over SSH (<c>wp post list</c>) and formats it for the prompt.
/// Parsing is separated from I/O so it can be unit-tested with canned JSON.
/// </summary>
public sealed class ExistingPostsFetcher(ISshRunner runner, string wordPressFolder)
{
    /// <summary>Returns published posts, or an empty list if the command fails or returns nothing.</summary>
    public IReadOnlyList<ExistingPost> Fetch(int limit = 200)
    {
        string cmd = WpCliCommands.InFolder(wordPressFolder, WpCliCommands.ListPublishedPosts(limit));
        SshCommandResult result = runner.Run(cmd);
        if (!result.Success || string.IsNullOrWhiteSpace(result.StdOut))
            return Array.Empty<ExistingPost>();

        return Parse(result.StdOut);
    }

    /// <summary>Parses <c>wp post list --format=json</c> output. Tolerant of numeric or string IDs.</summary>
    public static IReadOnlyList<ExistingPost> Parse(string json)
    {
        var posts = new List<ExistingPost>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return posts;

            foreach (JsonElement el in doc.RootElement.EnumerateArray())
            {
                string id = GetString(el, "ID");
                string title = GetString(el, "post_title");
                string url = GetString(el, "url");
                if (title.Length > 0 || url.Length > 0)
                    posts.Add(new ExistingPost(id, title, url));
            }
        }
        catch (JsonException)
        {
            // Unparseable output — treat as no posts.
        }

        return posts;
    }

    /// <summary>Renders posts as one "Title — URL" line each for inclusion in the prompt.</summary>
    public static string FormatForPrompt(IReadOnlyList<ExistingPost> posts)
    {
        if (posts.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        foreach (ExistingPost p in posts)
            sb.AppendLine($"- {p.Title} — {p.Url}");
        return sb.ToString().TrimEnd();
    }

    private static string GetString(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out JsonElement v))
            return string.Empty;

        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? string.Empty,
            JsonValueKind.Number => v.GetRawText(),
            _ => string.Empty
        };
    }
}
