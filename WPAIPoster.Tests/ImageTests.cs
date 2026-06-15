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
    public void Select_OrdersByScoreAndMarksFeatured()
    {
        var scored = new (string, double)[]
        {
            ("a.jpg", 0.2),
            ("b.jpg", 0.9),
            ("c.jpg", 0.5),
            ("d.jpg", 0.1),
        };

        var picks = ImageRelevanceSelector.Select(scored, 3);

        Assert.Equal(3, picks.Count);
        Assert.Equal("b.jpg", picks[0].Path);
        Assert.True(picks[0].IsFeatured);
        Assert.False(picks[1].IsFeatured);
        Assert.Equal(new[] { "b.jpg", "c.jpg", "a.jpg" }, picks.Select(p => p.Path));
    }

    [Fact]
    public void Select_CountLargerThanCandidates_ReturnsAll()
    {
        var scored = new (string, double)[] { ("a.jpg", 0.5) };
        var picks = ImageRelevanceSelector.Select(scored, 3);
        Assert.Single(picks);
        Assert.True(picks[0].IsFeatured);
    }
}
