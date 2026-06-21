using WPAIPoster.BlogPost;

namespace WPAIPoster.Tests;

public class BlogPostParserTests
{
    private const string Valid = """
        {
          "metaTitle": "Fast Sites",
          "metaDescription": "How to speed up WordPress.",
          "h1": "Speed Up Your WordPress Site",
          "bodyHtml": "<p>Intro.</p><h2>Problem</h2><p>Slow.</p>",
          "imageThemes": ["speed", "dashboard"],
          "internalLinks": [{ "anchor": "caching guide", "url": "https://x.test/caching" }],
          "cta": "Start now."
        }
        """;

    [Fact]
    public void Parse_CleanJson()
    {
        var post = BlogPostParser.Parse(Valid);

        Assert.Equal("Fast Sites", post.MetaTitle);
        Assert.Equal("Speed Up Your WordPress Site", post.H1);
        Assert.Contains("<h2>Problem</h2>", post.BodyHtml);
        Assert.Equal(2, post.ImageThemes.Count);
        Assert.Single(post.InternalLinks);
        Assert.Equal("https://x.test/caching", post.InternalLinks[0].Url);
        Assert.Equal("Start now.", post.Cta);
    }

    [Fact]
    public void Parse_FencedJson()
    {
        string fenced = "```json\n" + Valid + "\n```";
        var post = BlogPostParser.Parse(fenced);
        Assert.Equal("Fast Sites", post.MetaTitle);
    }

