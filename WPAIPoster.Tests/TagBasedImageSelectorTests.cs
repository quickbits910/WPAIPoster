using WPAIPoster.BlogPost;
using WPAIPoster.Images;

namespace WPAIPoster.Tests;

public class TagBasedImageSelectorTests
{
    private static ImageTagCatalog MountainCatalog()
    {
        var newer = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var older = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        return new ImageTagCatalog(new List<TaggedImage>
        {
            new("/a.jpg", new[] { "mountain", "peak" }, newer),
            new("/b.jpg", new[] { "trail", "hiking" }, older),
            new("/c.jpg", new[] { "laptop" }, newer),
        });
    }

    private static BlogPostResult MountainPost() => new()
    {
        H1 = "Mountain Hiking Guide",
        BodyHtml = "<p>Trails and peaks await.</p>",
        ImageThemes = new List<ImageTheme> { new("mountain", "mountain"), new("trail", "trail") },
    };

    // ---- ParseSelectedIndices (bare flat list) ----

    [Fact]
    public void ParseSelectedIndices_JsonArray()
    {
        Assert.Equal(new[] { 3, 1, 7 }, ParseIndices("[3, 1, 7]", 8));
    }

    [Fact]
    public void ParseSelectedIndices_PrefersArrayScope_IgnoresStrayNumbers()
    {
        // "5" outside the array (and out of range) must be ignored; only the array contents count.
        Assert.Equal(new[] { 2, 1 }, ParseIndices("Sure, 99 candidates. Picks: [2, 1]. Done.", 3));
    }

    [Fact]
    public void ParseSelectedIndices_ClampsAndDedupes()
    {
        Assert.Equal(new[] { 2, 3 }, ParseIndices("[2, 2, 3, 99, 0]", 3));
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("none are relevant")]
    public void ParseSelectedIndices_EmptyOrGarbage(string reply)
    {
        Assert.Empty(TagBasedImageSelector.ParseSelectedIndices(reply, 5));
    }

    private static int[] ParseIndices(string reply, int count)
        => TagBasedImageSelector.ParseSelectedIndices(reply, count).ToArray();

    // ---- ParseSelectedByTheme (per-theme object) ----

    private static int[][] ByTheme(string? reply, int themeCount, int candidateCount)
        => TagBasedImageSelector.ParseSelectedByTheme(reply, themeCount, candidateCount)
            .Select(l => l.ToArray()).ToArray();

    [Fact]
    public void ParseSelectedByTheme_MapsThemeNumbersToImageNumbers()
    {
        Assert.Equal(
            new[] { new[] { 4, 19 }, Array.Empty<int>(), new[] { 1, 15 } },
            ByTheme("{\"1\":[4,19], \"2\":[], \"3\":[1,15]}", themeCount: 3, candidateCount: 20));
    }

    [Fact]
    public void ParseSelectedByTheme_ClampsRangeAndDedupes()
    {
        Assert.Equal(
            new[] { new[] { 2, 3 }, Array.Empty<int>() },
            ByTheme("{\"1\":[2, 2, 99, 0, 3]}", themeCount: 2, candidateCount: 5));
    }

    [Fact]
    public void ParseSelectedByTheme_IgnoresOutOfRangeThemeKeys()
    {
        // Theme keys 0 and 5 are out of [1,2]; only key 1 is honoured.
        Assert.Equal(
            new[] { new[] { 3 }, Array.Empty<int>() },
            ByTheme("{\"0\":[1], \"5\":[2], \"1\":[3]}", themeCount: 2, candidateCount: 5));
    }

    [Fact]
    public void ParseSelectedByTheme_ToleratesProseAndFences()
    {
        Assert.Equal(
            new[] { new[] { 2 }, Array.Empty<int>() },
            ByTheme("Here you go:\n```json\n{\"1\":[2]}\n```", themeCount: 2, candidateCount: 5));
    }

    [Theory]
    [InlineData("")]
    [InlineData("[1, 2, 3]")]        // a bare array, not the expected object
    [InlineData("no json here")]
    public void ParseSelectedByTheme_UnparseableOrNonObject_AllEmpty(string reply)
    {
        int[][] byTheme = ByTheme(reply, themeCount: 3, candidateCount: 5);
        Assert.Equal(3, byTheme.Length);
        Assert.All(byTheme, Assert.Empty);
    }

    // ---- InterleaveRoundRobin ----

    [Fact]
    public void InterleaveRoundRobin_BalancesAcrossThemes()
    {
        var picks = TagBasedImageSelector.InterleaveRoundRobin(new[]
        {
            new[] { "a", "b" }, new[] { "c", "d" }, new[] { "e" },
        });
        Assert.Equal(new[] { "a", "c", "e", "b", "d" }, picks);
    }

    [Fact]
    public void InterleaveRoundRobin_DedupesAcrossThemes()
    {
        var picks = TagBasedImageSelector.InterleaveRoundRobin(new[]
        {
            new[] { "a", "b" }, new[] { "a", "c" },
        });
        Assert.Equal(new[] { "a", "b", "c" }, picks);
    }

    [Fact]
    public void InterleaveRoundRobin_Empty()
    {
        Assert.Empty(TagBasedImageSelector.InterleaveRoundRobin(Array.Empty<IReadOnlyList<string>>()));
        Assert.Empty(TagBasedImageSelector.InterleaveRoundRobin(new[] { Array.Empty<string>(), Array.Empty<string>() }));
    }

    // ---- BuildPrompt ----

