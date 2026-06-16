using WPAIPoster.BlogPost;
using WPAIPoster.Config;
using WPAIPoster.Images;
using WPAIPoster.Wordpress;

namespace WPAIPoster.Tests;

public class WpCliCommandsTests
{
    [Fact]
    public void ShellQuote_EscapesSingleQuotes()
    {
        Assert.Equal(@"'it'\''s here'", WpCliCommands.ShellQuote("it's here"));
        Assert.Equal("'plain'", WpCliCommands.ShellQuote("plain"));
        Assert.Equal("''", WpCliCommands.ShellQuote(null));
    }

    [Fact]
    public void InFolder_PrependsCd()
    {
        string cmd = WpCliCommands.InFolder("my site", "wp post list");
        Assert.Equal("cd 'my site' && wp post list", cmd);
    }

    [Fact]
    public void CreatePost_Draft_UsesDraftStatusAndPorcelain()
    {
        string cmd = WpCliCommands.CreatePost("/tmp/p.html", "My Title", publish: false, excerpt: "desc");

        Assert.Contains("wp post create '/tmp/p.html'", cmd);
        Assert.Contains("--post_title='My Title'", cmd);
        Assert.Contains("--post_status=draft", cmd);
        Assert.Contains("--post_excerpt='desc'", cmd);
        Assert.Contains("--porcelain", cmd);
        Assert.DoesNotContain("publish", cmd);
    }

    [Fact]
    public void CreatePost_Publish_UsesPublishStatus()
    {
        string cmd = WpCliCommands.CreatePost("/tmp/p.html", "T", publish: true, excerpt: "d");
        Assert.Contains("--post_status=publish", cmd);
    }

    [Fact]
    public void CreatePost_QuotesTitleWithQuotes()
    {
        string cmd = WpCliCommands.CreatePost("/tmp/p.html", "It's a \"Test\"", publish: false, excerpt: "");
        Assert.Contains(@"--post_title='It'\''s a ""Test""'", cmd);
    }

    [Fact]
    public void ImportMedia_FeaturedAddsFlag()
    {
        string featured = WpCliCommands.ImportMedia("/tmp/i.jpg", 42, "t", "alt", featured: true);
        string normal = WpCliCommands.ImportMedia("/tmp/i.jpg", 42, "t", "alt", featured: false);

        Assert.Contains("--post_id=42", featured);
        Assert.Contains("--featured_image", featured);
        Assert.Contains("--porcelain", featured);
        Assert.DoesNotContain("--featured_image", normal);
    }

    [Fact]
    public void UpdateMeta_AndContent_AndRemove()
    {
        Assert.Equal("wp post meta update 7 '_yoast_wpseo_title' 'Hi'",
            WpCliCommands.UpdateMeta(7, "_yoast_wpseo_title", "Hi"));
        Assert.Equal("wp post update 7 '/tmp/x.html'",
            WpCliCommands.UpdatePostContent(7, "/tmp/x.html"));
        Assert.Equal("rm -f '/tmp/x.html'", WpCliCommands.RemoveRemoteFile("/tmp/x.html"));
    }

    [Fact]
    public void CreateTerm_QuotesName_WithPorcelain()
    {
        Assert.Equal("wp term create category 'Blog' --porcelain", WpCliCommands.CreateTerm("category", "Blog"));
        Assert.Equal("wp term create post_tag 'concept cars' --porcelain",
            WpCliCommands.CreateTerm("post_tag", "concept cars"));
    }

    [Fact]
    public void GetTermId_FiltersByName()
    {
        Assert.Equal("wp term list post_tag --name='aero' --field=term_id",
            WpCliCommands.GetTermId("post_tag", "aero"));
    }

    [Fact]
    public void SetPostTerms_UsesIds_ById()
    {
        string cmd = WpCliCommands.SetPostTerms(7, "post_tag", new[] { 12, 34 });
        Assert.Equal("wp post term set 7 post_tag 12 34 --by=id", cmd);
    }
}

