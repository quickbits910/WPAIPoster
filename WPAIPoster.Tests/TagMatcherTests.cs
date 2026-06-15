using WPAIPoster.Images;

namespace WPAIPoster.Tests;

public class TagMatcherTests
{
    [Fact]
    public void Tokenize_DropsStopwordsAndShortTokens_StripsHtml()
    {
        var tokens = TagMatcher.Tokenize(
            "Speed Up Your WordPress Site",
            "<p>Caching <strong>is</strong> key.</p>",
            new[] { "dashboard" });

        Assert.Contains("speed", tokens);
        Assert.Contains("wordpress", tokens);
        Assert.Contains("caching", tokens);
        Assert.Contains("dashboard", tokens);
        Assert.DoesNotContain("up", tokens);      // < 3 chars
        Assert.DoesNotContain("your", tokens);    // stopword
        Assert.DoesNotContain("is", tokens);      // stopword + short
        Assert.DoesNotContain("p", tokens);       // HTML stripped
        Assert.DoesNotContain("strong", tokens);  // HTML tag name stripped
    }

    [Theory]
    [InlineData("mountain", "mountain", true)]
    [InlineData("mountain", "mountains", true)]   // plural stem
    [InlineData("cat", "cats", true)]
    [InlineData("shepherd", "german", false)]
    [InlineData("trail", "trails", true)]
    [InlineData("dog", "cat", false)]
    public void WordsMatch_FlexibleEqualityPluralSubstring(string a, string b, bool expected)
    {
        Assert.Equal(expected, TagMatcher.WordsMatch(a, b));
    }

    [Fact]
    public void TagWords_DropsAttributeLabelBeforePipe()
    {
        // ImageTagger writes "species|German Shepherd"; the label before '|' should be dropped.
        var words = TagMatcher.TagWords("species|German Shepherd").ToList();
        Assert.Contains("german", words);
        Assert.Contains("shepherd", words);
        Assert.DoesNotContain("species", words);
    }

    [Fact]
    public void Rank_OrdersByMatchCountThenNewest()
    {
        var older = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var newer = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var catalog = new ImageTagCatalog(new List<TaggedImage>
        {
            new("/a.jpg", new[] { "mountain", "sunset", "lake" }, newer), // matches mountain+sunset = 2
            new("/b.jpg", new[] { "laptop", "code" }, newer),             // 0 → excluded
            new("/c.jpg", new[] { "mountain", "trail" }, older),          // matches mountain+trail = 2
        });

        var tokens = TagMatcher.Tokenize("Mountain trips", "<p>sunset over the trail</p>", new[] { "mountain" });
        var ranked = TagMatcher.Rank(catalog, tokens, 5);

        Assert.Equal(2, ranked.Count);
        Assert.Equal("/a.jpg", ranked[0].Path); // tie on score (2,2) → newer first
        Assert.Equal("/c.jpg", ranked[1].Path);
    }

    [Fact]
    public void Rank_NoTokens_ReturnsEmpty()
    {
        var catalog = new ImageTagCatalog(new List<TaggedImage>
        {
            new("/a.jpg", new[] { "mountain" }, DateTime.UtcNow),
        });
        Assert.Empty(TagMatcher.Rank(catalog, Array.Empty<string>(), 5));
    }
}
