using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace WPAIPoster.Config;

/// <summary>
/// Cross-platform symmetric encryption for secrets stored in <c>ssh-config.json</c> (SSH password,
/// key passphrase). Uses AES-256-GCM with a random 32-byte key persisted in a sibling key file.
/// The encrypted payload is base64(<c>nonce(12) || tag(16) || ciphertext</c>).
/// </summary>
public sealed class SshConfigProtector
{
    private const int KeySize = 32;   // AES-256
    private const int NonceSize = 12; // AES-GCM standard nonce
    private const int TagSize = 16;   // AES-GCM standard tag

    private readonly string _keyFilePath;

    /// <summary>Creates a protector that stores/reads its key at <paramref name="keyFilePath"/>.</summary>
    public SshConfigProtector(string keyFilePath)
    {
        _keyFilePath = keyFilePath;
    }

    /// <summary>
    /// Returns the conventional key-file path that sits next to a given <c>ssh-config.json</c> path
    /// (same directory, <c>ssh-config.key</c>).
    /// </summary>
    public static string KeyFilePathFor(string configPath)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? ".";
        return Path.Combine(dir, "ssh-config.key");
    }

    /// <summary>Encrypts <paramref name="plaintext"/> and returns a base64 payload.</summary>
    public string Protect(string plaintext)
    {
        byte[] key = LoadOrCreateKey();
        byte[] plain = Encoding.UTF8.GetBytes(plaintext);
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
        byte[] cipher = new byte[plain.Length];
        byte[] tag = new byte[TagSize];

        using (var aes = new AesGcm(key, TagSize))
            aes.Encrypt(nonce, plain, cipher, tag);

        byte[] payload = new byte[NonceSize + TagSize + cipher.Length];
        Buffer.BlockCopy(nonce, 0, payload, 0, NonceSize);
        Buffer.BlockCopy(tag, 0, payload, NonceSize, TagSize);
        Buffer.BlockCopy(cipher, 0, payload, NonceSize + TagSize, cipher.Length);
        return Convert.ToBase64String(payload);
    }

    /// <summary>Decrypts a base64 payload produced by <see cref="Protect"/>.</summary>
    public string Unprotect(string encoded)
    {
        byte[] key = LoadKeyOrThrow();
        byte[] payload = Convert.FromBase64String(encoded);
        if (payload.Length < NonceSize + TagSize)
            throw new CryptographicException("Encrypted SSH secret is malformed.");

        byte[] nonce = new byte[NonceSize];
        byte[] tag = new byte[TagSize];
        int cipherLen = payload.Length - NonceSize - TagSize;
        byte[] cipher = new byte[cipherLen];
        Buffer.BlockCopy(payload, 0, nonce, 0, NonceSize);
        Buffer.BlockCopy(payload, NonceSize, tag, 0, TagSize);
        Buffer.BlockCopy(payload, NonceSize + TagSize, cipher, 0, cipherLen);

        byte[] plain = new byte[cipherLen];
        using (var aes = new AesGcm(key, TagSize))
            aes.Decrypt(nonce, cipher, tag, plain);

        return Encoding.UTF8.GetString(plain);
    }

    private byte[] LoadOrCreateKey()
    {
        if (File.Exists(_keyFilePath))
            return LoadKeyOrThrow();

        byte[] key = RandomNumberGenerator.GetBytes(KeySize);
        string? dir = Path.GetDirectoryName(_keyFilePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllText(_keyFilePath, Convert.ToBase64String(key));
        RestrictPermissions(_keyFilePath);
        return key;
    }

    private byte[] LoadKeyOrThrow()
    {
        if (!File.Exists(_keyFilePath))
            throw new FileNotFoundException(
                $"SSH key file not found at '{_keyFilePath}'. Re-run --set-ssh-password to recreate the secret.");

        return Convert.FromBase64String(File.ReadAllText(_keyFilePath).Trim());
    }

    /// <summary>Restricts the key file to the current user (0600 on Unix; best-effort elsewhere).</summary>
    private static void RestrictPermissions(string path)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch
            {
                // Best-effort; not all filesystems support Unix modes.
            }
        }
        // On Windows the file lives under the app/config directory; NTFS inherits user-scoped ACLs.
    }
}