    [Fact]
    public void Parse_WithSurroundingProse()
    {
        string noisy = "Sure! Here is your post:\n" + Valid + "\nLet me know if you want changes.";
        var post = BlogPostParser.Parse(noisy);
        Assert.Equal("Speed Up Your WordPress Site", post.H1);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no json here at all")]
    [InlineData("{ this is not valid json }")]
    public void Parse_Invalid_Throws(string input)
    {
        Assert.Throws<FormatException>(() => BlogPostParser.Parse(input));
    }

    [Fact]
    public void Parse_BodyUnderAlternateKey()
    {
        // Model used "body" instead of "bodyHtml".
        var post = BlogPostParser.Parse("""
            { "metaTitle": "T", "h1": "H", "body": "<p>Alt body.</p>", "cta": "Go" }
            """);

        Assert.Equal("<p>Alt body.</p>", post.BodyHtml);
        Assert.Equal("H", post.H1);
    }

    [Fact]
    public void Parse_BodyAsArrayOfLines_Joined()
    {
        var post = BlogPostParser.Parse("""
            { "h1": "H", "content": ["<p>line 1</p>", "<p>line 2</p>"] }
            """);

        Assert.Equal("<p>line 1</p>\n<p>line 2</p>", post.BodyHtml);
    }

    [Fact]
    public void Parse_ImageThemesUnderAlternateKey()
    {
        var post = BlogPostParser.Parse("""
            { "h1": "H", "bodyHtml": "<p>x</p>", "themes": ["a", "b"] }
            """);

        Assert.Equal(new[] { "a", "b" }, post.ImageThemes.Select(t => t.Subject));
        Assert.Equal(new[] { "a", "b" }, post.ImageThemes.Select(t => t.Description)); // string falls back to both
    }

    [Fact]
    public void Parse_ImageThemes_AsObjects_ReadsSubjectAndDescription()
    {
        var post = BlogPostParser.Parse("""
            { "h1": "H", "bodyHtml": "<p>x</p>", "imageThemes": [
              { "subject": "network", "description": "interconnected computer network" },
              { "subject": "tree" }
            ] }
            """);

        Assert.Equal(new[] { "network", "tree" }, post.ImageThemes.Select(t => t.Subject));
        Assert.Equal("interconnected computer network", post.ImageThemes[0].Description);
        Assert.Equal("tree", post.ImageThemes[1].Description); // missing description falls back to subject
    }

    [Fact]
    public void Parse_ImageThemes_AsStrings_StillWorks()
    {
        var post = BlogPostParser.Parse("""
            { "h1": "H", "bodyHtml": "<p>x</p>", "imageThemes": ["speed", "dashboard"] }
            """);

        Assert.Equal(new[] { "speed", "dashboard" }, post.ImageThemes.Select(t => t.Subject));
    }

    [Fact]
    public void Parse_TagsAndCategories()
    {
        var post = BlogPostParser.Parse("""
            { "h1": "H", "bodyHtml": "<p>x</p>", "tags": ["aero", "cars"], "categories": ["Automotive"] }
            """);

        Assert.Equal(new[] { "aero", "cars" }, post.Tags);
        Assert.Equal(new[] { "Automotive" }, post.Categories);
    }

    [Fact]
    public void Parse_CategoriesUnderAlternateKey()
    {
        var post = BlogPostParser.Parse("""{ "h1": "H", "bodyHtml": "<p>x</p>", "category": ["News"] }""");
        Assert.Equal(new[] { "News" }, post.Categories);
    }

    [Fact]
    public void Parse_RepairsUnescapedQuotesInBody()
    {
        // bodyHtml contains an HTML attribute with unescaped double-quotes — invalid JSON as-is.
        const string raw = """{ "h1": "H", "bodyHtml": "<p>See <a href="https://x.test">here</a>.</p>" }""";
        var post = BlogPostParser.Parse(raw);

        Assert.Equal("H", post.H1);
        Assert.Contains("href=\"https://x.test\"", post.BodyHtml);
    }

    [Fact]
    public void Parse_RepairsBogusBackslashEscapesInBody()
    {
        // The model "escaped" single quotes as \' which is not a valid JSON escape; repair drops the backslash.
        const string raw = """{ "h1": "H", "bodyHtml": "<p>It\'s <a href=\'/x\'>here</a>.</p>" }""";
        var post = BlogPostParser.Parse(raw);

        Assert.Equal("H", post.H1);
        Assert.Contains("It's", post.BodyHtml);
        Assert.Contains("href='/x'", post.BodyHtml);
    }

    [Fact]
    public void Parse_PreservesValidEscapesInBody()
    {
        // A valid \" and \n must survive the repair pass intact.
        const string raw = """{ "h1": "H", "bodyHtml": "<p>A \"quote\" and a break.</p>" }""";
        var post = BlogPostParser.Parse(raw);

        Assert.Contains("A \"quote\" and a break.", post.BodyHtml);
    }

    [Fact]
    public void Parse_RepairsRawNewlinesInBody()
    {
        // A literal newline inside a string value is invalid JSON; the repair pass escapes it.
        const string raw = "{ \"h1\": \"H\", \"bodyHtml\": \"<p>line one\nline two</p>\" }";
        var post = BlogPostParser.Parse(raw);

        Assert.Contains("line one", post.BodyHtml);
        Assert.Contains("line two", post.BodyHtml);
    }

    [Fact]
    public void Parse_NoBody_ThrowsWithRawResponse()
    {
        const string raw = """{ "metaTitle": "T", "h1": "H", "cta": "Go" }""";
        var ex = Assert.Throws<FormatException>(() => BlogPostParser.Parse(raw));

        Assert.Contains("no post body", ex.Message);
        Assert.Contains(raw, ex.Message); // raw response surfaced for debugging
    }
}

public class BlogPostGeneratorTests
{
    [Fact]
    public void BuildPrompt_SubstitutesTokens()
    {
        string template = "Context: {USER_INPUT}\nPosts:\n{EXISTING_POSTS}";
        string result = BlogPostGenerator.BuildPrompt(template, "my brief", "- A — https://a");

        Assert.Contains("Context: my brief", result);
        Assert.Contains("- A — https://a", result);
        Assert.DoesNotContain("{USER_INPUT}", result);
        Assert.DoesNotContain("{EXISTING_POSTS}", result);
    }

    [Fact]
    public void BuildPrompt_EmptyExistingPosts_UsesPlaceholder()
    {
        string result = BlogPostGenerator.BuildPrompt("{EXISTING_POSTS}", "x", "");
        Assert.Contains("(none available)", result);
    }

    [Fact]
    public async Task GenerateAsync_ParsesModelReply()
    {
        var fake = new FakeLlmClient("""
            { "metaTitle": "T", "metaDescription": "D", "h1": "H", "bodyHtml": "<p>B</p>",
              "imageThemes": ["a"], "internalLinks": [], "cta": "C" }
            """);
        var gen = new BlogPostGenerator(fake, "Brief: {USER_INPUT} Posts: {EXISTING_POSTS}");

        BlogPostResult post = await gen.GenerateAsync("write about cats", "- P — https://p");

        Assert.Equal("H", post.H1);
        Assert.Single(fake.Prompts);
        Assert.Contains("write about cats", fake.Prompts[0]);
        Assert.Contains("https://p", fake.Prompts[0]);
    }

