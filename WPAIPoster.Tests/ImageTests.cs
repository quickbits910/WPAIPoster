using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using WPAIPoster.Config;
using WPAIPoster.Images;

namespace WPAIPoster.Tests;

public class ImagePreparerTests : IDisposable
{
    private readonly string _tempDir;

    public ImagePreparerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"WPAIImg_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    /// <summary>Creates a noisy image (poorly compressible) so the size cap is actually exercised.</summary>
    private string MakeNoisyImage(int w, int h)
    {
        using var img = new Image<Rgba32>(w, h);
        var rng = new Random(1234);
        img.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                Span<Rgba32> row = accessor.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                    row[x] = new Rgba32((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256), 255);
            }
        });
        string path = Path.Combine(_tempDir, "src.png");
        img.SaveAsPng(path);
        return path;
    }

    [Fact]
    public void PrepareForUpload_ResultUnderCap()
    {
        string src = MakeNoisyImage(4000, 3000);
        string dest = Path.Combine(_tempDir, "out.jpg");

        long size = ImagePreparer.PrepareForUpload(src, dest);

        Assert.True(File.Exists(dest));
        Assert.True(size <= AppLimits.MaxImageBytes, $"size {size} exceeds cap {AppLimits.MaxImageBytes}");
    }

    [Fact]
    public void PrepareForUpload_DownscalesOversizedDimensions()
    {
        string src = MakeNoisyImage(5000, 2000);
        string dest = Path.Combine(_tempDir, "out.jpg");

        ImagePreparer.PrepareForUpload(src, dest, maxBytes: AppLimits.MaxImageBytes, maxDimension: 1000);

        using var outImg = Image.Load(dest);
        Assert.True(Math.Max(outImg.Width, outImg.Height) <= 1000);
        // aspect ratio preserved (5000x2000 = 2.5:1)
        Assert.Equal(2.5, outImg.Width / (double)outImg.Height, 1);
    }

    [Fact]
    public void MakeVisionThumbnailBase64_ProducesSmallJpeg()
    {
        string src = MakeNoisyImage(2000, 2000);

        var (b64, mime) = ImagePreparer.MakeVisionThumbnailBase64(src, maxDimension: 256);

        Assert.Equal("image/jpeg", mime);
        byte[] bytes = Convert.FromBase64String(b64);
        using var thumb = Image.Load(bytes);
        Assert.True(Math.Max(thumb.Width, thumb.Height) <= 256);
    }
}

public class ImageRelevanceSelectorTests
{
    // Hashes here are arbitrary-but-distinct; dedup is disabled (threshold -1) unless a test exercises it.
    private const int NoDedup = -1;

    private static ScoredImage Img(string path, double[] scores, ulong hash = 0) => new(path, scores, hash);

    [Theory]
    [InlineData("0.85", 0.85)]
    [InlineData("Relevance: 0.4", 0.4)]
    [InlineData("1", 1.0)]
    [InlineData("0", 0.0)]
    [InlineData("1.9", 1.0)]      // clamped
    [InlineData("nonsense", 0.0)] // no number
    [InlineData(null, 0.0)]
    [InlineData("", 0.0)]
    public void ParseScore_ExtractsAndClamps(string? reply, double expected)
    {
        Assert.Equal(expected, ImageRelevanceSelector.ParseScore(reply), 3);
    }

    [Fact]
    public void ParseScores_ParsesArrayInOrder()
    {
        Assert.Equal(new[] { 0.1, 0.9, 0.3 }, ImageRelevanceSelector.ParseScores("[0.1, 0.9, 0.3]", 3));
    }

    [Fact]
    public void ParseScores_PrefersArrayOverProseNumbers()
    {
        // Stray "1" / "2" from prose must not be consumed; only the array contents count.
        Assert.Equal(new[] { 0.4, 0.6 }, ImageRelevanceSelector.ParseScores("Themes 1 and 2: [0.4, 0.6]", 2));
    }

