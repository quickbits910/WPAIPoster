using System.Diagnostics.CodeAnalysis;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Security;
using WPAIPoster.Config;

namespace WPAIPoster.Wordpress;

/// <summary>
/// <see cref="ISshRunner"/> implementation backed by SSH.NET. Supports private-key auth (with an
/// optional passphrase) and/or password auth, plus SFTP file upload. Fully cross-platform/managed.
/// </summary>
[ExcludeFromCodeCoverage]
public sealed class SshNetRunner : ISshRunner
{
    private readonly SshClient _ssh;
    private readonly SftpClient _sftp;

    private SshNetRunner(ConnectionInfo connectionInfo)
    {
        _ssh = new SshClient(connectionInfo);
        _sftp = new SftpClient(connectionInfo);
    }

    /// <summary>
    /// Builds connection details from <paramref name="cfg"/>, decrypting secrets with
    /// <paramref name="protector"/>, then connects both the command and SFTP channels.
    /// Tries full algorithm negotiation first; if the handshake fails (e.g. an OpenSSL
    /// "invalid digest" mismatch on newer stacks), retries once with a pinned modern algorithm set.
    /// Set <c>pinAlgorithms: true</c> in ssh-config.json to skip negotiation and pin from the start.
    /// </summary>
    public static SshNetRunner Connect(SshConfig cfg, SshConfigProtector protector)
    {
        if (string.IsNullOrWhiteSpace(cfg.Server))
            throw new InvalidOperationException("ssh-config.json is missing 'server'.");
        if (string.IsNullOrWhiteSpace(cfg.Username))
            throw new InvalidOperationException("ssh-config.json is missing 'username'.");

        // Parse the key / decrypt the password once; build fresh auth methods per attempt.
        PrivateKeyFile? privateKey = LoadPrivateKey(cfg, protector);
        string? password = cfg.PasswordEnc is { Length: > 0 } ? protector.Unprotect(cfg.PasswordEnc) : null;

        if (privateKey is null && password is null)
            throw new InvalidOperationException(
                "No SSH credentials configured: set 'keyPath' (with --set-key-password if the key is "
                + "passphrase-protected) or run --set-ssh-password for basic auth.");

        AuthenticationMethod[] BuildAuth()
        {
            var methods = new List<AuthenticationMethod>();
            if (privateKey is not null)
                methods.Add(new PrivateKeyAuthenticationMethod(cfg.Username, privateKey));
            if (password is not null)
                methods.Add(new PasswordAuthenticationMethod(cfg.Username, password));
            return methods.ToArray();
        }

        ConnectionInfo BuildConnInfo(bool pinned)
        {
            var ci = new ConnectionInfo(cfg.Server, cfg.EffectivePort, cfg.Username, BuildAuth());
            if (pinned)
                PinModernAlgorithms(ci);
            return ci;
        }

        bool forcePinned = cfg.PinAlgorithms == true;

        try
        {
            return ConnectWith(BuildConnInfo(forcePinned));
        }
        catch (Exception ex) when (!forcePinned && ShouldFallBackToPinned(ex))
        {
            Console.Error.WriteLine(
                $"SSH handshake failed ({ex.Message.Trim()}); retrying with a pinned modern algorithm set...");
            return ConnectWith(BuildConnInfo(pinned: true));
        }
    }

    private static PrivateKeyFile? LoadPrivateKey(SshConfig cfg, SshConfigProtector protector)
    {
        if (string.IsNullOrWhiteSpace(cfg.KeyPath))
            return null;

        string keyPath = ResolveKeyPath(cfg);
        if (!File.Exists(keyPath))
            throw new FileNotFoundException($"SSH private key not found at '{keyPath}'.");

        string? passphrase = cfg.PrivateKeyPwdEnc is { Length: > 0 }
            ? protector.Unprotect(cfg.PrivateKeyPwdEnc)
            : null;

        var pk = passphrase is null
            ? new PrivateKeyFile(keyPath)
            : new PrivateKeyFile(keyPath, passphrase);

        DropSha1Signature(pk);
        return pk;
    }

