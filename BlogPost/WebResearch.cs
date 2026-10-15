using WPAIPoster.Config;
using WPAIPoster.Llm;
using WPAIPoster.Mcp;
using WPAIPoster.Prompts;

namespace WPAIPoster.BlogPost;

/// <summary>
/// Fetches a small number of brief URLs through an MCP page fetcher, asks the text model to summarize the
/// fetched pages, and returns capped research notes for the blog prompt. Pure helpers are unit-tested; the
/// orchestration is exercised with fake fetcher/LLM clients.
/// </summary>
public sealed class WebResearch(ILlmClient client, IMcpPageFetcher fetcher, string promptTemplate)
{
    public static WebResearch Create(ILlmClient client, IMcpPageFetcher fetcher, string promptFileName)
        => new(client, fetcher, PromptLoader.Load(promptFileName).GetPromptText());

    public static string PromptFileNameFor(string key)
    {
        string s = key.Trim().ToLowerInvariant();
        var chars = new List<char>();
        bool pendingDash = false;

        foreach (char c in s)
        {
            if (char.IsLetterOrDigit(c))
            {
                if (pendingDash && chars.Count > 0)
                    chars.Add('-');
                chars.Add(c);
                pendingDash = false;
            }
            else if (c is '-' or '_' or ' ' or '.' or '/' or '\\')
            {
                pendingDash = true;
            }
        }

        string stem = string.Concat(chars).Trim('-');
        if (stem.Length == 0)
            stem = "web-research";

        return $"{stem}.json";
    }

    public static string BuildPrompt(
        string template, string? brief, IReadOnlyList<string> urls,
        IReadOnlyList<(string Url, string Content)> pages)
    {
        string briefText = string.IsNullOrWhiteSpace(brief) ? "(none)" : brief.Trim();

        string urlList = urls.Count > 0
            ? string.Join("\n", urls.Select(u => $"- {u}"))
            : "(none)";

        string content = pages.Count > 0
            ? string.Join("\n\n", pages.Select(p => $"URL: {p.Url}\nContent:\n{p.Content.Trim()}"))
            : "(no page content fetched)";

        return template
            .Replace("{BRIEF}", briefText)
            .Replace("{URLS}", urlList)
            .Replace("{PAGE_CONTENT}", content);
    }

    public static string Truncate(string? text, int maxChars)
    {
        if (text is null)
            return string.Empty;
        if (maxChars <= 0)
            return string.Empty;
        if (text.Length <= maxChars)
            return text;
        return maxChars == 1 ? "…" : text[..(maxChars - 1)] + "…";
    }

    public async Task<string?> ResearchAsync(string? brief, IReadOnlyList<string> urls, int maxUrls, int maxChars)
    {
        int take = Math.Max(0, maxUrls);
        if (urls.Count == 0 || take == 0)
            return null;

        var selected = urls.Take(take).ToList();
        var pages = new List<(string Url, string Content)>();

        foreach (string url in selected)
        {
            try
            {
                string? content = await fetcher.FetchContentAsync(url, AppLimits.McpFetchMaxLength);
                if (content is { Length: > 0 })
                    pages.Add((url, content.Trim()));
            }
            catch (Exception)
            {
                // Best-effort per URL: a dead source should not block the post.
            }
        }

        if (pages.Count == 0)
            return null;

        string prompt = BuildPrompt(promptTemplate, brief, selected, pages);
        string? reply = await client.SendAsync(prompt, null, null);
        string summary = Truncate(reply, maxChars);

        return summary.Length == 0 ? null : summary;
    }
}