public class HtmlImageEmbedderTests
{
    private const string ThreeH2Body =
        "<p>intro</p><h2>A</h2><p>a</p><h2>B</h2><p>b</p><h2>C</h2><p>c</p>";

    private static int Pos(string haystack, string needle) =>
        haystack.IndexOf(needle, StringComparison.Ordinal);

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { count++; i += needle.Length; }
        return count;
    }

    [Fact]
    public void Embed_NoImages_ReturnsUnchanged()
    {
        Assert.Equal("<p>a</p>", HtmlImageEmbedder.Embed("<p>a</p>", Array.Empty<EmbeddedImage>()));
    }

    [Fact]
    public void Embed_FirstImage_GoesUnderH1AtTop()
    {
        string result = HtmlImageEmbedder.Embed(ThreeH2Body, new[] { new EmbeddedImage("u1", "alt") });

        Assert.Equal(1, CountOccurrences(result, "<figure>"));
        Assert.True(Pos(result, "src=\"u1\"") < Pos(result, "<p>intro</p>")); // above the intro
    }

    [Fact]
    public void Embed_FourImages_TopThen2ndAnd3rdH2ThenBottom()
    {
        var imgs = new[]
        {
            new EmbeddedImage("u1", "a1"),
            new EmbeddedImage("u2", "a2"),
            new EmbeddedImage("u3", "a3"),
            new EmbeddedImage("u4", "a4"),
        };

        string result = HtmlImageEmbedder.Embed(ThreeH2Body, imgs);

        Assert.Equal(4, CountOccurrences(result, "<figure>"));

        // u1 at top (under H1)
        Assert.True(Pos(result, "src=\"u1\"") < Pos(result, "<p>intro</p>"));
        // u2 under the 2nd H2 (B): after </h2> of B, before the 3rd H2 (C)
        Assert.True(Pos(result, "<h2>B</h2>") < Pos(result, "src=\"u2\""));
        Assert.True(Pos(result, "src=\"u2\"") < Pos(result, "<h2>C</h2>"));
        // u3 under the 3rd H2 (C)
        Assert.True(Pos(result, "<h2>C</h2>") < Pos(result, "src=\"u3\""));
        // u4 at the very bottom
        Assert.True(Pos(result, "src=\"u4\"") > Pos(result, "<p>c</p>"));
        // 1st H2 (A) gets no image
        Assert.True(Pos(result, "src=\"u3\"") > Pos(result, "<h2>A</h2>"));
    }

    [Fact]
    public void Embed_FewerH2sThanImages_ExtrasGoToBottom()
    {
        string body = "<p>intro</p><h2>Only</h2><p>x</p>";
        var imgs = new[]
        {
            new EmbeddedImage("u1", "a1"),
            new EmbeddedImage("u2", "a2"),
            new EmbeddedImage("u3", "a3"),
        };

        string result = HtmlImageEmbedder.Embed(body, imgs);

        Assert.Equal(3, CountOccurrences(result, "<figure>"));
        Assert.True(Pos(result, "src=\"u1\"") < Pos(result, "<p>intro</p>")); // top
        Assert.True(Pos(result, "src=\"u2\"") > Pos(result, "<p>x</p>"));     // bottom
        Assert.True(Pos(result, "src=\"u3\"") > Pos(result, "src=\"u2\""));   // after u2 (bottom, in order)
    }

    [Fact]
    public void Embed_EscapesAlt()
    {
        string result = HtmlImageEmbedder.Embed("<p>x</p>",
            new[] { new EmbeddedImage("u", "a & \"b\"") });
        Assert.Contains("alt=\"a &amp; &quot;b&quot;\"", result);
    }
}

public class ExistingPostsFetcherTests
{
    private const string Json = """
        [
          { "ID": 12, "post_title": "Hello World", "url": "https://s.test/hello" },
          { "ID": 13, "post_title": "Caching", "url": "https://s.test/caching" }
        ]
        """;

    [Fact]
    public void Parse_ReadsNumericIds()
    {
        var posts = ExistingPostsFetcher.Parse(Json);

        Assert.Equal(2, posts.Count);
        Assert.Equal("12", posts[0].Id);
        Assert.Equal("Hello World", posts[0].Title);
        Assert.Equal("https://s.test/caching", posts[1].Url);
    }

