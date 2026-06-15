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
        ImageThemes = new List<string> { "mountain", "trail" },
    };

    // ---- ParseSelectedIndices ----

    [Fact]
    public void ParseSelectedIndices_JsonArray()
    {
        Assert.Equal(new[] { 3, 1, 7 }, BlogPostParserIndices("[3, 1, 7]", 8));
    }

    [Fact]
    public void ParseSelectedIndices_PrefersArrayScope_IgnoresStrayNumbers()
    {
        // "5" outside the array (and out of range) must be ignored; only the array contents count.
        Assert.Equal(new[] { 2, 1 }, BlogPostParserIndices("Sure, 99 candidates. Picks: [2, 1]. Done.", 3));
    }

    [Fact]
    public void ParseSelectedIndices_ClampsAndDedupes()
    {
        Assert.Equal(new[] { 2, 3 }, BlogPostParserIndices("[2, 2, 3, 99, 0]", 3));
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("none are relevant")]
    public void ParseSelectedIndices_EmptyOrGarbage(string reply)
    {
        Assert.Empty(TagBasedImageSelector.ParseSelectedIndices(reply, 5));
    }

    private static int[] BlogPostParserIndices(string reply, int count)
        => TagBasedImageSelector.ParseSelectedIndices(reply, count).ToArray();

    // ---- BuildPrompt ----

    [Fact]
    public void BuildPrompt_SubstitutesAndNumbersCandidates()
    {
        const string tmpl = "T:{TITLE} Themes:{IMAGE_THEMES} Body:{BODY}\n{TAGGED_IMAGES}";
        var candidates = new List<TaggedImage>
        {
            new("/a.jpg", new[] { "mountain", "peak" }, DateTime.UtcNow),
            new("/b.jpg", new[] { "trail" }, DateTime.UtcNow),
        };

        string prompt = TagBasedImageSelector.BuildPrompt(tmpl, MountainPost(), candidates);

        Assert.Contains("T:Mountain Hiking Guide", prompt);
        Assert.Contains("Themes:mountain, trail", prompt);
        Assert.Contains("Body:Trails and peaks await.", prompt); // HTML stripped
        Assert.Contains("1. mountain, peak", prompt);
        Assert.Contains("2. trail", prompt);
    }

    // ---- SelectAsync ----

    [Fact]
    public async Task SelectAsync_MapsModelPicksToPaths()
    {
        var fake = new FakeLlmClient("[2, 1]");
        var picked = await new TagBasedImageSelector(fake, "{TAGGED_IMAGES}")
            .SelectAsync(MountainCatalog(), MountainPost(), candidateLimit: 10);

        // Ranked candidates are [/a.jpg, /b.jpg] (laptop excluded); model picked 2 then 1.
        Assert.Equal(new[] { "/b.jpg", "/a.jpg" }, picked);
    }

    [Fact]
    public async Task SelectAsync_ModelUnhelpful_FallsBackToLocalRanking()
    {
        var fake = new FakeLlmClient("I cannot decide");
        var picked = await new TagBasedImageSelector(fake, "{TAGGED_IMAGES}")
            .SelectAsync(MountainCatalog(), MountainPost(), candidateLimit: 10);

        Assert.Equal(new[] { "/a.jpg", "/b.jpg" }, picked); // local rank order
    }

    [Fact]
    public async Task SelectAsync_NoTagMatches_ReturnsEmpty_WithoutCallingModel()
    {
        var fake = new FakeLlmClient("[1]");
        var unrelated = new BlogPostResult
        {
            H1 = "Italian Pasta Recipes",
            BodyHtml = "<p>Cooking spaghetti and sauce.</p>",
            ImageThemes = new List<string> { "pasta", "kitchen" },
        };

        var picked = await new TagBasedImageSelector(fake, "{TAGGED_IMAGES}")
            .SelectAsync(MountainCatalog(), unrelated, candidateLimit: 10);

        Assert.Empty(picked);
        Assert.Empty(fake.Prompts); // model not queried when nothing matches
    }
}