    [Fact]
    public void ParseScores_PadsWhenFewerNumbers()
    {
        Assert.Equal(new[] { 0.5, 0.0, 0.0 }, ImageRelevanceSelector.ParseScores("[0.5]", 3));
    }

    [Fact]
    public void ParseScores_TruncatesAndClampsExtras()
    {
        Assert.Equal(new[] { 1.0, 0.2 }, ImageRelevanceSelector.ParseScores("[1.5, 0.2, 0.9, 0.4]", 2));
    }

    [Fact]
    public void ParseScores_EmptyReply_AllZero()
    {
        Assert.Equal(new[] { 0.0, 0.0 }, ImageRelevanceSelector.ParseScores("", 2));
    }

    [Fact]
    public void Select_AssignsDistinctImagePerTheme_FeaturedIsGlobalBest()
    {
        var scored = new[]
        {
            Img("a.jpg", new[] { 0.9, 0.2, 0.1 }),
            Img("b.jpg", new[] { 0.8, 0.3, 0.2 }), // strong on theme 0 but crowds 'a'
            Img("c.jpg", new[] { 0.1, 0.9, 0.1 }),
            Img("d.jpg", new[] { 0.2, 0.1, 0.8 }),
        };

        var picks = ImageRelevanceSelector.Select(scored, themeCount: 3, count: 3, hammingThreshold: NoDedup);

        Assert.Equal(3, picks.Count);
        Assert.Equal(new[] { "a.jpg", "c.jpg", "d.jpg" }, picks.Select(p => p.Path).OrderBy(p => p));
        Assert.DoesNotContain("b.jpg", picks.Select(p => p.Path)); // duplicate-theme image excluded
        Assert.Equal("a.jpg", picks.Single(p => p.IsFeatured).Path); // 0.9, tie broken by path
    }

    [Fact]
    public void Select_SkipsPerceptualNearDuplicate()
    {
        var scored = new[]
        {
            Img("a.jpg", new[] { 0.9 }, hash: 0x0),
            Img("b.jpg", new[] { 0.85 }, hash: 0x1),                 // within Hamming 1 of 'a' → near-dup
            Img("c.jpg", new[] { 0.5 }, hash: 0xFFFFFFFFFFFFFFFF),   // visually distinct
        };

        // Single theme, two slots: 'a' covers the theme, the fill slot must skip the near-dup 'b' for 'c'.
        var picks = ImageRelevanceSelector.Select(scored, themeCount: 1, count: 2, hammingThreshold: 6);

        Assert.Equal(new[] { "a.jpg", "c.jpg" }, picks.Select(p => p.Path).OrderBy(p => p));
        Assert.DoesNotContain("b.jpg", picks.Select(p => p.Path));
    }

    [Fact]
    public void Select_DedupDisabled_KeepsHigherScoredDuplicate()
    {
        var scored = new[]
        {
            Img("a.jpg", new[] { 0.9 }, hash: 0x0),
            Img("b.jpg", new[] { 0.85 }, hash: 0x1),
            Img("c.jpg", new[] { 0.5 }, hash: 0xFFFFFFFFFFFFFFFF),
        };

        var picks = ImageRelevanceSelector.Select(scored, themeCount: 1, count: 2, hammingThreshold: NoDedup);

        Assert.Equal(new[] { "a.jpg", "b.jpg" }, picks.Select(p => p.Path).OrderBy(p => p));
    }

    [Fact]
    public void Select_DedupStarvation_StillReturnsRequestedCount()
    {
        // Every image is a near-dup of the others; dedup must relax rather than under-fill.
        var scored = new[]
        {
            Img("a.jpg", new[] { 0.9 }, hash: 0x0),
            Img("b.jpg", new[] { 0.8 }, hash: 0x1),
            Img("c.jpg", new[] { 0.7 }, hash: 0x3),
        };

        var picks = ImageRelevanceSelector.Select(scored, themeCount: 1, count: 3, hammingThreshold: 6);

        Assert.Equal(3, picks.Count);
    }

