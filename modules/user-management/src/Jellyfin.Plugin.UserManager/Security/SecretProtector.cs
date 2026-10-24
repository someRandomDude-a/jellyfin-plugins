using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.UserManager.Security;

/// <summary>
/// AES-GCM protection for secrets stored in the plugin database. The key lives in a separate
/// file in the plugin data directory and is never written to the database or logs.
/// </summary>
public sealed class SecretProtector
{
    private const string Prefix = "v1";
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly string _keyPath;
    private readonly Lazy<byte[]> _key;

    public SecretProtector(string dataFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataFolder);
        Directory.CreateDirectory(dataFolder);
        _keyPath = Path.Combine(dataFolder, "secrets.key");
        _key = new Lazy<byte[]>(LoadOrCreateKey, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>
    /// Gets the path of the encryption key file.
    /// </summary>
    public string KeyPath => _keyPath;

    public string Protect(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);

        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[plainBytes.Length];
        var tag = new byte[TagSize];

        using (var aes = new AesGcm(_key.Value, TagSize))
        {
            aes.Encrypt(nonce, plainBytes, cipher, tag);
        }

        CryptographicOperations.ZeroMemory(plainBytes);

        return string.Join(
            '.',
            Prefix,
            Base64Url.Encode(nonce),
            Base64Url.Encode(cipher),
            Base64Url.Encode(tag));
    }

    public string Unprotect(string protectedValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(protectedValue);
        if (!File.Exists(_keyPath)) throw new CryptographicException("Restore the original lifecycle encryption key; existing ciphertext must never generate a replacement key.");

        var parts = protectedValue.Split('.');
        if (parts.Length != 4 || !string.Equals(parts[0], Prefix, StringComparison.Ordinal))
        {
            throw new FormatException("The protected value has an unknown format.");
        }

        var nonce = Base64Url.Decode(parts[1]);
        var cipher = Base64Url.Decode(parts[2]);
        var tag = Base64Url.Decode(parts[3]);

        if (nonce.Length != NonceSize || tag.Length != TagSize)
        {
            throw new FormatException("The protected value is malformed.");
        }

        var plain = new byte[cipher.Length];
        using (var aes = new AesGcm(_key.Value, TagSize))
        {
            aes.Decrypt(nonce, cipher, tag, plain);
        }

        try
        {
            return Encoding.UTF8.GetString(plain);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public bool TryUnprotect(string? protectedValue, out string? plaintext)
    {
        plaintext = null;
        if (string.IsNullOrWhiteSpace(protectedValue))
        {
            return false;
        }

        try
        {
            plaintext = Unprotect(protectedValue);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return false;
        }
    }

    private byte[] LoadOrCreateKey()
    {
        if (File.Exists(_keyPath))
        {
            var existing = File.ReadAllBytes(_keyPath);
            if (existing.Length == KeySize)
            {
                return existing;
            }

            throw new InvalidOperationException(
                "The lifecycle encryption key is invalid. Restore the database and original key together.");
        }

        var key = RandomNumberGenerator.GetBytes(KeySize);
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.WriteThrough
            };

            using var stream = new FileStream(_keyPath, options);
            stream.Write(key);
            stream.Flush(true);
        }
        catch (IOException) when (File.Exists(_keyPath))
        {
            // Another thread/process won the race.
            return LoadOrCreateKey();
        }

        TryRestrictPermissions(_keyPath);
        return key;
    }

    private static void TryRestrictPermissions(string path)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            else
            {
                File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
            }
        }
        catch
        {
            // Permission hardening is best effort; the key still lives outside the database.
        }
    }
}
