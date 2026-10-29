using System.Text.Json.Serialization;

namespace DeletionQueue.Core;

public enum ProposalAuthority { Denied, OwnRequests, AnyAccessibleItem }
public enum MediaKind { Movie, Series }
public enum ProposalStatus { Blocked, Waiting, Ready, Claimed, Uncertain, FilesDeleted, Completed, Cancelled, Expired }
public enum Answer { Yes, No }

public sealed class QueueException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

public sealed record Account(Guid Id, bool Enabled, bool Administrator = false);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class Policy
{
    public long Revision { get; set; }
    public ProposalAuthority DefaultAuthority { get; set; } = ProposalAuthority.OwnRequests;
    public Dictionary<Guid, ProposalAuthority> Accounts { get; set; } = [];
    public int ActivityDays { get; set; } = 14;
    public int DelayHours { get; set; } = 48;
    public int AdministratorApprovalDays { get; set; } = 30;
    public bool ActivityWaitTimeoutEnabled { get; set; }
    public int ActivityWaitTimeoutDays { get; set; } = 45;
    public int RecentHistoryDays { get; set; } = 7;
    public bool AutomaticDeletion { get; set; }
    public bool RequireAdministratorApproval { get; set; }
    // Absent in older previews: an old preview-only opt-in must never silently enable a new destructive transport.
    public bool SeerrManagedDeletionAcknowledged { get; set; }
    public bool Suspended { get; set; }
}

public sealed class Preferences
{
    public int RecentHistoryDays { get; set; }
    public int ActivityDays { get; set; }
    public int AdministratorApprovalDays { get; set; }
    public long Revision { get; set; }
    public int DelayHours { get; set; } = 48;
    public bool Notifications { get; set; } = true;
    public HashSet<Guid> Subscriptions { get; set; } = [];
}

// Constructed by a trusted adapter, never bound from an HTTP request body.
public sealed record VerifiedTarget(
    string Key, string ActivityKey, string Fingerprint, Guid JellyfinItemId,
    string Title, MediaKind Kind, int SeerrMediaId, bool Is4K,
    bool MappingVerified, bool OwnRequestVerified, bool FilesPinned, long ConfigurationRevision = 0,
    string? DestructiveScopeKey = null)
{
    public int? RequestId { get; init; }
    public Guid? RequestOwnerId { get; init; }
    public bool RequestOnly { get; init; }
}

public sealed class Activity
{
    public Guid UserId { get; set; }
    public string Key { get; set; } = "";
    public long Revision { get; set; }
    public DateTimeOffset At { get; set; }
    public HashSet<string> Sessions { get; set; } = [];
}

public sealed record Consent(Answer Answer, string Snapshot, DateTimeOffset At);
public sealed record AdministratorApproval(Guid UserId, string Snapshot, DateTimeOffset At);
public sealed record ExecutionClaim(Guid Id, DateTimeOffset At, string TargetFingerprint, long ProposalRevision)
{
    public long? ActivityGeneration { get; init; }
}
// Trusted, read-only upstream observation; never a request-body deletion grant.
public sealed record ExternalTargetChange(string Reason);

public sealed class Proposal
{
    public bool RequiresAutomaticRuleValidation { get; set; }
    public string? AutomaticRuleId { get; set; }
    public Guid? AutomaticAuthorizerId { get; set; }
    public Guid? RequesterId { get; set; }
    public Guid? AdministratorRequestedBy { get; set; }
    public bool CreatorAutoApprovalDisabled { get; set; }
    public string? LastApprovalNoticeSnapshot { get; set; }
    public bool Inactive { get; set; }
    public DateTimeOffset? ActivityVerifiedAt { get; set; }
    public DateTimeOffset? LastCheckedAt { get; set; }
    public EvaluationEvidence? LastCheckEvidence { get; set; }
    public DateTimeOffset? ApprovalWaitingSince { get; set; }
    public DateTimeOffset? ApprovalDeadline { get; set; }
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OwnerId { get; set; }
    public VerifiedTarget Target { get; set; } = null!;
    public long Revision { get; set; } = 1;
    public ProposalStatus Status { get; set; } = ProposalStatus.Blocked;
    public string Reason { get; set; } = "VerificationRequired";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastPlaybackResetAt { get; set; }
    public DateTimeOffset? LastPlaybackNoticeAt { get; set; }
    public DateTimeOffset? EligibleSince { get; set; }
    public DateTimeOffset? NotBefore { get; set; }
    public int DelayFloorHours { get; set; } = 48;
    public Dictionary<Guid, Consent> Consents { get; set; } = [];
    public AdministratorApproval? AdministratorApproval { get; set; }
    public ExecutionClaim? Claim { get; set; }
    public DateTimeOffset? FilesDeletedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public string? ClosedCheckpoint { get; set; }
    public DateTimeOffset? ClosedChecksAt { get; set; }
    public string? DeletionEvidence { get; set; }
    public string? CleanupOutcome { get; set; }
    public bool Terminal => Status is ProposalStatus.Completed or ProposalStatus.Cancelled or ProposalStatus.Expired;
}

