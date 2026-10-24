using System.Text.Json;

namespace AccountHub.Core.Seerr;

public sealed record SeerrQuota(int? Limit, int? Days)
{
    public void Validate()
    {
        if (Limit is < 0 or > 100000 || Days is < 1 or > 365 || Limit.HasValue != Days.HasValue)
            throw new HubException("invalid_request", "Quota requires a limit (0 means unlimited) and 1–365 days, or both null to inherit");
    }
}
public sealed record SeerrDesired(long Permissions, SeerrQuota MovieQuota, SeerrQuota TvQuota);
public sealed record SeerrAccount(int Id, Guid? NativeId, long Permissions, string CreatedAt, string? Username = null);
public sealed record SeerrCapabilities(string Fingerprint, string Version, long DefaultPermissions, SeerrQuota MovieQuota, SeerrQuota TvQuota, string Source);
public sealed record SeerrNativeUser(Guid Id, string Username, bool Administrator, bool Disabled);
public interface ISeerrDirectory { SeerrNativeUser? Find(Guid id); }
public interface ISeerrSession
{
    SeerrCapabilities Capabilities { get; }
    Task<SeerrAccount?> FindNativeAsync(Guid id, CancellationToken ct);
    Task<SeerrAccount?> FindAsync(int id, CancellationToken ct);
    Task ImportAsync(Guid id, CancellationToken ct);
    Task<SeerrDesired> ReadDesiredAsync(int id, CancellationToken ct);
    Task ApplyAsync(int id, Guid nativeId, string createdAt, SeerrDesired desired, CancellationToken ct);
    Task DeleteAsync(int id, Guid nativeId, string createdAt, CancellationToken ct);
    Task<JsonElement> RequestsAsync(int id, int skip, int limit, CancellationToken ct);
}
public interface ISeerrUpstream { Task<ISeerrSession> OpenAsync(CancellationToken ct); }
public sealed record SeerrLink(Guid UserId, int SeerrId, string Fingerprint, string CreatedAt, SeerrDesired? Desired, long Revision = 1);
public sealed record SeerrJob
{
    public Guid Id { get; init; }
    public Guid UserId { get; init; }
    public Guid? ActorId { get; init; }
    public string Kind { get; init; } = "reconcile";
    public string State { get; set; } = "pending";
    public string Fingerprint { get; init; } = "";
    public int? SeerrId { get; set; }
    public string? AccountCreatedAt { get; set; }
    public SeerrDesired? Desired { get; init; }
    public bool ImportAttempted { get; set; }
    public int Attempts { get; set; }
    public long Due { get; set; }
    public long Updated { get; set; }
    public string? Error { get; set; }
    public bool AuditPending { get; set; }
    public bool Active => State is "pending" or "running" or "awaiting_native_delete";
}
public sealed class SeerrState
{
    public SeerrCapabilities? Capabilities { get; set; }
    public List<SeerrLink> Links { get; set; } = [];
    public List<SeerrJob> Jobs { get; set; } = [];
    public List<SeerrAuditEntry> AuditOutbox { get; set; } = [];
}
public sealed record SeerrAuditEntry(Guid JobId, SeerrJobEvent Event);
public sealed record SeerrJobEvent(Guid EventId, Guid? ActorId, string Type, string State, string? Error, long OccurredAt);
public interface ISeerrEvents { Task PublishAsync(SeerrJobEvent entry, CancellationToken ct); }