    [Fact]
    public void Parse_InvalidJson_ReturnsEmpty()
    {
        Assert.Empty(ExistingPostsFetcher.Parse("not json"));
        Assert.Empty(ExistingPostsFetcher.Parse("{}"));
    }

    [Fact]
    public void FormatForPrompt_RendersLines()
    {
        var posts = ExistingPostsFetcher.Parse(Json);
        string text = ExistingPostsFetcher.FormatForPrompt(posts);

        Assert.Contains("- Hello World — https://s.test/hello", text);
        Assert.Contains("- Caching — https://s.test/caching", text);
    }

    [Fact]
    public void Fetch_RunsListInFolder_AndParses()
    {
        var runner = new FakeSshRunner(cmd =>
            cmd.Contains("wp post list")
                ? new SshCommandResult(0, Json, "")
                : new SshCommandResult(0, "", ""));

        var fetcher = new ExistingPostsFetcher(runner, "site.au");
        var posts = fetcher.Fetch();

        Assert.Equal(2, posts.Count);
        Assert.Contains(runner.Commands, c => c.StartsWith("cd 'site.au' && wp post list"));
    }

    [Fact]
    public void Fetch_NonZeroExit_ReturnsEmpty()
    {
        var runner = new FakeSshRunner(_ => new SshCommandResult(1, "", "boom"));
        var posts = new ExistingPostsFetcher(runner, "site.au").Fetch();
        Assert.Empty(posts);
    }
}

public class WpCliPublisherTests
{
    private static BlogPostResult SamplePost() => new()
    {
        MetaTitle = "Meta T",
        MetaDescription = "Meta D",
        H1 = "The Title",
        BodyHtml = "<p>Intro.</p><p>Body.</p>",
        ImageThemes = new List<string> { "theme" },
        Cta = "Go.",
    };

    /// <summary>Responder that hands out sequential IDs for create/import and a guid/siteurl for reads.</summary>
    private static Func<string, SshCommandResult> Responder()
    {
        int nextId = 100;
        return cmd =>
        {
            if (cmd.Contains("wp post create")) return new SshCommandResult(0, (++nextId).ToString(), "");
            if (cmd.Contains("wp media import")) return new SshCommandResult(0, (++nextId).ToString(), "");
            if (cmd.Contains("wp term create")) return new SshCommandResult(0, (++nextId).ToString(), "");
            if (cmd.Contains("--field=guid")) return new SshCommandResult(0, "https://s.test/wp-content/img.jpg", "");
            if (cmd.Contains("wp option get siteurl")) return new SshCommandResult(0, "https://s.test", "");
            return new SshCommandResult(0, "", "");
        };
    }

    [Fact]
    public void Publish_Draft_CreatesThenMetaThenMediaInOrder()
    {
        var runner = new FakeSshRunner(Responder());
        var images = new List<SelectedImage>
        {
            new("/tmp/feat.jpg", 0.9, IsFeatured: true),
            new("/tmp/extra.jpg", 0.5, IsFeatured: false),
        };
        var publisher = new WpCliPublisher(runner, "site.au",
            new SeoMetaKeys { Title = "_yoast_wpseo_title", Description = "_yoast_wpseo_metadesc" });

        PublishOutcome outcome = publisher.Publish(SamplePost(), images, publish: false);

        Assert.Equal(101, outcome.PostId);
        Assert.False(outcome.Published);
        Assert.Equal("https://s.test/wp-admin/post.php?post=101&action=edit", outcome.AdminEditUrl);

        // Command ordering
        int idxCreate = runner.Commands.FindIndex(c => c.Contains("wp post create"));
        int idxMeta = runner.Commands.FindIndex(c => c.Contains("wp post meta update"));
        int idxMedia = runner.Commands.FindIndex(c => c.Contains("wp media import"));
        Assert.True(idxCreate >= 0 && idxMeta > idxCreate && idxMedia > idxMeta);

        // Two media imports, exactly one featured
        Assert.Equal(2, runner.Commands.Count(c => c.Contains("wp media import")));
        Assert.Equal(1, runner.Commands.Count(c => c.Contains("--featured_image")));

        // Draft status used
        Assert.Contains(runner.Commands, c => c.Contains("--post_status=draft"));

        // Body + final body + 2 images uploaded
        Assert.Equal(4, runner.Uploads.Count);

        // Inline (non-featured) image triggered a content update
        Assert.Contains(runner.Commands, c => c.Contains("wp post update"));

        // Temp files cleaned up (one rm per upload; temp paths are absolute so no cd needed)
        Assert.Equal(runner.Uploads.Count, runner.Commands.Count(c => c.StartsWith("rm -f ")));
    }

