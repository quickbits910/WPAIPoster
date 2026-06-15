namespace WPAIPoster.Wordpress;

/// <summary>Result of running a remote command.</summary>
public readonly record struct SshCommandResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Success => ExitCode == 0;
    public string TrimmedOut => StdOut.Trim();
}

/// <summary>
/// Abstraction over an SSH session so the WP-CLI publisher can be unit-tested with a fake.
/// </summary>
public interface ISshRunner : IDisposable
{
    /// <summary>Runs <paramref name="command"/> on the remote host and returns its result.</summary>
    SshCommandResult Run(string command);

    /// <summary>Uploads a local file to <paramref name="remotePath"/> via SFTP.</summary>
    void UploadFile(string localPath, string remotePath);
}