    [Fact]
    public void BuildPrompt_WithEditorFeedback_AppendsRevisionNotes()
    {
        string result = BlogPostGenerator.BuildPrompt(
            "Brief: {USER_INPUT}", "x", "", editorFeedback: "Sharpen the takeaway.");

        Assert.Contains("Editor revision notes", result);
        Assert.Contains("Sharpen the takeaway.", result);
    }

    [Fact]
    public void BuildPrompt_WithoutFeedback_HasNoRevisionNotes()
    {
        string result = BlogPostGenerator.BuildPrompt("Brief: {USER_INPUT}", "x", "");
        Assert.DoesNotContain("Editor revision notes", result);
    }

    [Fact]
    public void BuildPrompt_ListsSourceLinks()
    {
        string result = BlogPostGenerator.BuildPrompt(
            "Links:\n{SOURCE_LINKS}", "x", "", sourceLinks: new[] { "https://a.test/x", "https://b.test" });

        Assert.Contains("- https://a.test/x", result);
        Assert.Contains("- https://b.test", result);
    }

    [Fact]
    public void BuildPrompt_NoSourceLinks_RendersNone()
    {
        string result = BlogPostGenerator.BuildPrompt("Links:\n{SOURCE_LINKS}", "x", "");
        Assert.Contains("(none)", result);
        Assert.DoesNotContain("{SOURCE_LINKS}", result);
    }
}

public class BriefLinksTests
{
    [Fact]
    public void ExtractUrls_FindsBareMarkdownAndAngleBracketed_Deduped()
    {
        const string brief = """
            Write about https://github.com/x/y and see [the docs](https://docs.test/guide).
            Also <https://example.com/page>. Repeat: https://github.com/x/y again.
            """;

        var urls = BriefLinks.ExtractUrls(brief);

        Assert.Equal(
            new[] { "https://github.com/x/y", "https://docs.test/guide", "https://example.com/page" },
            urls);
    }

    [Theory]
    [InlineData("See https://a.test/page.", "https://a.test/page")]      // trailing period stripped
    [InlineData("(https://a.test/page)", "https://a.test/page")]          // wrapping paren stripped
    [InlineData("https://a.test/page,", "https://a.test/page")]           // trailing comma stripped
    public void ExtractUrls_TrimsTrailingPunctuation(string brief, string expected)
    {
        Assert.Equal(new[] { expected }, BriefLinks.ExtractUrls(brief));
    }

    [Fact]
    public void ExtractUrls_NoUrls_ReturnsEmpty()
    {
        Assert.Empty(BriefLinks.ExtractUrls("just some text, no links here"));
    }

    [Theory]
    [InlineData("https://www.github.com/x/y", "github.com/x/y")]
    [InlineData("https://example.com/", "example.com")]
    [InlineData("http://sub.test/a/b", "sub.test/a/b")]
    public void ReadableAnchor_StripsSchemeWwwAndTrailingSlash(string url, string expected)
    {
        Assert.Equal(expected, BriefLinks.ReadableAnchor(url));
    }

    [Fact]
    public void EnsureLinksPresent_AppendsMissingUnderSources()
    {
        const string body = "<p>Intro with <a href='https://a.test/x'>a link</a>.</p>";
        var urls = new[] { "https://a.test/x", "https://b.test/y" };

        string result = BriefLinks.EnsureLinksPresent(body, urls);

        Assert.Contains("<h2>Sources</h2>", result);
        Assert.Contains("<a href='https://b.test/y'>b.test/y</a>", result);
        Assert.DoesNotContain(">a.test/x<", result); // already present, not duplicated into Sources
    }

    [Fact]
    public void EnsureLinksPresent_AllPresent_ReturnsUnchanged()
    {
        const string body = "<p><a href='https://a.test/x'>x</a> and <a href='https://b.test/y'>y</a></p>";
        var urls = new[] { "https://a.test/x", "https://b.test/y" };

        string result = BriefLinks.EnsureLinksPresent(body, urls);

        Assert.Equal(body, result);
        Assert.DoesNotContain("Sources", result);
    }

