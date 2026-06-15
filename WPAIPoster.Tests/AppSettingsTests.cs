using WPAIPoster.Config;

namespace WPAIPoster.Tests;

public class AppSettingsTests : IDisposable
{
    private readonly string _tempDir;

    public AppSettingsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"WPAISettings_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private string Write(string json)
    {
        string path = Path.Combine(_tempDir, "app.settings.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void Load_AllFields_Deserializes()
    {
        string path = Write("""
            {
              "provider": "anthropic",
              "model": "claude-opus-4-8",
              "visionModel": "claude-opus-4-8",
              "baseUrl": "https://example.com",
              "apiKey": "sk-test",
              "imageLibrary": "/images",
              "autoPublish": true,
              "wordPressFolder": "site.au",
              "maxImagesToScore": 12,
              "imagesPerPost": 2,
              "seoMetaKeys": { "title": "_yt", "description": "_yd" }
            }
            """);

        var s = AppSettings.Load(path);

        Assert.Equal("anthropic", s.Provider);
        Assert.Equal("claude-opus-4-8", s.Model);
        Assert.Equal("claude-opus-4-8", s.VisionModel);
        Assert.Equal("https://example.com", s.BaseUrl);
        Assert.Equal("sk-test", s.ApiKey);
        Assert.Equal("/images", s.ImageLibrary);
        Assert.True(s.AutoPublish);
        Assert.Equal("site.au", s.WordPressFolder);
        Assert.Equal(12, s.MaxImagesToScore);
        Assert.Equal(2, s.ImagesPerPost);
        Assert.Equal("_yt", s.SeoMetaKeys!.Title);
        Assert.Equal("_yd", s.SeoMetaKeys!.Description);
        Assert.Equal(path, s.LoadedFrom);
    }

    [Fact]
    public void Load_MissingFile_ReturnsAllNull()
    {
        var s = AppSettings.Load(Path.Combine(_tempDir, "nope.json"));

        Assert.Null(s.Provider);
        Assert.Null(s.AutoPublish);
        Assert.Null(s.LoadedFrom);
    }

    [Fact]
    public void Load_CommentsAndTrailingCommas_Allowed()
    {
        string path = Write("""
            {
              // local provider
              "provider": "lmstudio",
              "model": "gemma",
            }
            """);

        var s = AppSettings.Load(path);

        Assert.Equal("lmstudio", s.Provider);
        Assert.Equal("gemma", s.Model);
    }
}
