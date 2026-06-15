using System.Text.Json;
using System.Text.Json.Serialization;

namespace WPAIPoster.Config;

/// <summary>
/// SSH connection settings loaded from <c>ssh-config.json</c>. The password (if used) is stored
/// as an AES-GCM ciphertext in <see cref="PasswordEnc"/> and decrypted at runtime via
/// <see cref="SshConfigProtector"/>. Private-key auth is preferred when <see cref="KeyPath"/> is set.
/// </summary>
public sealed class SshConfig
{
    [JsonPropertyName("server")]
    public string? Server { get; set; }

    /// <summary>SSH port. Null/0 means use the default (22).</summary>
    [JsonPropertyName("port")]
    public int? Port { get; set; }

    [JsonPropertyName("username")]
    public string? Username { get; set; }

    /// <summary>Path to the private key file used for key-based auth.</summary>
    [JsonPropertyName("keyPath")]
    public string? KeyPath { get; set; }

    /// <summary>
    /// Encrypted passphrase (AES-GCM ciphertext) that unlocks the private key at <see cref="KeyPath"/>.
    /// Used for key-based auth when the private key is passphrase-protected.
    /// </summary>
    [JsonPropertyName("privateKeyPwdEnc")]
    public string? PrivateKeyPwdEnc { get; set; }

    /// <summary>
    /// Encrypted SSH login password (AES-GCM ciphertext) for username/password (basic) auth.
    /// Independent of <see cref="PrivateKeyPwdEnc"/>; both auth methods may be configured at once.
    /// </summary>
    [JsonPropertyName("passwordEnc")]
    public string? PasswordEnc { get; set; }

    /// <summary>
    /// Optional path to a system <c>ssh</c> executable. Retained for a future external-ssh transport mode;
    /// the default SSH.NET transport does not use it.
    /// </summary>
    [JsonPropertyName("sshExecutablePath")]
    public string? SshExecutablePath { get; set; }

    /// <summary>
    /// When true, always restrict the SSH handshake to a known-good modern algorithm set (curve25519,
    /// aes256-gcm, ed25519, sha2 HMACs) instead of negotiating freely. When null/false, the runner tries
    /// full negotiation first and only falls back to the pinned set if the handshake fails.
    /// </summary>
    [JsonPropertyName("pinAlgorithms")]
    public bool? PinAlgorithms { get; set; }

    /// <summary>Resolved port, defaulting to 22 when unset.</summary>
    [JsonIgnore]
    public int EffectivePort => Port is > 0 ? Port.Value : 22;

    /// <summary>Absolute path of the file that was loaded, or null if none was found.</summary>
    [JsonIgnore]
    public string? LoadedFrom { get; private set; }

    private const string FileName = "ssh-config.json";

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
    public static SshConfig Load()
    {
        string cwd = Path.Combine(Directory.GetCurrentDirectory(), FileName);
        string base_ = Path.Combine(AppContext.BaseDirectory, FileName);

        string? found = File.Exists(cwd) ? cwd :
                        File.Exists(base_) ? base_ : null;

        return Load(found);
    }

    public static SshConfig Load(string? path)
    {
        if (path is null || !File.Exists(path))
            return new SshConfig();

        try
        {
            string json = File.ReadAllText(path);
            var cfg = JsonSerializer.Deserialize<SshConfig>(json, ReadOptions) ?? new SshConfig();
            cfg.LoadedFrom = path;
            return cfg;
        }
        catch
        {
            Console.WriteLine("Warning: ssh-config.json could not be parsed — using defaults.");
            return new SshConfig();
        }
    }

    /// <summary>Serializes this config back to <paramref name="path"/> (used by the --set-ssh-password verb).</summary>
    public void Save(string path)
    {
        string json = JsonSerializer.Serialize(this, WriteOptions);
        File.WriteAllText(path, json);
    }
}
