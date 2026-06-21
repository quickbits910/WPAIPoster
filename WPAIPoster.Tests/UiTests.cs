using WPAIPoster.Llm;
using WPAIPoster.Ui;

namespace WPAIPoster.Tests;

public class RunLoggerTests : IDisposable
{
    private readonly string _dir;

    public RunLoggerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wpai-log-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* ignore */ }
    }

    [Fact]
    public void BuildLogFileName_HasTimestampAndSanitizedToken()
    {
        string name = RunLogger.BuildLogFileName(new DateTime(2026, 6, 20, 14, 12, 33), "a1b2-c3/d4??extra");

        Assert.StartsWith("run-20260620-141233-", name);
        Assert.EndsWith(".log", name);
        Assert.DoesNotContain("/", name);
        Assert.DoesNotContain("?", name);
    }

    [Fact]
    public void BuildLogFileName_EmptyToken_FallsBack()
    {
        string name = RunLogger.BuildLogFileName(new DateTime(2026, 1, 1, 0, 0, 0), "!!!");
        Assert.Equal("run-20260101-000000-log.log", name);
    }

    [Fact]
    public void Write_ProducesTimestampedVerbatimLine()
    {
        string path;
        using (var logger = new RunLogger(_dir, new DateTime(2026, 6, 20, 9, 0, 0), "tok"))
        {
            path = logger.LogPath;
            logger.Write("DEBUG", "\"imageThemes\": [{ \"subject\": \"network\" }], \"tags\": [\"a\", \"b\"]");
        }

        string text = File.ReadAllText(path);
        // Content with brackets (JSON arrays) must be preserved verbatim — not stripped as if it were markup.
        Assert.Contains("DEBUG] \"imageThemes\": [{ \"subject\": \"network\" }], \"tags\": [\"a\", \"b\"]", text);
        Assert.Contains("Run started", text); // constructor line
    }
}

public class LoggingLlmClientTests : IDisposable
{
    private readonly string _dir;

    public LoggingLlmClientTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wpai-llmlog-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* ignore */ }
    }

    [Fact]
    public async Task SendAsync_ReturnsInnerReply_AndLogsPromptAndReply()
    {
        var inner = new FakeLlmClient("THE-REPLY");
        string path;
        string? reply;

        using (var logger = new RunLogger(_dir, DateTime.Now, "tok"))
        {
            path = logger.LogPath;
            var client = new LoggingLlmClient(inner, logger, "text");
            reply = await client.SendAsync("THE-PROMPT", null, null);
        }

        Assert.Equal("THE-REPLY", reply);
        Assert.Equal("THE-PROMPT", inner.Prompts.Single());

        string log = File.ReadAllText(path);
        Assert.Contains("THE-PROMPT", log);
        Assert.Contains("THE-REPLY", log);
    }
}
