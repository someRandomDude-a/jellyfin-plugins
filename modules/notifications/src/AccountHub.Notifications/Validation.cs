using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AccountHub.Api;

namespace AccountHub.Notifications;

internal static partial class Validation
{
    [GeneratedRegex("^[a-z0-9]+(?:[._-][a-z0-9]+)+$")]
    private static partial Regex Dotted();
    public static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static void Require(bool condition, string reason)
    {
        if (!condition) throw new NotificationException("rejected", reason);
    }
    public static bool SafeLink(string? link, NotificationOptions options)
    {
        if (link is null) return true;
        if (link.Length > 1024 || link.Any(c => char.IsControl(c) || c == '\\') || link.Contains('%')) return false;
        // Fragment routes and base-path-relative web links only; never root-relative or auth/token pages.
        if (link.Contains("token", StringComparison.OrdinalIgnoreCase) || link.Contains("redirect", StringComparison.OrdinalIgnoreCase)) return false;
        if (link.StartsWith("#/") || link.StartsWith("web/index.html#/")) return true;
        return Uri.TryCreate(link, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
            string.IsNullOrEmpty(uri.UserInfo) && options.AllowedLinkHosts.Contains(uri.IdnHost);
    }
    public static void Event(NotificationEvent e, NotificationOptions options, ProducerIdentity? producer)
    {
        Require(e.SchemaVersion == 1 && e.EventId != Guid.Empty, "Invalid event version or ID");
        Require(e.Source is not null && options.Sources.Contains(e.Source), "Unknown source");
        if (producer is not null)
            Require(producer.Source == e.Source && e.Source != "jellyfin-security" &&
                producer.Scopes.Contains("notifications:write") && (!e.AdminOnly || producer.Scopes.Contains("notifications:admin")), "Producer scope/source denied");
        Require(e.Type is not null && e.Type.Length <= 100 && Dotted().IsMatch(e.Type), "Invalid type");
        Require(e.Severity is "info" or "warning" or "high" or "critical", "Invalid severity");
        Require(e.Title is not null && e.Title.Length is > 0 and <= 200 && e.Body is not null && e.Body.Length <= 4000, "Invalid text length");
        Require(e.Audience is not null && e.Audience.Length <= options.MaxAudience && e.Audience.All(x => x != Guid.Empty), "Invalid audience");
        Require(!e.AdminOnly || e.Audience!.Length == 0, "Admin audience must be resolved by host");
        Require(e.OccurredAt != default && e.DedupeKey?.Length is not > 200 && SafeLink(e.Link, options), "Invalid time, dedupe key or link");
        Require(e.Metadata is null || (e.Metadata.Count <= 20 && e.Metadata.All(p => p.Key.Length <= 80 && p.Value is not null && p.Value.Length <= 500 && !SecretKey(p.Key))), "Unsafe metadata");
        Require(Encoding.UTF8.GetByteCount(Contract.Serialize(e)) <= Contract.EventLimit, "Event too large");
    }
    public static bool SecretKey(string key) => new[] { "token", "secret", "password", "cookie", "code", "authorization", "path", "rationale", "vote" }
        .Any(x => key.Contains(x, StringComparison.OrdinalIgnoreCase)) && !key.Equals("errorCode", StringComparison.OrdinalIgnoreCase);
    public static void Audit(AuditEvent e, NotificationOptions options, ProducerIdentity? producer)
    {
        Require(e.SchemaVersion == 1 && e.EventId != Guid.Empty && options.Sources.Contains(e.Source), "Invalid audit event");
        Require(e.EventType is not null && e.EventType.Length <= 100 && Dotted().IsMatch(e.EventType) && e.Outcome is "success" or "failure" or "denied", "Invalid audit type/outcome");
        Require(e.OccurredAt != default && Encoding.UTF8.GetByteCount(Contract.Serialize(e)) <= Contract.EventLimit, "Invalid audit size/time");
        if (producer is not null) Require(producer.Source == e.Source && e.Source != "jellyfin-security" && producer.Scopes.Contains("audit:write"), "Producer scope/source denied");
        if (e.Metadata is { } meta) Inspect(meta);
    }
    private static void Inspect(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var p in value.EnumerateObject()) { Require(!SecretKey(p.Name), "Unsafe audit metadata"); Inspect(p.Value); }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var child in value.EnumerateArray()) Inspect(child);
    }
}
