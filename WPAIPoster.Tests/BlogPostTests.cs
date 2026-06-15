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

        Assert.Equal(new[] { "a", "b" }, post.ImageThemes);
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
}
