using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
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
    private SshClient _ssh;
    private SftpClient _sftp;

    // Re-establishes a fresh, connected (ssh, sftp) pair using the original auth / host-key wiring.
    // Called on construction and again by Reconnect() after a dropped connection.
    private readonly Func<(SshClient Ssh, SftpClient Sftp)> _establish;

    private SshNetRunner(Func<(SshClient Ssh, SftpClient Sftp)> establish)
    {
        _establish = establish;
        (_ssh, _sftp) = establish();
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

        // Host-key verification (anti-MITM): reject the connection unless the server's key matches the
        // pinned fingerprint; trust-on-first-use when no fingerprint is configured yet.
        string? expectedFp = NormalizeFingerprint(cfg.HostKeyFingerprint);
        string? learnedFp = null;
        (string Expected, string Observed)? mismatch = null;

        void VerifyHostKey(object? _, HostKeyEventArgs e)
        {
            string observed = NormalizeFingerprint(e.FingerPrintSHA256) ?? string.Empty;
            if (expectedFp is null)
            {
                learnedFp = observed;          // trust-on-first-use: remember and accept
                e.CanTrust = true;
            }
            else if (FingerprintsEqual(expectedFp, observed))
            {
                e.CanTrust = true;
            }
            else
            {
                e.CanTrust = false;            // reject → Connect throws; surfaced as a clear error below
                mismatch = (expectedFp, observed);
            }
        }

        TimeSpan keepAlive = cfg.EffectiveKeepAliveInterval;
        bool forcePinned = cfg.PinAlgorithms == true;

        // Builds a fresh, connected (ssh, sftp) pair with the host-key handler wired and the pinned
        // fallback applied. Reused verbatim by Reconnect() when a dropped idle connection is retried,
        // so the anti-MITM / handshake behaviour is identical on reconnection.
        (SshClient Ssh, SftpClient Sftp) EstablishOnce(bool pinned)
        {
            ConnectionInfo ci = BuildConnInfo(pinned);
            var ssh = new SshClient(ci) { KeepAliveInterval = keepAlive };
            var sftp = new SftpClient(ci) { KeepAliveInterval = keepAlive };
            ssh.HostKeyReceived += VerifyHostKey;
            sftp.HostKeyReceived += VerifyHostKey;
            try
            {
                ssh.Connect();
                sftp.Connect();
                return (ssh, sftp);
            }
            catch
            {
                try { ssh.Dispose(); } catch { /* ignore */ }
                try { sftp.Dispose(); } catch { /* ignore */ }
                throw;
            }
        }

        (SshClient Ssh, SftpClient Sftp) Establish()
        {
            mismatch = null;                       // reset per attempt so a stale value can't skew the guard
            try
            {
                return EstablishOnce(forcePinned);
            }
            catch (Exception ex) when (!forcePinned && mismatch is null && ShouldFallBackToPinned(ex))
            {
                Console.Error.WriteLine(
                    $"SSH handshake failed ({ex.Message.Trim()}); retrying with a pinned modern algorithm set...");
                return EstablishOnce(pinned: true);
            }
        }

        SshNetRunner connected;
        try
        {
            connected = new SshNetRunner(Establish);
        }
        catch when (mismatch is not null)
        {
            throw new InvalidOperationException(
                $"SSH host key mismatch — possible man-in-the-middle. Expected SHA256:{mismatch.Value.Expected} " +
                $"but the server presented SHA256:{mismatch.Value.Observed}. If the server's key legitimately " +
                "changed, update 'hostKeyFingerprint' in ssh-config.json (or clear it to re-pin).");
        }

        // Trust-on-first-use: persist the freshly-seen fingerprint so later connections are verified.
        if (expectedFp is null && learnedFp is { Length: > 0 })
        {
            // Verify against this key on any in-process Reconnect() too, rather than re-learning it.
            expectedFp = learnedFp;
            if (cfg.LoadedFrom is { Length: > 0 })
            {
                cfg.HostKeyFingerprint = learnedFp;
                try
                {
                    cfg.Save(cfg.LoadedFrom);
                    Console.Error.WriteLine(
                        $"Pinned SSH host key SHA256:{learnedFp} to {cfg.LoadedFrom} (trust-on-first-use).");
                }
                catch { /* best-effort; verification still happened this run */ }
            }
        }

        return connected;
    }

    /// <summary>Strips an optional "SHA256:" prefix, surrounding whitespace, and base64 padding for comparison.</summary>
    public static string? NormalizeFingerprint(string? fingerprint)
    {
        if (string.IsNullOrWhiteSpace(fingerprint))
            return null;

        string s = fingerprint.Trim();
        if (s.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase))
            s = s["SHA256:".Length..];
        return s.TrimEnd('=');
    }

    /// <summary>Constant-time comparison of two already-normalized SHA-256 fingerprints.</summary>
    public static bool FingerprintsEqual(string expected, string observed)
        => CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(observed));

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
        => WithReconnect(() =>
        {
            using var cmd = _ssh.CreateCommand(command);
            string output = cmd.Execute();
            return new SshCommandResult(cmd.ExitStatus ?? -1, output, cmd.Error);
        });

    public void UploadFile(string localPath, string remotePath)
        => WithReconnect(() =>
        {
            // Open the stream inside the lambda so a retry gets a fresh, rewound FileStream.
            using var fs = File.OpenRead(localPath);
            _sftp.UploadFile(fs, remotePath, canOverride: true);
            return true;
        });

    /// <summary>
    /// Runs <paramref name="op"/> and, on a dropped connection, reconnects and retries it <b>once</b>.
    /// The connection is opened up front but sits idle for minutes while the post is generated and every
    /// image is vision-scored, so a keep-alive can't stop a server (shared hosting) from actively
    /// resetting the idle session — the first op of the publish phase then throws. Reconnect-and-retry
    /// re-establishes the session transparently and completes the operation. Non-transient failures
    /// (auth, a non-zero WP-CLI exit — which isn't an exception) fall straight through.
    /// <para>
    /// Idempotency note: in the realistic failure the drop hits the idle first op — the SFTP body upload,
    /// which is idempotent (<c>canOverride: true</c>) — and the back-to-back commands that follow a fresh
    /// reconnect don't idle long enough to drop, so re-running a non-idempotent command (e.g.
    /// <c>wp post create</c>) after a mid-publish reset is effectively theoretical.
    /// </para>
    /// </summary>
    private T WithReconnect<T>(Func<T> op)
    {
        try
        {
            return op();
        }
        catch (Exception ex) when (IsTransientConnectionError(ex))
        {
            Reconnect();
            return op();
        }
    }

    /// <summary>Disposes the current clients and re-establishes a fresh, connected pair.</summary>
    private void Reconnect()
    {
        try { if (_sftp.IsConnected) _sftp.Disconnect(); } catch { /* ignore */ }
        try { if (_ssh.IsConnected) _ssh.Disconnect(); } catch { /* ignore */ }
        try { _sftp.Dispose(); } catch { /* ignore */ }
        try { _ssh.Dispose(); } catch { /* ignore */ }

        (_ssh, _sftp) = _establish();
    }

    /// <summary>
    /// True when <paramref name="ex"/> (or any inner exception) indicates the SSH/SFTP connection
    /// dropped and the operation is worth retrying on a fresh session — a reset/closed socket,
    /// an operation timeout, or an <see cref="ObjectDisposedException"/> SSH.NET throws when the
    /// underlying session died. A genuine auth rejection (<see cref="SshAuthenticationException"/>)
    /// is <b>not</b> transient — it would fail identically on retry and must surface.
    /// </summary>
    public static bool IsTransientConnectionError(Exception? ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is SshAuthenticationException)
                return false;
            if (e is SshConnectionException or SocketException
                or SshOperationTimeoutException or ObjectDisposedException)
                return true;
        }
        return false;
    }

    public void Dispose()
    {
        try { if (_sftp.IsConnected) _sftp.Disconnect(); } catch { /* ignore */ }
        try { if (_ssh.IsConnected) _ssh.Disconnect(); } catch { /* ignore */ }
        _sftp.Dispose();
        _ssh.Dispose();
    }
}
