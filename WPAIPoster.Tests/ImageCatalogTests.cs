using WPAIPoster.Images;

namespace WPAIPoster.Tests;

public class CandidateSetTests
{
    [Fact]
    public void Build_PrimaryFirst_ThenFill_Deduped_Capped()
    {
        var result = CandidateSet.Build(
            primary: new[] { "a", "b" },
            fill: new[] { "b", "c", "d" },
            max: 3);

        Assert.Equal(new[] { "a", "b", "c" }, result);
    }

    [Fact]
    public void Build_EmptyPrimary_UsesFill()
    {
        var result = CandidateSet.Build(Array.Empty<string>(), new[] { "x", "y" }, 5);
        Assert.Equal(new[] { "x", "y" }, result);
    }

    [Fact]
    public void Build_MaxZero_ReturnsEmpty()
    {
        Assert.Empty(CandidateSet.Build(new[] { "a" }, new[] { "b" }, 0));
    }
}

public class StripPrefixTests
{
    [Fact]
    public void StripPrefix_RemovesPrefixCaseInsensitive_AndDedupes()
    {
        var result = ImageLibraryScanner.StripPrefix(
            new[] { "AI.Mountain", "ai.lake", "Plain", "AI.Mountain" }, "AI.");

        Assert.Equal(new[] { "Mountain", "lake", "Plain" }, result);
    }

    [Fact]
    public void StripPrefix_NullPrefix_KeepsTags()
    {
        var result = ImageLibraryScanner.StripPrefix(new[] { "AI.Mountain" }, null);
        Assert.Equal(new[] { "AI.Mountain" }, result);
    }
}

public class ScanWithTagsTests : IDisposable
{
    private readonly string _dir;

    public ScanWithTagsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"WPAIScan_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private string Touch(string name, DateTime mtimeUtc)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, "x");
        File.SetLastWriteTimeUtc(path, mtimeUtc);
        return path;
    }

    [Fact]
    public void ScanWithTags_BuildsCatalog_NewestFirst_PrefixStripped_WithIndex()
    {
        string a = Touch("a.jpg", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        string b = Touch("b.png", new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Touch("ignore.txt", DateTime.UtcNow); // non-image, excluded

        var reader = new FakeImageTagReader(new Dictionary<string, string[]>
        {
            ["a.jpg"] = new[] { "AI.Mountain", "AI.Lake" },
            ["b.png"] = new[] { "AI.Laptop" },
        });

        ImageTagCatalog catalog = ImageLibraryScanner.ScanWithTags(_dir, 100, reader, "AI.");

        Assert.Equal(2, catalog.Images.Count);              // .txt excluded
        Assert.Equal(a, catalog.Images[0].Path);            // newest first
        Assert.Equal(b, catalog.Images[1].Path);
        Assert.Equal(new[] { "Mountain", "Lake" }, catalog.Images[0].Tags); // prefix stripped
        Assert.Equal(2, catalog.TaggedCount);

        // Fast lookup dictionary is lower-cased and points at the right paths.
        Assert.True(catalog.TagIndex.ContainsKey("mountain"));
        Assert.Equal(new[] { a }, catalog.TagIndex["mountain"]);
    }

    [Fact]
    public void ScanWithTags_RespectsMaxIndex()
    {
        Touch("a.jpg", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Touch("b.jpg", new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Touch("c.jpg", new DateTime(2022, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var reader = new FakeImageTagReader(new Dictionary<string, string[]>());
        ImageTagCatalog catalog = ImageLibraryScanner.ScanWithTags(_dir, 2, reader, "AI.");

        Assert.Equal(2, catalog.Images.Count); // capped, newest two
    }
}
