using WPAIPoster.BlogPost;
using WPAIPoster.Llm;
using WPAIPoster.Mcp;
using WPAIPoster.Config;

namespace WPAIPoster.Tests;

public class WebResearchTests
{
    [Fact]
    public void PromptFileNameFor_McpKey_UsesKeyStem()
    {
        Assert.Equal("duck-duck-go-search.json", WebResearch.PromptFileNameFor("duck-duck-go-search"));
    }

    [Fact]
    public void PromptFileNameFor_SanitizesUnsafeKey()
    {
        Assert.Equal("web-search.json", WebResearch.PromptFileNameFor("Web Search/../"));
    }

    [Fact]
    public void BuildPrompt_SubstitutesBriefUrlsAndPages()
    {
        string prompt = WebResearch.BuildPrompt(
            "Brief: {BRIEF}\nURLS:\n{URLS}\nCONTENT:\n{PAGE_CONTENT}",
            "write about MCP",
            new[] { "https://a.test", "https://b.test" },
            new List<(string Url, string Content)> { ("https://a.test", "A page"), ("https://b.test", "B page") });

        Assert.Contains("Brief: write about MCP", prompt);
        Assert.Contains("- https://a.test", prompt);
        Assert.Contains("URL: https://a.test", prompt);
        Assert.Contains("A page", prompt);
        Assert.DoesNotContain("{BRIEF}", prompt);
        Assert.DoesNotContain("{URLS}", prompt);
        Assert.DoesNotContain("{PAGE_CONTENT}", prompt);
    }

    [Fact]
    public void Truncate_CapsWithEllipsis()
    {
        Assert.Equal("12345…", WebResearch.Truncate("1234567890", 6));
        Assert.Equal("short", WebResearch.Truncate("short", 10));
        Assert.Equal(string.Empty, WebResearch.Truncate("long", 0));
    }

    [Fact]
    public async Task ResearchAsync_FetchesPagesAndSummarizes()
    {
        var fetcher = new FakeMcpPageFetcher(new Dictionary<string, string?>
        {
            ["https://a.test"] = "A page",
            ["https://b.test"] = "B page"
        });
        var llm = new FakeLlmClient("Summary from pages");
        var researcher = new WebResearch(llm, fetcher, "Brief: {BRIEF}\nURLS:\n{URLS}\nCONTENT:\n{PAGE_CONTENT}");

        string? summary = await researcher.ResearchAsync(
            "write about MCP",
            new[] { "https://a.test", "https://b.test", "https://c.test" },
            2,
            AppLimits.WebResearchMaxChars);

        Assert.Equal("Summary from pages", summary);
        Assert.Single(llm.Prompts);
        Assert.Contains("write about MCP", llm.Prompts[0]);
        Assert.Contains("A page", llm.Prompts[0]);
        Assert.Contains("B page", llm.Prompts[0]);
        Assert.DoesNotContain("c.test", llm.Prompts[0]);
    }

    [Fact]
    public async Task ResearchAsync_NoUrls_ReturnsNull()
    {
        var llm = new FakeLlmClient("unused");
        var fetcher = new FakeMcpPageFetcher(new Dictionary<string, string?>());
        var researcher = new WebResearch(llm, fetcher, "{PAGE_CONTENT}");

        string? summary = await researcher.ResearchAsync("brief", Array.Empty<string>(), 3, AppLimits.WebResearchMaxChars);

        Assert.Null(summary);
        Assert.Empty(llm.Prompts);
    }

    [Fact]
    public async Task ResearchAsync_AllFetchesFail_ReturnsNull()
    {
        var llm = new FakeLlmClient("unused");
        var fetcher = new ThrowingMcpPageFetcher(new Exception("down"));
        var researcher = new WebResearch(llm, fetcher, "{PAGE_CONTENT}");

        string? summary = await researcher.ResearchAsync("brief", new[] { "https://a.test" }, 3, AppLimits.WebResearchMaxChars);

        Assert.Null(summary);
        Assert.Empty(llm.Prompts);
    }

    [Fact]
    public async Task ResearchAsync_TruncatesSummary()
    {
        var llm = new FakeLlmClient("0123456789");
        var fetcher = new FakeMcpPageFetcher(new Dictionary<string, string?> { ["https://a.test"] = "A page" });
        var researcher = new WebResearch(llm, fetcher, "{PAGE_CONTENT}");

        string? summary = await researcher.ResearchAsync("brief", new[] { "https://a.test" }, 3, 6);

        Assert.Equal("01234…", summary);
    }
}