    [Fact]
    public void EnsureLinksPresent_PresentWithTrailingSlash_CountsAsPresent()
    {
        const string body = "<p><a href='https://a.test/x/'>x</a></p>";
        string result = BriefLinks.EnsureLinksPresent(body, new[] { "https://a.test/x" });
        Assert.Equal(body, result); // trailing-slash variant matches
    }

    [Fact]
    public void EnsureLinksPresent_NoUrls_ReturnsUnchanged()
    {
        const string body = "<p>No links.</p>";
        Assert.Equal(body, BriefLinks.EnsureLinksPresent(body, Array.Empty<string>()));
    }
}

public class ExternalLinksTests
{
    [Theory]
    [InlineData("abc.au", "abc.au")]
    [InlineData("https://www.abc.au/blog", "abc.au")]
    [InlineData("www.Site.COM/x", "site.com")]
    [InlineData("/var/www/site", "")] // path-style folder → no domain
    [InlineData(null, "")]
    public void SiteHost_ReducesToBareHost(string? folder, string expected)
    {
        Assert.Equal(expected, ExternalLinks.SiteHost(folder));
    }

    [Theory]
    [InlineData("https://github.com/x", "abc.au", true)]   // other site
    [InlineData("https://abc.au/post", "abc.au", false)] // same domain
    [InlineData("https://blog.abc.au/p", "abc.au", false)] // subdomain
    [InlineData("https://www.abc.au/p", "abc.au", false)]  // www stripped
    [InlineData("/relative/path", "abc.au", false)]       // relative
    [InlineData("#section", "abc.au", false)]             // in-page anchor
    [InlineData("mailto:a@b.com", "abc.au", false)]       // mailto
    [InlineData("https://github.com/x", "", true)]                // unknown site → external
    public void IsExternalHref_Classifies(string href, string siteHost, bool expected)
    {
        Assert.Equal(expected, ExternalLinks.IsExternalHref(href, siteHost));
    }

    [Fact]
    public void MarkExternalLinksNewTab_AddsTargetToExternalOnly()
    {
        const string body =
            "<p><a href='https://github.com/x'>ext</a> and " +
            "<a href='https://abc.au/post'>internal</a> and " +
            "<a href='/rel'>rel</a></p>";

        string result = ExternalLinks.MarkExternalLinksNewTab(body, "abc.au");

        Assert.Contains("<a href='https://github.com/x' target='_blank' rel='noopener noreferrer'>", result);
        Assert.Contains("<a href='https://abc.au/post'>internal</a>", result); // unchanged
        Assert.Contains("<a href='/rel'>rel</a>", result);                              // unchanged
    }

    [Fact]
    public void MarkExternalLinksNewTab_RespectsExistingTarget()
    {
        const string body = "<a href='https://github.com/x' target='_self'>x</a>";
        string result = ExternalLinks.MarkExternalLinksNewTab(body, "abc.au");
        Assert.Equal(body, result); // not modified
        Assert.DoesNotContain("_blank", result);
    }

    [Fact]
    public void MarkExternalLinksNewTab_AppliesToAppendedSourcesLinks()
    {
        // End-to-end: a Sources section built by BriefLinks then marked for new-tab.
        string body = BriefLinks.EnsureLinksPresent("<p>Body.</p>", new[] { "https://github.com/x/y" });
        string result = ExternalLinks.MarkExternalLinksNewTab(body, "abc.au");

        Assert.Contains("target='_blank' rel='noopener noreferrer'", result);
    }
}

public class EditorReviewerTests
{
    private static BlogPostResult Draft() => new()
    {
        MetaTitle = "Meta T",
        MetaDescription = "Meta D",
        H1 = "Headline",
        BodyHtml = "<p>Body content.</p>",
        Cta = "Subscribe.",
    };