    [Fact]
    public void Select_FewerThemesThanCount_FillsByBestScore()
    {
        var scored = new[]
        {
            Img("a.jpg", new[] { 0.2 }),
            Img("b.jpg", new[] { 0.9 }),
            Img("c.jpg", new[] { 0.5 }),
            Img("d.jpg", new[] { 0.1 }),
        };

        var picks = ImageRelevanceSelector.Select(scored, themeCount: 1, count: 3, hammingThreshold: NoDedup);

        Assert.Equal(new[] { "b.jpg", "c.jpg", "a.jpg" }, picks.Select(p => p.Path));
        Assert.True(picks[0].IsFeatured);
    }

    [Fact]
    public void Select_MoreThemesThanCount_StopsAtCount()
    {
        var scored = new[]
        {
            Img("a.jpg", new[] { 0.9, 0.1, 0.1 }),
            Img("c.jpg", new[] { 0.1, 0.9, 0.1 }),
            Img("d.jpg", new[] { 0.1, 0.1, 0.95 }),
        };

        var picks = ImageRelevanceSelector.Select(scored, themeCount: 3, count: 2, hammingThreshold: NoDedup);

        Assert.Equal(2, picks.Count);
        Assert.Equal("d.jpg", picks.Single(p => p.IsFeatured).Path); // 0.95 is the global best
    }

    [Fact]
    public void Select_CountLargerThanCandidates_ReturnsAll()
    {
        var scored = new[] { Img("a.jpg", new[] { 0.5 }) };
        var picks = ImageRelevanceSelector.Select(scored, themeCount: 1, count: 3, hammingThreshold: NoDedup);
        Assert.Single(picks);
        Assert.True(picks[0].IsFeatured);
    }
}

public class PerceptualHashTests : IDisposable
{
    private readonly string _tempDir;

    public PerceptualHashTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "wpaiposter-phash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* ignore */ }
    }

    [Theory]
    [InlineData(0UL, 0UL, 0)]
    [InlineData(0b1011UL, 0b0001UL, 2)]
    [InlineData(ulong.MaxValue, 0UL, 64)]
    public void HammingDistance_CountsDifferingBits(ulong a, ulong b, int expected)
    {
        Assert.Equal(expected, PerceptualHash.HammingDistance(a, b));
    }

    [Fact]
    public void Compute_MonotonicGradients_AreDeterministic()
    {
        // Left→right brightening: every pixel is darker than its right neighbour → all bits 0.
        string brightening = SaveGradient("up.png", x => (byte)(x * 28));
        // Right→left brightening: every pixel is brighter than its right neighbour → all bits 1.
        string darkening = SaveGradient("down.png", x => (byte)((8 - x) * 28));

        ulong up = PerceptualHash.Compute(brightening);
        ulong down = PerceptualHash.Compute(darkening);

        Assert.Equal(0UL, up);
        Assert.Equal(ulong.MaxValue, down);
        Assert.Equal(64, PerceptualHash.HammingDistance(up, down));
    }

    [Fact]
    public void Compute_SameImage_HashIsStable()
    {
        string a = SaveGradient("a.png", x => (byte)(x * 17));
        string b = SaveGradient("b.png", x => (byte)(x * 17));

        Assert.Equal(0, PerceptualHash.HammingDistance(PerceptualHash.Compute(a), PerceptualHash.Compute(b)));
    }

    private string SaveGradient(string name, Func<int, byte> brightness, int w = 9, int h = 8)
    {
        using var img = new Image<L8>(w, h);
        img.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < h; y++)
            {
                Span<L8> row = accessor.GetRowSpan(y);
                for (int x = 0; x < w; x++)
                    row[x] = new L8(brightness(x));
            }
        });

        string path = Path.Combine(_tempDir, name);
        img.Save(path); // PNG (lossless) — preserves the gradient so the hash is exact
        return path;
    }
}
