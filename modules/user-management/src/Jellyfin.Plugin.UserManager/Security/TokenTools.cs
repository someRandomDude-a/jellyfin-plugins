using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.UserManager.Security;

/// <summary>
/// Random token creation and one-way hashing helpers. Only token hashes are ever persisted.
/// </summary>
public static class TokenTools
{
    private const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    /// <summary>
    /// Creates a cryptographically random URL-safe token (32 bytes).
    /// </summary>
    public static string CreateUrlToken() => Base64Url.Encode(RandomNumberGenerator.GetBytes(32));

    /// <summary>
    /// Creates a short human-friendly linking code, e.g. <c>K7P2-Q9XM</c>.
    /// </summary>
    public static string CreateLinkCode()
    {
        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);
        var chars = new char[9];
        for (var i = 0; i < 8; i++)
        {
            chars[i < 4 ? i : i + 1] = CodeAlphabet[bytes[i] % CodeAlphabet.Length];
        }

        chars[4] = '-';
        return new string(chars);
    }

    /// <summary>
    /// Normalizes user-entered codes (case and separators are ignored).
    /// </summary>
    public static string NormalizeCode(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        var normalized = new StringBuilder(code.Length);
        foreach (var c in code.Trim().ToUpperInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                normalized.Append(c);
            }
        }

        return normalized.ToString();
    }

    /// <summary>
    /// Returns a SHA-256 hex hash of a token. Input is normalized for link codes before hashing.
    /// </summary>
    public static string HashToken(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static string HashCode(string code) => HashToken(NormalizeCode(code));

    public static bool FixedTimeEquals(string left, string right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(left),
            Encoding.UTF8.GetBytes(right));
    }
}

/// <summary>
/// Unpadded base64url encoding.
/// </summary>
public static class Base64Url
{
    public static string Encode(ReadOnlySpan<byte> data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Decode(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch
        {
            2 => "==",
            3 => "=",
            0 => string.Empty,
            _ => throw new FormatException("Invalid base64url value.")
        };

        return Convert.FromBase64String(padded);
    }
}