    [Fact]
    public void BuildPrompt_SubstitutesAllTokens()
    {
        const string template =
            "Brief: {USER_INPUT} | {META_TITLE} | {META_DESCRIPTION} | {H1} | {BODY} | {CTA}";

        string result = EditorReviewer.BuildPrompt(template, "write about cats", Draft());

        Assert.Contains("write about cats", result);
        Assert.Contains("Meta T", result);
        Assert.Contains("Meta D", result);
        Assert.Contains("Headline", result);
        Assert.Contains("<p>Body content.</p>", result);
        Assert.Contains("Subscribe.", result);
        Assert.DoesNotContain("{USER_INPUT}", result);
        Assert.DoesNotContain("{BODY}", result);
    }

    [Fact]
    public void ParseReview_CleanJson()
    {
        var review = EditorReviewer.ParseReview("""{ "score": 0.85, "feedback": "Tighten the intro." }""");

        Assert.Equal(0.85, review.Score, 3);
        Assert.Equal("Tighten the intro.", review.Feedback);
        Assert.False(review.IsUnscored);
    }

    [Fact]
    public void ParseReview_FencedAndPivotedProse()
    {
        const string raw = "Here is my review:\n```json\n{ \"score\": 0.4, \"feedback\": \"Unclear audience.\" }\n```";
        var review = EditorReviewer.ParseReview(raw);

        Assert.Equal(0.4, review.Score, 3);
        Assert.Equal("Unclear audience.", review.Feedback);
    }

    [Fact]
    public void ParseReview_ScoreAsString()
    {
        var review = EditorReviewer.ParseReview("""{ "score": "0.9", "feedback": "Good." }""");
        Assert.Equal(0.9, review.Score, 3);
    }

    [Fact]
    public void ParseReview_ClampsAboveOne()
    {
        var review = EditorReviewer.ParseReview("""{ "score": 1.5, "feedback": "x" }""");
        Assert.Equal(1.0, review.Score, 3);
    }

    [Fact]
    public void ParseReview_RepairsUnescapedQuotesInFeedback()
    {
        // Unescaped double-quotes in feedback would break strict JSON; the repair pass recovers it.
        const string raw = """{ "score": 0.5, "feedback": "Define the term "RAG" up front." }""";
        var review = EditorReviewer.ParseReview(raw);

        Assert.Equal(0.5, review.Score, 3);
        Assert.Contains("RAG", review.Feedback);
    }

    [Theory]
    [InlineData("""{ "feedback": "no score here" }""")]
    [InlineData("not json at all")]
    [InlineData("")]
    [InlineData(null)]
    public void ParseReview_Unparseable_IsUnscored(string? raw)
    {
        var review = EditorReviewer.ParseReview(raw);
        Assert.True(review.IsUnscored);
    }

    [Fact]
    public void CombineFeedback_SingleNote_ReturnedUnchanged()
    {
        Assert.Equal("Fix the intro.", EditorReviewer.CombineFeedback(new[] { "Fix the intro." }));
    }

    [Fact]
    public void CombineFeedback_Empty_ReturnsEmpty()
    {
        Assert.Equal("", EditorReviewer.CombineFeedback(Array.Empty<string>()));
        Assert.Equal("", EditorReviewer.CombineFeedback(new[] { "", "   " }));
    }

    [Fact]
    public void CombineFeedback_MultipleRounds_LabelledAndAllRetained()
    {
        string combined = EditorReviewer.CombineFeedback(new[] { "Fix the intro.", "Sharpen the takeaway." });

        Assert.Contains("Review round 1:", combined);
        Assert.Contains("Fix the intro.", combined);          // earlier note retained
        Assert.Contains("Review round 2:", combined);
        Assert.Contains("Sharpen the takeaway.", combined);   // latest note included
    }

    [Fact]
    public async Task ReviewAsync_SendsDraftAndBrief_ReturnsParsedReview()
    {
        var fake = new FakeLlmClient("""{ "score": 0.72, "feedback": "Add a clear takeaway." }""");
        var reviewer = new EditorReviewer(fake, "Brief: {USER_INPUT}\nBody: {BODY}");

        EditorReview review = await reviewer.ReviewAsync("write about cats", Draft());

        Assert.Equal(0.72, review.Score, 3);
        Assert.Equal("Add a clear takeaway.", review.Feedback);
        Assert.Single(fake.Prompts);
        Assert.Contains("write about cats", fake.Prompts[0]);
        Assert.Contains("<p>Body content.</p>", fake.Prompts[0]);
    }
}
