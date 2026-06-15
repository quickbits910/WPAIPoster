using WPAIPoster.Prompts;

namespace WPAIPoster.Tests;

public class PromptLoaderTests : IDisposable
{
    private readonly string _tempDir;

    public PromptLoaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"WPAIPrompts_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void Load_FromDir_ParsesPromptLines()
    {
        string path = Path.Combine(_tempDir, "p.json");
        File.WriteAllText(path, """{ "prompt": ["line one", "line two"] }""");

        PromptConfig cfg = PromptLoader.Load("p.json", _tempDir);

        Assert.Equal(2, cfg.Prompt.Count);
        Assert.Equal("line one\nline two", cfg.GetPromptText());
    }

    [Fact]
    public void Load_FromDir_MissingFile_Throws()
    {
        Assert.Throws<FileNotFoundException>(() => PromptLoader.Load("absent.json", _tempDir));
    }

    [Theory]
    [InlineData("../secret.json")]
    [InlineData("sub/file.json")]
    [InlineData("/etc/passwd")]
    public void Load_UnsafeFileName_Throws(string fileName)
    {
        Assert.Throws<InvalidOperationException>(() => PromptLoader.Load(fileName, _tempDir));
    }

    [Fact]
    public void PromptFileNames_AreDefined()
    {
        Assert.Equal("blog-post-prompt.json", PromptLoader.BlogPostPromptFile);
        Assert.Equal("image-relevance-prompt.json", PromptLoader.ImageRelevancePromptFile);
        Assert.Equal("tag-to-blog-post-body-prompt.json", PromptLoader.TagToBodyPromptFile);
    }

    [Fact]
    public void Load_MalformedJson_ThrowsClearNamedError()
    {
        string path = Path.Combine(_tempDir, "p.json");
        File.WriteAllText(path, """{ "prompt": ["ok" "broken"] }"""); // missing comma

        var ex = Assert.Throws<InvalidOperationException>(() => PromptLoader.Load("p.json", _tempDir));
        Assert.Contains("p.json", ex.Message);
        Assert.Contains("not valid JSON", ex.Message);
    }

    [Fact]
    public void Load_AllowsCommentsAndTrailingCommas()
    {
        string path = Path.Combine(_tempDir, "p.json");
        File.WriteAllText(path, """
            {
              // a comment
              "prompt": ["line one", "line two",]
            }
            """);

        var cfg = PromptLoader.Load("p.json", _tempDir);
        Assert.Equal(2, cfg.Prompt.Count);
    }
}
