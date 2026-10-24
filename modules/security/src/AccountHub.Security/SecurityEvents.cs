using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AccountHub.Security;

public sealed record SecurityEvent(string Type, string Severity, string Title, string Body,
    DateTimeOffset OccurredAt, string? Username, string? IpHash)
{
    public string Source => "jellyfin-security";
    public bool AdminOnly => true;
    public bool IsTest => Type == "security.webhook.test";
    public bool IsNoisy => Type is "security.login_attempt" or "security.failed_attempts";
}

public static class SecurityEventParser
{
    public static SecurityEvent Parse(ReadOnlyMemory<byte> rawBody, DateTimeOffset receivedAt, ReadOnlySpan<byte> ipHashKey)
    {
        using var doc = JsonDocument.Parse(rawBody, new JsonDocumentOptions { MaxDepth = 16 });
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException();
        var wire = Text(root, "event", 80);
        var (type, severity, title) = wire switch
        {
            "lockout" => ("security.lockout", "high", "Account locked out"),
            "suspicious_login" => ("security.suspicious_login", "high", "Suspicious sign-in"),
            "emergency_lockout" => ("security.emergency_lockout", "critical", "Emergency lockout"),
            "recovery_code_used" => ("security.recovery_code_used", "high", "Recovery code used"),
            "passkey_registered" => ("security.passkey_registered", "info", "Passkey registered"),
            "totp_rotated" => ("security.totp_rotated", "info", "Authenticator rotated"),
            "admin_force_logout" => ("security.admin_force_logout", "high", "Administrator forced logout"),
            "login_attempt" => ("security.login_attempt", "info", "Sign-in attempt summary"),
            "failed_attempts" => ("security.failed_attempts", "warning", "Failed sign-in summary"),
            "pairing_request" => ("security.pairing_request", "info", "Device pairing requested"),
            "pairing_approved" => ("security.pairing_approved", "info", "Device pairing approved"),
            "pairing_denied" => ("security.pairing_denied", "info", "Device pairing denied"),
            "test" => ("security.webhook.test", "info", "Security webhook test received"),
            _ => ("security.unknown", "warning", "Unrecognized Security event")
        };
        var occurred = root.TryGetProperty("timestamp", out var time) && time.ValueKind == JsonValueKind.String
            && time.TryGetDateTimeOffset(out var parsed) ? parsed : receivedAt;
        string? username = null, ipHash = null;
        // Unknown and pairing events are entirely redacted, including names/device labels.
        if (type != "security.unknown" && !type.StartsWith("security.pairing_", StringComparison.Ordinal)
            && root.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.Object)
        {
            username = Text(payload, "username", 128);
            var ip = Text(payload, "ip", 64) ?? Text(payload, "remoteIp", 64);
            if (ip is not null && IPAddress.TryParse(ip, out var address))
            {
                if (ipHashKey.Length < 32) throw new ArgumentException("A separate strong privacy hash key is required.");
                ipHash = Convert.ToHexStringLower(HMACSHA256.HashData(ipHashKey, Encoding.UTF8.GetBytes(address.ToString())));
            }
        }
        // Deliberately never copies title/message, deviceName, pairingCode, unknown/nested fields or wire label.
        return new(type, severity, title, "Open Jellyfin Security for details.", occurred, username, ipHash);
    }

    private static string? Text(JsonElement parent, string key, int max)
    {
        if (!parent.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String) return null;
        var text = value.GetString();
        return text is { Length: > 0 } && text.Length <= max && !text.Any(char.IsControl) ? text : null;
    }
}
