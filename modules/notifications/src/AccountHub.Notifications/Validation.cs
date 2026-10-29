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
    public static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool condition, string reason)
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
    public static void Event(NotificationEvent e, NotificationOptions options, ProducerIdentity? producer, bool registeredSource = false)
    {
        Require(e.SchemaVersion == 1 && e.EventId != Guid.Empty, "Invalid event version or ID");
        Require(e.Source is not null && (options.Sources.Contains(e.Source) || registeredSource), "Unknown source");
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
        Require(e.Details is null || e.Details.Length <= 8000, "Notification details too long");
        Require(e.Commands is null, "Commands must be projected by the internal provider");
        Require(e.FirstViewedAt is null,"Viewed timestamps are recipient-owned");
        Require(e.Importance is null&&!e.Retroactive&&e.Group is null,"Notification presentation state is server-owned");
        if (e.Layout is not null) Layout(e.Layout);
        Require(e.Actions is null || e.Actions.Length <= 5 && e.Actions.All(a => a is not null && a.Label is { Length: > 0 and <= 60 } &&
            a.Link is not null && SafeLink(a.Link, options) && a.Size is "compact" or "normal"), "Invalid notification actions");
        Require(Encoding.UTF8.GetByteCount(Contract.Serialize(e)) <= Contract.EventLimit, "Event too large");
    }
    public static void Layout(NotificationLayout layout) {
        Require(layout is not null && layout.Density is "compact" or "standard" && layout.SummaryLength is >= 60 and <= 300 && layout.ToastSeconds is >= 0 and <= 10 && layout.MessageTemplate?.Length is not > 400 && (layout.ExpireAfterViewHours is null or >=1 and <=8760), "Invalid notification layout");
        if (layout.Canvas is not { } canvas) return;
        Require(canvas.Height is >= 100 and <= 500 && canvas.Layers is { Length: > 0 and <= 12 }, "Invalid notification canvas");
        Require(canvas.Layers.Select(layer => layer?.Id).Distinct().Count() == canvas.Layers.Length, "Duplicate canvas layer");
        foreach (var layer in canvas.Layers) Require(layer is not null && layer.Id is { Length: > 0 and <= 40 } && layer.Id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') && layer.Kind is "title" or "body" or "text" or "image"
            && new[] { layer.X, layer.Y, layer.Width, layer.Height }.All(double.IsFinite) && layer.X is >= 0 and <= 99 && layer.Y is >= 0 and <= 99 && layer.Width is >= 1 and <= 100 && layer.Height is >= 1 and <= 100 && layer.X + layer.Width <= 100 && layer.Y + layer.Height <= 100
            && layer.Text is { Length: <= 400 } && layer.FontSize is >= 10 and <= 48 && layer.FontWeight is 400 or 500 or 600 or 700 or 800 && layer.FontFamily is "inherit" or "sans-serif" or "serif" or "monospace" && layer.Align is "start" or "center" or "end" && layer.Tone is "inherit" or "muted" or "accent" && layer.Fit is "cover" or "contain", "Invalid notification layer");
    }
    public static void Defaults(NotificationDefaults value) => Require(value is not null && value.Importance is "silent" or "low" or "normal" or "high", "Invalid notification defaults");
    public static void Registration(NotificationRegistration registration, NotificationOptions options)
    {
        Require(registration is not null && registration.Source is { Length: > 0 and <= 100 } && registration.Source.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '_' or '.') && registration.Version is { Length: > 0 and <= 50 } && registration.Types is { Length: > 0 and <= 100 }, "Invalid producer registration");
        Require(registration.Types.Select(type => type?.Type).Distinct().Count() == registration.Types.Length, "Duplicate notification type");
        if (registration.Grouping is { } grouping) {
            bool SafeKey(string? key) => key is { Length: > 0 and <= 80 } && key.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-') && !SecretKey(key);
            Require(SafeKey(grouping.MetadataKey) && (grouping.LabelKey is null || SafeKey(grouping.LabelKey)) && grouping.Name is { Length: > 0 and <= 100 }, "Invalid producer grouping");
        }
        foreach (var type in registration.Types) {
            Require(type is not null && type.Type is { Length: > 0 and <= 100 } && (type.Type == "*" || Dotted().IsMatch(type.Type.EndsWith(".*", StringComparison.Ordinal) ? type.Type[..^1] + "event" : type.Type)), "Invalid notification type");
            Require(type.Name is { Length: > 0 and <= 100 } && type.Trigger is { Length: > 0 and <= 200 } && type.Recipients is "explicit-users" or "administrators" or "event", "Invalid type description or recipients");
            Layout(type.Layout);
            if (type.Defaults is { } defaults) Defaults(defaults);
            if (type.Example is { } example) Require(example.Title is { Length: > 0 and <= 200 } && example.Body is { Length: <= 1000 } && example.Severity is "info" or "warning" or "high" or "critical", "Invalid simulated notification");
            Require(type.Actions is not null && type.Actions.Length <= 5 && type.Actions.All(action => action is not null && action.Label is { Length: > 0 and <= 60 } && action.Size is "compact" or "normal"), "Invalid registered actions");
            Require(type.Actions.Select(action => action.Label).Distinct().Count() == type.Actions.Length, "Duplicate registered action");
            Require(type.Commands is { Length: <= 5 } && type.Commands.All(command => command is { Length: > 0 and <= 50 } && command.All(c => char.IsAsciiLetterLower(c) || c == '-')) && type.Commands.Distinct().Count() == type.Commands.Length, "Invalid registered commands");
        }
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