    [Fact]
    public void BuildPrompt_ListsThemesAndNumberedCandidatesWithHints()
    {
        const string tmpl = "T:{TITLE}\nThemes:\n{THEMES}\nBody:{BODY}\nImgs:\n{TAGGED_IMAGES}";
        BlogPostResult post = MountainPost();
        var a = new TaggedImage("/a.jpg", new[] { "mountain", "peak" }, DateTime.UtcNow);
        var b = new TaggedImage("/b.jpg", new[] { "trail" }, DateTime.UtcNow);
        var union = new List<TaggedImage> { a, b };
        var perTheme = new IReadOnlyList<TaggedImage>[] { new[] { a }, new[] { b } };

        string prompt = TagBasedImageSelector.BuildPrompt(tmpl, post, post.ImageThemes, union, perTheme);

        Assert.Contains("T:Mountain Hiking Guide", prompt);
        Assert.Contains("1. mountain — mountain", prompt);       // numbered themes (subject — description)
        Assert.Contains("2. trail — trail", prompt);
        Assert.Contains("Body:Trails and peaks await.", prompt); // HTML stripped
        Assert.Contains("1. [themes 1] mountain, peak", prompt); // candidate hint = matched theme number(s)
        Assert.Contains("2. [themes 2] trail", prompt);
    }

    // ---- SelectAsync ----

    [Fact]
    public async Task SelectAsync_MapsPerThemePicksToPaths()
    {
        // Candidate union is [/a.jpg (1), /b.jpg (2)]. Model assigns theme 1 → image 2, theme 2 → image 1;
        // round-robin interleave yields [/b.jpg, /a.jpg].
        var fake = new FakeLlmClient("{\"1\":[2], \"2\":[1]}");
        var picked = await new TagBasedImageSelector(fake, "{THEMES}\n{TAGGED_IMAGES}")
            .SelectAsync(MountainCatalog(), MountainPost(), candidateLimit: 10);

        Assert.Equal(new[] { "/b.jpg", "/a.jpg" }, picked);
    }

    [Fact]
    public async Task SelectAsync_ModelUnhelpful_FallsBackToBalancedLocalRanking()
    {
        var fake = new FakeLlmClient("I cannot decide");
        var picked = await new TagBasedImageSelector(fake, "{THEMES}\n{TAGGED_IMAGES}")
            .SelectAsync(MountainCatalog(), MountainPost(), candidateLimit: 10);

        // Per-theme code ranking, round-robin: mountain→/a.jpg, trail→/b.jpg.
        Assert.Equal(new[] { "/a.jpg", "/b.jpg" }, picked);
    }

    [Fact]
    public async Task SelectAsync_UserTags_SurfaceOtherwiseExcludedImageUnderEveryTheme()
    {
        // "laptop" matches no theme, but as a (cross-cutting) author tag it surfaces /c.jpg under both
        // themes, so it survives the per-theme shortlist instead of being dropped entirely.
        var fake = new FakeLlmClient("I cannot decide"); // fall back to local ranking
        var picked = await new TagBasedImageSelector(fake, "{THEMES}\n{TAGGED_IMAGES}")
            .SelectAsync(MountainCatalog(), MountainPost(), candidateLimit: 10, userTags: new[] { "laptop" });

        // mountain→[/a.jpg, /c.jpg], trail→[/b.jpg, /c.jpg]; interleave+dedupe → a, b, c.
        Assert.Equal(new[] { "/a.jpg", "/b.jpg", "/c.jpg" }, picked);
    }

    [Fact]
    public async Task SelectAsync_BalancesThemesEvenWhenOneThemeIsTagDense()
    {
        // The collapse scenario in miniature: many "code" images, one "server" image. The shortlist head
        // must include the server image rather than being all code.
        var t = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var catalog = new ImageTagCatalog(new List<TaggedImage>
        {
            new("/code1.jpg", new[] { "code", "software" }, t),
            new("/code2.jpg", new[] { "code", "developer" }, t),
            new("/code3.jpg", new[] { "coding", "programming" }, t),
            new("/server.jpg", new[] { "server", "datacenter" }, t),
        });
        var post = new BlogPostResult
        {
            H1 = "Scaling Up",
            BodyHtml = "<p>Code and servers.</p>",
            ImageThemes = new List<ImageTheme> { new("server", "server hardware"), new("code", "source code") },
        };

        var fake = new FakeLlmClient("I cannot decide"); // exercise the deterministic balanced fallback
        var picked = await new TagBasedImageSelector(fake, "{THEMES}\n{TAGGED_IMAGES}")
            .SelectAsync(catalog, post, candidateLimit: 10);

        Assert.Contains("/server.jpg", picked);
        Assert.Equal("/server.jpg", picked[0]); // server theme leads the round-robin
    }

    [Fact]
    public async Task SelectAsync_NoTagMatches_ReturnsEmpty_WithoutCallingModel()
    {
        var fake = new FakeLlmClient("{\"1\":[1]}");
        var unrelated = new BlogPostResult
        {
            H1 = "Italian Pasta Recipes",
            BodyHtml = "<p>Cooking spaghetti and sauce.</p>",
            ImageThemes = new List<ImageTheme> { new("pasta", "pasta"), new("kitchen", "kitchen") },
        };

        var picked = await new TagBasedImageSelector(fake, "{THEMES}\n{TAGGED_IMAGES}")
            .SelectAsync(MountainCatalog(), unrelated, candidateLimit: 10);

        Assert.Empty(picked);
        Assert.Empty(fake.Prompts); // model not queried when nothing matches
    }
}
