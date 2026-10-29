using AccountHub.Api;
using Microsoft.AspNetCore.Http;

namespace AccountHub.Notifications;

// Implement in Account Hub using Jellyfin's current-user/elevation APIs and Security gate.
// No default implementation: installing a module must never introduce an auth bypass.
public interface INotificationHost
{
    string? PublicServerUrl(HttpContext context) => null;
    IReadOnlyList<NotificationRegistration> NotificationRegistrations => [];
    bool NotificationAccessAvailable(string source) => true;
    Task<NotificationCommand[]> NotificationCommandsAsync(Guid user, NotificationEvent entry, NotificationCommandRequest? request, CancellationToken ct) => Task.FromResult<NotificationCommand[]>([]);
    Task<Guid?> CurrentUserAsync(HttpContext context, CancellationToken ct);
    Task<bool> IsAdministratorAsync(Guid userId, CancellationToken ct);
    Task<bool> UserExistsAsync(Guid userId, CancellationToken ct);
    Task<bool> CanAccessNotificationAsync(Guid userId, NotificationEvent entry, CancellationToken ct) => Task.FromResult(!entry.Type.StartsWith("library.", StringComparison.Ordinal));
    Task<IReadOnlyList<Guid>> AdministratorsAsync(CancellationToken ct);
    Task AuthorizeManagementAsync(HttpContext context, bool configurationWrite, CancellationToken ct);
    Task AuthorizeAuditAsync(HttpContext context, CancellationToken ct);
    Task<string?> DiscordLinkAsync(Guid userId, CancellationToken ct);
    // The core writer owns hashing, retention checkpoints, idempotency and export auditing.
    Task<object> AppendAuditAsync(AuditEvent entry, CancellationToken ct);
    Task<object> ExportAuditAsync(string format, CancellationToken ct);
    bool SecurityCompatible { get; }
    string SecurityAlertMode { get; }
    string? SecurityWebhookSecret { get; }
}

/// <summary>Adapter to Hub's ONE in-process bot. Must suppress mentions and validate channel permissions.</summary>
public interface IDiscordTransport
{
    Task<SendResult> SendAsync(string destination, bool directMessage, string text, string deliveryId, CancellationToken ct);
}

public sealed record SendResult(bool Delivered, bool Retryable = false, string? Error = null,
    TimeSpan? RetryAfter = null, string? MessageId = null);
public sealed record Route(string Id, string Source, string Type, string Channel, string Destination,
    bool AdminOnly = true, bool DirectMessage = false, bool AllowPrivate = false, bool AllowHttp = false,
    string? Secret = null);
public sealed record Settings(long Revision, Route[] Routes, int NotificationDays = 90)
{
    public int BlockedNotificationLimit{get;init;}=500;
}
public sealed record Preference(string Source, string Type, string Channel, bool Enabled)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]public string? Importance{get;init;}
}
public sealed record InboxRow(Guid Id, NotificationEvent Event, bool Read, bool Dismissed);
public sealed record Delivery(Guid Id, Guid UserId, bool AdminOnly, NotificationEvent Event, Route Route,
    int Attempts, string Lease, string Body, string? DiscordLink);
public sealed record ProducerIdentity(string Source, string[] Scopes);
public sealed class NotificationException(string code, string message, int status = 400) : Exception(message)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}
public sealed class NotificationOptions
{
    public required string DatabasePath { get; init; }
    public bool RequireExistingDatabase { get; init; }
    public HashSet<string> Sources { get; init; } = ["account-hub", "deletion-queue", "private-syncplay", "jellyfin-security", "discord-bridge", "notifications"];
    public HashSet<string> AllowedLinkHosts { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public int MaxAudience { get; init; } = 1000;
}
