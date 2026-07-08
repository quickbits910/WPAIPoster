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

    // ---- Weighted Rank ----

    [Fact]
    public void TokenizeWords_AppliesSameStopwordAndLengthFilter()
    {
        var tokens = TagMatcher.TokenizeWords(new[] { "Artificial Intelligence", "ai-agents", "an" });
        Assert.Contains("artificial", tokens);
        Assert.Contains("intelligence", tokens);
        Assert.Contains("agents", tokens);   // split on '-'
        Assert.DoesNotContain("ai", tokens);  // < 3 chars
        Assert.DoesNotContain("an", tokens);   // stopword/short
    }

    [Fact]
    public void WeightedRank_HigherWeightSourceOutranksLowerWeightSource()
    {
        var t = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var catalog = new ImageTagCatalog(new List<TaggedImage>
        {
            new("/user.jpg", new[] { "agent" }, t),     // matches the weight-5 group only
            new("/cat.jpg", new[] { "software" }, t),   // matches the weight-2 group only
        });

        var groups = new[]
        {
            new TagMatcher.WeightedTokens(TagMatcher.TokenizeWords(new[] { "agent" }), 5),
            new TagMatcher.WeightedTokens(TagMatcher.TokenizeWords(new[] { "software" }), 2),
        };

        var ranked = TagMatcher.Rank(catalog, groups, 5);
        Assert.Equal(new[] { "/user.jpg", "/cat.jpg" }, ranked.Select(r => r.Path));
    }

    [Fact]
    public void WeightedRank_SumsPerTagMaxWeight()
    {
        var t = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var catalog = new ImageTagCatalog(new List<TaggedImage>
        {
            // two tags: one matches the weight-2 group, one matches the weight-5 group → score 7
            new("/both.jpg", new[] { "software", "agent" }, t),
            // one tag matching weight-5 only → score 5
            new("/one.jpg", new[] { "agent" }, t),
        });

        var groups = new[]
        {
            new TagMatcher.WeightedTokens(TagMatcher.TokenizeWords(new[] { "agent" }), 5),
            new TagMatcher.WeightedTokens(TagMatcher.TokenizeWords(new[] { "software" }), 2),
        };

        var ranked = TagMatcher.Rank(catalog, groups, 5);
        Assert.Equal(new[] { "/both.jpg", "/one.jpg" }, ranked.Select(r => r.Path));
    }

    [Fact]
    public void WeightedRank_NoGroups_ReturnsEmpty()
    {
        var catalog = new ImageTagCatalog(new List<TaggedImage>
        {
            new("/a.jpg", new[] { "mountain" }, DateTime.UtcNow),
        });
        Assert.Empty(TagMatcher.Rank(catalog, Array.Empty<TagMatcher.WeightedTokens>(), 5));
    }

    // ---- RankPerTheme (per-theme balance) ----

    [Fact]
    public void RankPerTheme_TagDenseThemeCannotCrowdOutThinTheme()
    {
        var t = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        // A "code"-dominated library with a single "server" image — the exact shape that collapses a
        // global top-N onto one theme. Per-theme ranking must still surface the lone server image.
        var catalog = new ImageTagCatalog(new List<TaggedImage>
        {
            new("/code1.jpg", new[] { "code", "software" }, t),
            new("/code2.jpg", new[] { "code", "developer" }, t),
            new("/code3.jpg", new[] { "coding", "programming" }, t),
            new("/server.jpg", new[] { "server", "datacenter" }, t),
        });

        var themeGroups = new IReadOnlyCollection<string>[]
        {
            TagMatcher.TokenizeWords(new[] { "server" }),
            TagMatcher.TokenizeWords(new[] { "code" }),
        };

        var perTheme = TagMatcher.RankPerTheme(catalog, themeGroups, Array.Empty<string>(), perThemeLimit: 3);

        Assert.Equal(2, perTheme.Count);
        Assert.Equal(new[] { "/server.jpg" }, perTheme[0].Select(i => i.Path));       // server theme
        Assert.Contains("/code1.jpg", perTheme[1].Select(i => i.Path));               // code theme
        Assert.DoesNotContain("/server.jpg", perTheme[1].Select(i => i.Path));
    }

    [Fact]
    public void RankPerTheme_CrossCutTokensBoostButThemeMatchesRankHigher()
    {
        var t = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var catalog = new ImageTagCatalog(new List<TaggedImage>
        {
            new("/theme.jpg", new[] { "mountain" }, t),   // matches the theme (weight 2)
            new("/author.jpg", new[] { "laptop" }, t),    // matches only the cross-cut author tag (weight 1)
        });

        var themeGroups = new IReadOnlyCollection<string>[] { TagMatcher.TokenizeWords(new[] { "mountain" }) };
        var crossCut = TagMatcher.TokenizeWords(new[] { "laptop" });

        var perTheme = TagMatcher.RankPerTheme(catalog, themeGroups, crossCut, perThemeLimit: 5);

        // Both surface under the theme, but the theme match outranks the cross-cut-only match.
        Assert.Equal(new[] { "/theme.jpg", "/author.jpg" }, perTheme[0].Select(i => i.Path));
    }

    [Fact]
    public void RankPerTheme_RespectsPerThemeLimit()
    {
        var t = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var catalog = new ImageTagCatalog(new List<TaggedImage>
        {
            new("/a.jpg", new[] { "code" }, t),
            new("/b.jpg", new[] { "code" }, t),
            new("/c.jpg", new[] { "code" }, t),
        });

        var themeGroups = new IReadOnlyCollection<string>[] { TagMatcher.TokenizeWords(new[] { "code" }) };
        var perTheme = TagMatcher.RankPerTheme(catalog, themeGroups, Array.Empty<string>(), perThemeLimit: 2);

        Assert.Equal(2, perTheme[0].Count);
    }

    // ---- MatchFraction (author-tag affinity) ----

    [Fact]
    public void MatchFraction_FractionOfDistinctTokensMatched()
    {
        // 1 of the 2 author tokens ("agent") is matched by the image's tags.
        Assert.Equal(0.5, TagMatcher.MatchFraction(new[] { "agent", "robot" }, new[] { "agent", "mcp" }), 3);
    }

    [Fact]
    public void MatchFraction_AllMatched_IsOne()
    {
        Assert.Equal(1.0, TagMatcher.MatchFraction(new[] { "agent", "mcp" }, new[] { "agent", "mcp" }), 3);
    }

    [Fact]
    public void MatchFraction_NoneMatched_IsZero()
    {
        Assert.Equal(0.0, TagMatcher.MatchFraction(new[] { "mountain" }, new[] { "agent", "mcp" }), 3);
    }

    [Fact]
    public void MatchFraction_EmptyTokens_IsZero()
    {
        Assert.Equal(0.0, TagMatcher.MatchFraction(new[] { "agent" }, Array.Empty<string>()), 3);
    }

    [Fact]
    public void MatchFraction_UsesFlexibleStemSubstringMatching()
    {
        // "agents" (image tag) matches the "agent" token via plural stem; "workflow|automation" drops
        // the attribute label so "automation" matches.
        var frac = TagMatcher.MatchFraction(
            new[] { "agents", "workflow|automation" }, new[] { "agent", "automation" });
        Assert.Equal(1.0, frac, 3);
    }
}