public sealed class QueueState
{
    public MediaPresentationState Presentation { get; set; } = new();
    public int Schema { get; set; } = 1;
    public HubSettings Hub { get; set; } = new();
    public List<IntegrationEvent> Outbox { get; set; } = [];
    public long HubDeliveryMetadataRolledOff { get; set; }
    public Dictionary<Guid, AdapterReceipt> AdapterReceipts { get; set; } = [];
    public Policy Policy { get; set; } = new();
    public SeerrSettings Seerr { get; set; } = new();
    public Dictionary<Guid, Preferences> Preferences { get; set; } = [];
    public List<Proposal> Proposals { get; set; } = [];
    public List<Activity> Activity { get; set; } = [];
    public List<Notice> Notices { get; set; } = [];
    public bool NoticeDeliveryIncomplete { get; set; }
    public DateTimeOffset? CoverageSince { get; set; }
    public DateTimeOffset? LastHeartbeat { get; set; }
    public bool CoverageHealthy { get; set; }
    public bool ClockHealthy { get; set; } = true;
    public string CoverageProblem { get; set; } = "CollectorNotStarted";
}

public sealed class SeerrSettings
{
    public long Revision { get; set; }
    public string BaseUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string JellyfinServerId { get; set; } = "";
}

public sealed record Notice(Guid Id, Guid UserId, Guid ProposalId, string Text, DateTimeOffset? ReadAt = null, string Delivery = "local");
public sealed record JournalEntry(long Sequence, Guid CommandId, string Actor, string Kind, DateTimeOffset At);

public sealed record EvaluationEvidence(
    bool OwnerAuthorized, bool TargetVerified, bool CompleteActivityScan,
    bool LocalCheckpointVerified, bool SeerrDelegationVerified, bool AdministratorAuthorized = false)
{
    public bool AutomaticRuleVerified { get; init; }
    public bool NativeHistoryVerified { get; init; }
    public bool AdministratorCreatorAuthorized { get; init; }
}

// Deliberately does not contain another user's identity, vote, activity, paths or request IDs.
public sealed record UserProposal(
    Guid Id, string Title, string Ownership, string Status, string Reason,
    long Revision, DateTimeOffset? NotBefore, bool CanUndo,
    string OwnSnapshot, Answer? OwnAnswer, DateTimeOffset? OwnActivityAt, string RequiredAction)
{
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public string Stage { get; init; } = "Consent";
    public string StatusLabel { get; init; } = "Pending checks";
    public string StatusHelp { get; init; } = "Removal is waiting for safety checks.";
    public DateTimeOffset? ScheduledFor { get; init; }
    public RemovalCheckpoint[] Timeline { get; init; } = [];
    public DateTimeOffset EvaluatedAt { get; init; }
    public DateTimeOffset? LastCheckedAt { get; init; }
    public string CheckRefreshHelp { get; init; } = "";
    public int ActivityWindowDays { get; init; }
    public int SafetyDelayHours { get; init; }
    public string? ClosedCheckpoint { get; init; }
    public string? ClosureExplanation { get; init; }
    public string MediaKind { get; init; } = "Movie";
    public bool CanConsent { get; init; }
    public string ConsentStatus { get; init; } = "Checking";
    public string ActivityStatus { get; init; } = "Checking";
    public string RequesterLabel { get; init; } = "Another requester";
    public bool CanApprove { get; init; }
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]public string? AdministratorApprovalSnapshot { get; init; }
}
public sealed record RemovalCheckpoint(string Key, string Label, string State, DateTimeOffset? At = null)
{
    public string? DateLabel { get; init; }
    public string? Decision { get; init; }
    public RemovalCheckpoint[] Paths { get; init; } = [];
    public double? DurationDays { get; init; }
    public int? DurationHours { get; init; }
}

public interface IStateStore
{
    QueueState Read();
    T Write<T>(Guid commandId, string actor, string kind, Func<QueueState, T> command);
}