    [Fact]
    public void Publish_NoSeoKeys_SkipsMeta()
    {
        var runner = new FakeSshRunner(Responder());
        var publisher = new WpCliPublisher(runner, "site.au", seoMetaKeys: null);

        publisher.Publish(SamplePost(), Array.Empty<SelectedImage>(), publish: true);

        Assert.DoesNotContain(runner.Commands, c => c.Contains("wp post meta update"));
        Assert.Contains(runner.Commands, c => c.Contains("--post_status=publish"));
    }

    [Fact]
    public void Publish_CreateFailure_Throws()
    {
        var runner = new FakeSshRunner(cmd =>
            cmd.Contains("wp post create")
                ? new SshCommandResult(1, "", "permission denied")
                : new SshCommandResult(0, "1", ""));
        var publisher = new WpCliPublisher(runner, "site.au", null);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            publisher.Publish(SamplePost(), Array.Empty<SelectedImage>(), publish: false));
        Assert.Contains("create post", ex.Message);
    }

    [Fact]
    public void Publish_AppliesTagsCappedAt5_AndModelCategories()
    {
        var runner = new FakeSshRunner(Responder());
        BlogPostResult post = SamplePost();
        post.Tags = new List<string> { "a", "b", "c", "d", "e", "f", "g" };
        post.Categories = new List<string> { "Tech", "Cars" };
        var publisher = new WpCliPublisher(runner, "site.au", null);

        publisher.Publish(post, Array.Empty<SelectedImage>(), publish: false);

        // Exactly the first 5 tags are created (capped); 'f'/'g' never touched.
        Assert.Equal(5, runner.Commands.Count(c => c.Contains("wp term create post_tag")));
        Assert.DoesNotContain(runner.Commands, c => c.Contains("wp term create post_tag 'f'"));

        string setTags = runner.Commands.Single(c => c.Contains("wp post term set") && c.Contains("post_tag"));
        Assert.EndsWith("--by=id", setTags);

        Assert.Contains(runner.Commands, c => c.Contains("wp term create category 'Tech'"));
        Assert.Contains(runner.Commands, c => c.Contains("wp term create category 'Cars'"));
        Assert.Contains(runner.Commands, c => c.Contains("wp post term set") && c.Contains(" category ") && c.EndsWith("--by=id"));
    }

    [Fact]
    public void Publish_NoCategories_AppliesDefault()
    {
        var runner = new FakeSshRunner(Responder());
        var publisher = new WpCliPublisher(runner, "site.au", null, defaultCategory: "Blog");

        publisher.Publish(SamplePost(), Array.Empty<SelectedImage>(), publish: false); // SamplePost has no categories

        Assert.Contains(runner.Commands, c => c.Contains("wp term create category 'Blog' --porcelain"));
        string setCats = runner.Commands.Single(c => c.Contains("wp post term set") && c.Contains(" category "));
        Assert.EndsWith("--by=id", setCats);
    }

    [Fact]
    public void Publish_NoTags_SkipsTagAssignment()
    {
        var runner = new FakeSshRunner(Responder());
        var publisher = new WpCliPublisher(runner, "site.au", null);

        publisher.Publish(SamplePost(), Array.Empty<SelectedImage>(), publish: false); // SamplePost has no tags

        Assert.DoesNotContain(runner.Commands, c => c.Contains("wp post term set") && c.Contains("post_tag"));
    }
}
