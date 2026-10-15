using WPAIPoster.Images;
using WPAIPoster.Llm;
using WPAIPoster.Mcp;
using WPAIPoster.Wordpress;

namespace WPAIPoster.Tests;

/// <summary>An <see cref="IImageTagReader"/> that returns canned tags per path (by full path or file name).</summary>
public sealed class FakeImageTagReader : IImageTagReader
{
    private readonly Dictionary<string, string[]> _tags;

    public FakeImageTagReader(Dictionary<string, string[]> tagsByPathOrName) => _tags = tagsByPathOrName;

    public IReadOnlyList<string> ReadTags(string path)
    {
        if (_tags.TryGetValue(path, out string[]? byPath)) return byPath;
        if (_tags.TryGetValue(Path.GetFileName(path), out string[]? byName)) return byName;
        return Array.Empty<string>();
    }
}

/// <summary>An <see cref="ILlmClient"/> that returns queued replies and records the prompts it saw.</summary>
public sealed class FakeLlmClient : ILlmClient
{
    private readonly Queue<string?> _replies;
    public List<string> Prompts { get; } = new();
    public int ImageCallCount { get; private set; }

    public FakeLlmClient(params string?[] replies) => _replies = new Queue<string?>(replies);

    public Task<string?> SendAsync(string promptText, string? base64Image, string? mimeType)
    {
        Prompts.Add(promptText);
        return Task.FromResult(Next());
    }

    public Task<string?> SendAsync(string promptText, IReadOnlyList<(string Base64, string MimeType)> images)
    {
        Prompts.Add(promptText);
        if (images.Count > 0) ImageCallCount++;
        return Task.FromResult(Next());
    }

    private string? Next() => _replies.Count > 0 ? _replies.Dequeue() : null;
}

/// <summary>
/// An <see cref="ISshRunner"/> that records commands/uploads and replies via a matching function.
/// </summary>
public sealed class FakeSshRunner : ISshRunner
{
    private readonly Func<string, SshCommandResult> _responder;
    public List<string> Commands { get; } = new();
    public List<(string Local, string Remote)> Uploads { get; } = new();

    public FakeSshRunner(Func<string, SshCommandResult> responder) => _responder = responder;

    public SshCommandResult Run(string command)
    {
        Commands.Add(command);
        return _responder(command);
    }

    public void UploadFile(string localPath, string remotePath) => Uploads.Add((localPath, remotePath));

    public void Dispose() { }
}

/// <summary>
/// An <see cref="ILlmClient"/> scripted per call: each entry is either an exception to throw or a reply to
/// return (the last entry repeats once the script runs out). Counts calls.
/// </summary>
public sealed class ScriptedLlmClient : ILlmClient
{
    private readonly object?[] _script;
    public int Calls { get; private set; }

    public ScriptedLlmClient(params object?[] script) => _script = script;

    /// <summary>A client whose every call throws <paramref name="ex"/>.</summary>
    public static ScriptedLlmClient Throwing(Exception ex) => new(ex);

    public Task<string?> SendAsync(string promptText, string? base64Image, string? mimeType) => Next();

    public Task<string?> SendAsync(string promptText, IReadOnlyList<(string Base64, string MimeType)> images) => Next();

    private Task<string?> Next()
    {
        object? step = _script.Length == 0 ? null : _script[Math.Min(Calls, _script.Length - 1)];
        Calls++;
        return step is Exception ex ? Task.FromException<string?>(ex) : Task.FromResult((string?)step);
    }
}

/// <summary>An <see cref="IMcpPageFetcher"/> that returns canned page text by URL.</summary>
public sealed class FakeMcpPageFetcher : IMcpPageFetcher
{
    private readonly Dictionary<string, string?> _pages;

    public FakeMcpPageFetcher(Dictionary<string, string?> pagesByUrl) => _pages = pagesByUrl;

    public Task<string?> FetchContentAsync(string url, int maxLength)
        => Task.FromResult(_pages.TryGetValue(url, out var page) ? page : null);
}

/// <summary>An <see cref="IMcpPageFetcher"/> whose every fetch throws the supplied exception.</summary>
public sealed class ThrowingMcpPageFetcher : IMcpPageFetcher
{
    private readonly Exception _ex;

    public ThrowingMcpPageFetcher(Exception ex) => _ex = ex;

    public Task<string?> FetchContentAsync(string url, int maxLength) => Task.FromException<string?>(_ex);
}
