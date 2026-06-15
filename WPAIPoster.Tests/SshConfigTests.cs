using System.Security.Cryptography;
using WPAIPoster.Config;

namespace WPAIPoster.Tests;

public class SshConfigTests : IDisposable
{
    private readonly string _tempDir;

    public SshConfigTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"WPAISsh_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void Load_AllFields_AndDefaultPort()
    {
        string path = Path.Combine(_tempDir, "ssh-config.json");
        File.WriteAllText(path, """
            { "server": "host", "username": "luke", "keyPath": "id_rsa" }
            """);

        var cfg = SshConfig.Load(path);

        Assert.Equal("host", cfg.Server);
        Assert.Equal("luke", cfg.Username);
        Assert.Equal("id_rsa", cfg.KeyPath);
        Assert.Equal(22, cfg.EffectivePort); // default
        Assert.Equal(path, cfg.LoadedFrom);
    }

    [Fact]
    public void EffectivePort_HonorsExplicitValue()
    {
        var cfg = new SshConfig { Port = 2222 };
        Assert.Equal(2222, cfg.EffectivePort);
    }

    [Fact]
    public void Save_RoundTrips()
    {
        string path = Path.Combine(_tempDir, "ssh-config.json");
        var cfg = new SshConfig
        {
            Server = "h", Username = "u", Port = 2200,
            PasswordEnc = "abc", PrivateKeyPwdEnc = "xyz"
        };
        cfg.Save(path);

        var loaded = SshConfig.Load(path);

        Assert.Equal("h", loaded.Server);
        Assert.Equal("u", loaded.Username);
        Assert.Equal(2200, loaded.Port);
        Assert.Equal("abc", loaded.PasswordEnc);
        Assert.Equal("xyz", loaded.PrivateKeyPwdEnc);
        // The key-passphrase field uses the privateKeyPwdEnc JSON name.
        Assert.Contains("privateKeyPwdEnc", File.ReadAllText(path));
    }
}

public class SshConfigProtectorTests : IDisposable
{
    private readonly string _tempDir;

    public SshConfigProtectorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"WPAIProt_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void Protect_Unprotect_RoundTrips()
    {
        var p = new SshConfigProtector(Path.Combine(_tempDir, "k.key"));
        const string secret = "Sup3r$ecret with spaces & symbols ✓";

        string enc = p.Protect(secret);
        string dec = p.Unprotect(enc);

        Assert.Equal(secret, dec);
        Assert.NotEqual(secret, enc);
    }

    [Fact]
    public void Protect_CreatesKeyFile()
    {
        string keyPath = Path.Combine(_tempDir, "k.key");
        var p = new SshConfigProtector(keyPath);

        p.Protect("x");

        Assert.True(File.Exists(keyPath));
    }

    [Fact]
    public void Unprotect_WithDifferentKey_Fails()
    {
        var p1 = new SshConfigProtector(Path.Combine(_tempDir, "k1.key"));
        var p2 = new SshConfigProtector(Path.Combine(_tempDir, "k2.key"));

        string enc = p1.Protect("hello");
        p2.Protect("init"); // create p2's own (different) key file
        // p2's key differs from p1's; GCM authentication must fail.
        Assert.ThrowsAny<CryptographicException>(() => p2.Unprotect(enc));
    }

    [Fact]
    public void Unprotect_MissingKeyFile_Throws()
    {
        var p = new SshConfigProtector(Path.Combine(_tempDir, "absent.key"));
        Assert.Throws<FileNotFoundException>(() => p.Unprotect("AAAAAAAAAAAAAAAAAAAAAAAAAAAA"));
    }

    [Fact]
    public void KeyFilePathFor_IsSiblingOfConfig()
    {
        string keyPath = SshConfigProtector.KeyFilePathFor("/a/b/ssh-config.json");
        Assert.Equal(Path.Combine("/a/b", "ssh-config.key"), keyPath);
    }

    [Fact]
    public void KeyFile_RestrictedOnUnix()
    {
        if (OperatingSystem.IsWindows()) return; // Unix-only assertion

        string keyPath = Path.Combine(_tempDir, "k.key");
        new SshConfigProtector(keyPath).Protect("x");

        UnixFileMode mode = File.GetUnixFileMode(keyPath);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
    }
}
