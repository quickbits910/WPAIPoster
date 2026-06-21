using System.Globalization;

namespace WPAIPoster.Ui;

/// <summary>
/// Writes a single timestamped, plain-text log file for one run. Every line goes through here so a run can
/// be reviewed after the fact. Messages are written <b>verbatim</b> — callers pass plain text (the console
/// styling is applied separately by <see cref="Ui"/>), so the log faithfully records content that contains
/// brackets (JSON arrays, code, markdown). The pure <see cref="BuildLogFileName"/> helper is unit-tested;
/// the file I/O is thin.
/// </summary>
public sealed class RunLogger : IDisposable
{
    private readonly TextWriter _writer;
    private readonly object _gate = new();

    /// <summary>Absolute path of the log file being written.</summary>
    public string LogPath { get; }

    /// <summary>Opens a new uniquely-named log file under <paramref name="outputFolder"/>.</summary>
    public RunLogger(string outputFolder, DateTime startedAt, string token)
    {
        Directory.CreateDirectory(outputFolder);
        LogPath = Path.Combine(outputFolder, BuildLogFileName(startedAt, token));
        _writer = new StreamWriter(LogPath, append: false) { AutoFlush = true };
        Write("INFO", $"Run started {startedAt:yyyy-MM-dd HH:mm:ss}");
    }

    /// <summary>Builds the per-run file name, e.g. <c>run-20260620-141233-a1b2c3d4.log</c>.</summary>
    public static string BuildLogFileName(DateTime startedAt, string token)
    {
        string stamp = startedAt.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string safe = new string((token ?? string.Empty).Where(char.IsLetterOrDigit).ToArray());
        if (safe.Length == 0) safe = "log";
        if (safe.Length > 12) safe = safe[..12];
        return $"run-{stamp}-{safe}.log";
    }

    /// <summary>Appends a single line, verbatim: <c>[HH:mm:ss LEVEL] message</c>.</summary>
    public void Write(string level, string message)
    {
        string line = $"[{TimeOnly.FromDateTime(DateTime.Now):HH:mm:ss} {level}] {message}";
        lock (_gate)
            _writer.WriteLine(line);
    }

    public void Dispose()
    {
        lock (_gate)
            _writer.Dispose();
    }
}
