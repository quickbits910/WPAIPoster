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
