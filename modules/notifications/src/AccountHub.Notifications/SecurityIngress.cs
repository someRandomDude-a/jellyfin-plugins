using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AccountHub.Api;

namespace AccountHub.Notifications;

public sealed class IngressLimiter(TimeProvider time)
{
    private readonly object gate = new();
    private readonly Dictionary<string, (long Minute, int Count)> buckets = [];
    public bool Allow(string key, int limit)
    {
        lock (gate)
        {
            var minute = time.GetUtcNow().ToUnixTimeSeconds() / 60;
            if (buckets.Count >= 1024)
                foreach (var expired in buckets.Where(p => p.Value.Minute != minute).Select(p => p.Key).ToArray()) buckets.Remove(expired);
            if (!buckets.TryGetValue(key, out var bucket))
            {
                if (buckets.Count >= 1024) return false;
                bucket = (minute, 0);
            }
            if (bucket.Minute != minute) bucket = (minute, 0);
            buckets[key] = (minute, bucket.Count + 1);
            return bucket.Count < limit;
        }
    }
}

public sealed class SecurityIngress(NotificationStore store, INotificationHost host, TimeProvider time, IngressLimiter limiter)
{
    private static readonly Dictionary<string, (string Type, string Severity, string Title)> Known = new(StringComparer.Ordinal)
    {
        ["suspicious_login"] = ("security.suspicious_login", "high", "Suspicious sign-in reported"),
        ["emergency_lockout"] = ("security.emergency_lockout", "critical", "Emergency lockout reported"),
        ["recovery_code_used"] = ("security.recovery_code_used", "high", "Recovery method used"),
        ["passkey_registered"] = ("security.passkey_registered", "info", "Passkey registered"),
        ["totp_rotated"] = ("security.totp_rotated", "info", "Authenticator changed"),
        ["admin_force_logout"] = ("security.admin_force_logout", "high", "Administrator ended sessions"),
        ["login_attempt"] = ("security.login_attempt", "info", "Sign-in activity reported"),
        ["failed_attempts"] = ("security.failed_attempts", "warning", "Failed sign-ins reported"),
        ["pairing_request"] = ("security.pairing_request", "info", "Device pairing requested"),
        ["pairing_approved"] = ("security.pairing_approved", "info", "Device pairing approved"),
        ["pairing_denied"] = ("security.pairing_denied", "info", "Device pairing denied"),
        ["test"] = ("security.webhook.test", "info", "Security webhook test received")
        // lockout is deliberately unknown pending a verified tagged-source fixture.
    };

    public object Receive(byte[] body, string timestamp, string signature)
    {
        if (body.Length > Contract.EventLimit) throw new NotificationException("invalid_request", "Body too large", 413);
        if (!limiter.Allow("security-global", 120)) throw new NotificationException("rate_limited", "Ingress rate limit", 429);
        var secret = host.SecurityWebhookSecret;
        var now = time.GetUtcNow();
        var valid = !string.IsNullOrEmpty(secret) && long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) &&
            Math.Abs((decimal)now.ToUnixTimeSeconds() - seconds) <= 300;
        byte[] supplied = [];
        if (signature.StartsWith("sha256=", StringComparison.Ordinal) && signature.Length == 71)
            try { supplied = Convert.FromHexString(signature[7..]); } catch (FormatException) { }
        var signed = Encoding.UTF8.GetBytes(timestamp + ".").Concat(body).ToArray();
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret ?? "unconfigured"), signed);
        valid &= supplied.Length == expected.Length && CryptographicOperations.FixedTimeEquals(supplied, expected);
        if (!valid)
        {
            if (limiter.Allow("security-rejection-audit", 1))
            {
                var rejection = new AuditEvent(1, Guid.NewGuid(), "account-hub", "security.webhook.rejected", "denied", now);
                try { store.Receipt("rejection:" + rejection.EventId, null, rejection, false); }
                catch (Exception ex) when (ex is NotificationException or Microsoft.Data.Sqlite.SqliteException)
                { /* A best-effort rejection audit must never replace an authentication failure with success/503. */ }
            }
            throw new NotificationException("unauthorized", "Invalid webhook authentication", 401);
        }
        var hash = Convert.ToHexStringLower(SHA256.HashData(body));
        var eventId = new Guid(SHA256.HashData(body).AsSpan(0, 16));
        if (!host.SecurityCompatible)
        {
            var quarantineAudit = new AuditEvent(1, eventId, "jellyfin-security", "security.webhook.quarantined", "denied", now);
            store.Receipt(hash, null, quarantineAudit, true);
            return new { status = "quarantined" };
        }
        using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 16 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("event", out var label) || label.ValueKind != JsonValueKind.String)
            throw new NotificationException("invalid_request", "Missing Security event");
        var key = label.GetString()!;
        var mapped = Known.GetValueOrDefault(key, ("security.unknown", "warning", "Unrecognized Security event"));
        // Deliberately ignore ALL sender prose, payload, usernames, IPs and unknown fields.
        // Signed does not mean safe; even nested or mixed-case pairing codes cannot survive.
        var audit = new AuditEvent(1, eventId, "jellyfin-security", mapped.Item1, "success", now);
        NotificationEvent? entry = host.SecurityAlertMode is "hub" or "both" && key != "test"
            ? new(1, eventId, "jellyfin-security", mapped.Item1, mapped.Item2, mapped.Item3,
                "Open Jellyfin Security to review the event.", [], true, now) : null;
        var accepted = store.Receipt(hash, entry, audit, false);
        return new { status = accepted ? "accepted" : "duplicate" };
    }
}