    /// <summary>
    /// Removes the legacy SHA-1 <c>ssh-rsa</c> signature variant from an RSA key when stronger
    /// <c>rsa-sha2-256/512</c> variants exist. Modern OpenSSH servers reject SHA-1 signatures, and
    /// system crypto-policies (e.g. Fedora) make OpenSSL refuse the SHA-1 digest outright — which
    /// otherwise surfaces during public-key auth as <c>error:03000098 ... invalid digest</c>.
    /// </summary>
    private static void DropSha1Signature(PrivateKeyFile pk)
    {
        if (pk.HostKeyAlgorithms is not IList<HostAlgorithm> algos)
            return;

        bool hasSha2 = algos.Any(a => a.Name is "rsa-sha2-256" or "rsa-sha2-512");
        if (!hasSha2)
            return; // nothing stronger to fall back to — leave the list untouched

        for (int i = algos.Count - 1; i >= 0; i--)
            if (algos[i].Name == "ssh-rsa")
                algos.RemoveAt(i);
    }

    private static SshNetRunner ConnectWith(ConnectionInfo connInfo)
    {
        var runner = new SshNetRunner(connInfo);
        try
        {
            runner._ssh.Connect();
            runner._sftp.Connect();
            return runner;
        }
        catch
        {
            runner.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Fall back to pinned algorithms for handshake/crypto failures, but not for genuine auth rejections
    /// (a wrong key/password would fail identically the second time and just obscure the real error).
    /// </summary>
    private static bool ShouldFallBackToPinned(Exception ex)
    {
        if (ex is SshAuthenticationException)
            return false;

        return ex is SshConnectionException
            || ex is SshException
            || ex.Message.Contains("digest", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("envelope", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Restricts the handshake to a known-good modern set matching a current OpenSSH server:
    /// curve25519 KEX, AES-256-GCM cipher, ed25519 host key, and SHA-2 HMACs (drops SHA-1 / CBC / legacy KEX).
    /// Each category is only narrowed if at least one preferred entry is actually available, so an unknown
    /// algorithm name can never empty a category and break negotiation.
    /// </summary>
    private static void PinModernAlgorithms(ConnectionInfo ci)
    {
        KeepOnly(ci.KeyExchangeAlgorithms, "curve25519-sha256", "curve25519-sha256@libssh.org");
        KeepOnly(ci.Encryptions, "aes256-gcm@openssh.com", "aes256-ctr");
        KeepOnly(ci.HostKeyAlgorithms, "ssh-ed25519", "rsa-sha2-512", "rsa-sha2-256");
        KeepOnly(ci.HmacAlgorithms,
            "hmac-sha2-256-etm@openssh.com", "hmac-sha2-512-etm@openssh.com",
            "hmac-sha2-256", "hmac-sha2-512");
    }

    private static void KeepOnly<T>(IDictionary<string, T> dict, params string[] preferred)
    {
        var keep = new HashSet<string>(preferred.Where(dict.ContainsKey));
        if (keep.Count == 0)
            return; // none of the preferred names exist in this SSH.NET build — leave the category alone

        foreach (string name in dict.Keys.Where(k => !keep.Contains(k)).ToList())
            dict.Remove(name);
    }

    /// <summary>Resolves a relative key path against the directory of the loaded ssh-config.json.</summary>
    private static string ResolveKeyPath(SshConfig cfg)
    {
        string keyPath = cfg.KeyPath!;
        if (Path.IsPathRooted(keyPath))
            return keyPath;

        string baseDir = cfg.LoadedFrom is { Length: > 0 }
            ? Path.GetDirectoryName(Path.GetFullPath(cfg.LoadedFrom)) ?? Directory.GetCurrentDirectory()
            : Directory.GetCurrentDirectory();

        return Path.GetFullPath(Path.Combine(baseDir, keyPath));
    }

    public SshCommandResult Run(string command)
    {
        using var cmd = _ssh.CreateCommand(command);
        string output = cmd.Execute();
        return new SshCommandResult(cmd.ExitStatus ?? -1, output, cmd.Error);
    }

    public void UploadFile(string localPath, string remotePath)
    {
        using var fs = File.OpenRead(localPath);
        _sftp.UploadFile(fs, remotePath, canOverride: true);
    }

    public void Dispose()
    {
        try { if (_sftp.IsConnected) _sftp.Disconnect(); } catch { /* ignore */ }
        try { if (_ssh.IsConnected) _ssh.Disconnect(); } catch { /* ignore */ }
        _sftp.Dispose();
        _ssh.Dispose();
    }
}
