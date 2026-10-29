using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeletionQueue.Core;

public enum NotificationMode { Auto, Local, Hub, Disabled }
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class HubSettings
{
    public long Revision { get; set; }
    public NotificationMode NotificationMode { get; set; }
    public bool HubIntegrationEnabled { get; set; } = true;
    public int HubFallbackAfterMinutes { get; set; } = 15;
    public bool PublishAudit { get; set; } = true;
    public bool DeviceBell { get; set; } = true;
}

public sealed class IntegrationEvent
{
    public Guid EventId { get; set; } = Guid.NewGuid();
    public string Kind { get; set; } = "";
    public Guid? NoticeId { get; set; }
    public string PayloadJson { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public string Status { get; set; } = "pending";
    public string? LastError { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    public Guid? LeaseId { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
}

public sealed record AdapterReceipt(Guid ActorId, string InputHash, string ResultJson);

public static class HubIntegration
{
    public static void Capture(QueueState state, IReadOnlyDictionary<Guid, long> revisions, HashSet<Guid> noticesBefore,
        string actor, string commandKind, DateTimeOffset now, bool hubAvailable, object? configurationBefore = null, object? commandSummary = null)
    {
        if (state.Schema < 2) return;
        foreach (var notice in state.Notices.Where(n => !noticesBefore.Contains(n.Id)).ToArray())
        {
            if (state.Hub.NotificationMode == NotificationMode.Disabled) { SetDelivery(state, notice.Id, "disabled"); continue; }
            if (!state.Hub.HubIntegrationEnabled || state.Hub.NotificationMode == NotificationMode.Local ||
                state.Hub.NotificationMode == NotificationMode.Auto && !hubAvailable) continue;
            var evt = new IntegrationEvent { Kind = "notification", NoticeId = notice.Id, CreatedAt = now, NextAttemptAt = now };
            var p = QueueRules.Find(state, notice.ProposalId);
            evt.PayloadJson = JsonSerializer.Serialize(new { schemaVersion = 1, eventId = evt.EventId, source = "deletion-queue",
                type = p.Status == ProposalStatus.FilesDeleted ? "proposal.seerr_removal" : ProposalType(p, !revisions.ContainsKey(p.Id)), severity = "info", title = p.Target.Title[..Math.Min(200, p.Target.Title.Length)],
                body = notice.Text, link = p.Target.JellyfinItemId != Guid.Empty ? "#/details?id=" + p.Target.JellyfinItemId.ToString("N") : "#/home?deletionQueueProposal=" + p.Id.ToString("D"), audience = new[] { notice.UserId },
                actions = new[] { new { label = "Review request", link = "#/home?deletionQueueProposal=" + p.Id.ToString("D"), size = "compact" } },
                adminOnly = false, dedupeKey = "deletionqueue:proposal:" + notice.ProposalId.ToString("D"), occurredAt = now,
                correlationId = p.Id, metadata = new { proposalId = p.Id } });
            Add(state, evt); SetDelivery(state, notice.Id, "pending");
        }
        if (!state.Hub.HubIntegrationEnabled) return;
        foreach (var p in state.Proposals.Where(p => !revisions.TryGetValue(p.Id, out var revision) || p.Revision != revision))
        {
            if(!p.Terminal&&p.Claim is null&&state.Policy.RequireAdministratorApproval&&p.Reason=="AdministratorApprovalRequired"&&!QueueRules.AdministratorApprovalCurrent(state,p)&&state.Hub.NotificationMode is NotificationMode.Auto or NotificationMode.Hub){
                var snapshot=QueueRules.AdministratorSnapshot(state,p);
                if(p.LastApprovalNoticeSnapshot!=snapshot){
                    var approval=new IntegrationEvent{Kind="notification",CreatedAt=now,NextAttemptAt=now};
                    approval.PayloadJson=JsonSerializer.Serialize(new{schemaVersion=1,eventId=approval.EventId,source="deletion-queue",type="proposal.approval_required",severity="warning",title="Removal approval requested",body="A removal request has passed eligibility checks and needs administrator approval.",audience=Array.Empty<Guid>(),adminOnly=true,occurredAt=now,
                        link="#/home?deletionQueueApprovals=1",actions=new[]{new{label="Review approvals",link="#/home?deletionQueueApprovals=1",size="normal"}},dedupeKey=$"deletionqueue:approval:{p.Id:D}",metadata=new{proposalId=p.Id}});
                    Add(state,approval);p.LastApprovalNoticeSnapshot=snapshot;
                }
            }
            else if(p.LastApprovalNoticeSnapshot is not null&&state.Hub.NotificationMode is NotificationMode.Auto or NotificationMode.Hub){
                var resolved=new IntegrationEvent{Kind="notification",CreatedAt=now,NextAttemptAt=now};
                resolved.PayloadJson=JsonSerializer.Serialize(new{schemaVersion=1,eventId=resolved.EventId,source="deletion-queue",type="proposal.approval_required",severity="info",title="Removal approval updated",body="This request is no longer awaiting administrator approval. Open the queue for its current state.",audience=Array.Empty<Guid>(),adminOnly=true,occurredAt=now,link=$"#/home?deletionQueueProposal={p.Id:D}",actions=new[]{new{label="Review approvals",link="#/home?deletionQueueApprovals=1",size="normal"}},dedupeKey=$"deletionqueue:approval:{p.Id:D}",metadata=new{proposalId=p.Id}});
                Add(state,resolved);p.LastApprovalNoticeSnapshot=null;
            }
            if (p.LastPlaybackResetAt is { } reset && p.LastPlaybackNoticeAt != reset && state.Hub.NotificationMode is NotificationMode.Auto or NotificationMode.Hub) {
                var reminder = new IntegrationEvent { Kind = "notification", CreatedAt = now, NextAttemptAt = now };
                var days = (p.DelayFloorHours / 24d).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
                reminder.PayloadJson = JsonSerializer.Serialize(new { schemaVersion = 1, eventId = reminder.EventId, source = "deletion-queue", type = "removal.playback_reset", severity = "warning",
                    title = p.Target.Title[..Math.Min(200, p.Target.Title.Length)], body = $"You played media requested for removal. The delay restarted: about {days} days, with fresh consent required.",
                    audience = new[] { p.OwnerId }, adminOnly = false, occurredAt = reset,
                    link = "#/home?deletionQueueProposal=" + p.Id.ToString("D"),
                    actions = new[] { new { label = "View request", link = "#/home?deletionQueueProposal=" + p.Id.ToString("D"), size = "compact" } },
                    dedupeKey = $"deletionqueue:playback:{p.Id:D}:{reset.UtcTicks}", metadata = new { proposalId = p.Id.ToString("D"), days } });
                Add(state, reminder); p.LastPlaybackNoticeAt = reset;
            }
            if (state.Hub.PublishAudit) Audit(state, "deletion." + ProposalType(p, !revisions.ContainsKey(p.Id)), p.Id, actor, now);
            if (p.Status == ProposalStatus.Uncertain && state.Hub.NotificationMode is NotificationMode.Auto or NotificationMode.Hub)
            {
                var alert = new IntegrationEvent { Kind = "notification", CreatedAt = now, NextAttemptAt = now };
                alert.PayloadJson = JsonSerializer.Serialize(new { schemaVersion = 1, eventId = alert.EventId, source = "deletion-queue",
                    type = "alert.removal_uncertain", severity = "critical", title = "Deletion outcome requires review",
                    body = "Check the deletion journal and Seerr before retrying this removal.",
                    link = "#/configurationpage?name=DeletionQueueAdmin", audience = Array.Empty<Guid>(), adminOnly = true,
                    actions = new[] { new { label = "Review deletion", link = "#/configurationpage?name=DeletionQueueAdmin", size = "normal" } },
                    dedupeKey = $"deletionqueue:uncertain:{p.Id:D}:{p.Revision}", occurredAt = now, correlationId = p.Id,
                    metadata = new { proposalId = p.Id } });
                Add(state, alert);
            }
        }
        if (state.Hub.PublishAudit && commandKind is ("AdministratorPolicyChanged" or "UserPolicyAndPreferencesReset" or "AllUserPermissionsReset" or "SeerrConfigurationChanged" or
            "EnhancedSeerrConnectionImported" or "AccountHubAdapterChanged" or "AccountHubPolicyReset"))
            Audit(state, commandKind is "UserPolicyAndPreferencesReset" or "AllUserPermissionsReset" or "AccountHubPolicyReset" ? "deletion.policy.reset" : "deletion.policy.changed", null, actor, now,
                new { before = configurationBefore, after = ConfigurationSummary(state), summary = commandSummary });
    }

    public static object ConfigurationSummary(QueueState s) => new { defaultAuthority = s.Policy.DefaultAuthority.ToString(),
        activityDays = s.Policy.ActivityDays, delayHours = s.Policy.DelayHours,administratorApprovalDays=s.Policy.AdministratorApprovalDays,recentHistoryDays=s.Policy.RecentHistoryDays, automaticDeletion = s.Policy.AutomaticDeletion,
        activityWaitTimeoutEnabled = s.Policy.ActivityWaitTimeoutEnabled, activityWaitTimeoutDays = s.Policy.ActivityWaitTimeoutDays,
        requireAdministratorApproval = s.Policy.RequireAdministratorApproval,
        seerrManagedDeletionAcknowledged = s.Policy.SeerrManagedDeletionAcknowledged, suspended = s.Policy.Suspended, accountOverrideCount = s.Policy.Accounts.Count,
        preferencesCount = s.Preferences.Count, connectionRevision = s.Seerr.Revision,
        notificationMode = s.Hub.NotificationMode.ToString(), hubIntegrationEnabled = s.Hub.HubIntegrationEnabled,
        publishAudit = s.Hub.PublishAudit, deviceBell = s.Hub.DeviceBell, hubFallbackAfterMinutes = s.Hub.HubFallbackAfterMinutes };

    private static string ProposalType(Proposal p, bool created) => created ? "proposal.created" : p.Status switch
    { ProposalStatus.Cancelled => "proposal.cancelled", ProposalStatus.Uncertain => "proposal.uncertain",
      ProposalStatus.Completed => "proposal.completed", ProposalStatus.FilesDeleted => "seerr.removal", _ => "proposal.updated" };

    private static void Audit(QueueState state, string type, Guid? subject, string actor, DateTimeOffset now, object? metadata = null)
    {
        var evt = new IntegrationEvent { Kind = "audit", CreatedAt = now, NextAttemptAt = now };
        evt.PayloadJson = JsonSerializer.Serialize(new { schemaVersion = 1, eventId = evt.EventId, source = "deletion-queue", eventType = type,
            actorUserId = Guid.TryParse(actor, out var id) ? (Guid?)id : null, subjectType = subject.HasValue ? "proposal" : "config",
            subjectId = subject?.ToString("D"), outcome = "success", occurredAt = now, correlationId = subject ?? evt.EventId, metadata = metadata ?? new { } });
        Add(state, evt);
    }

    private static void Add(QueueState state, IntegrationEvent evt)
    {
        // Only terminal delivery metadata is pruned; original authenticated journal events remain retained.
        state.Outbox.RemoveAll(e => e.DeliveredAt < evt.CreatedAt.AddDays(-30));
        // Bounded terminal delivery metadata may roll out earlier under pressure. Source events remain in the
        // authenticated journal; the notice's delivery ownership remains durable and is never backfilled.
        if (state.Outbox.Count >= 2000)
        {
            var terminal = state.Outbox.Where(e => e.DeliveredAt.HasValue).OrderBy(e => e.DeliveredAt).FirstOrDefault();
            if (terminal is not null) { state.Outbox.Remove(terminal); state.HubDeliveryMetadataRolledOff++; }
        }
        if (state.Outbox.Count >= 2000) throw new QueueException("IntegrationCapacityReached");
        state.Outbox.Add(evt);
    }

    public static void SetDelivery(QueueState state, Guid id, string delivery)
    {
        var n = state.Notices.FindIndex(n => n.Id == id);
        if (n >= 0) state.Notices[n] = state.Notices[n] with { Delivery = delivery };
    }

    public static void Complete(QueueState state, Guid eventId, Guid lease, string result, DateTimeOffset now)
    {
        var e = state.Outbox.Single(e => e.EventId == eventId);
        if (e.Status != "delivering" || e.LeaseId != lease) return;
        e.LeaseUntil = null; e.LeaseId = null; e.Attempts++;
        if (result == "delivered")
        { e.Status = "delivered"; e.DeliveredAt = now; e.LastError = null; if (e.NoticeId is { } notice) SetDelivery(state, notice, "hub"); return; }
        e.LastError = result == "rejected" ? "rejected" : "unavailable";
        if (result == "rejected" || e.Attempts >= 8 || e.Kind == "notification" && now >= e.CreatedAt.AddMinutes(state.Hub.HubFallbackAfterMinutes))
        {
            e.Status = e.Kind == "notification" ? "local_fallback" : "dead_letter"; e.DeliveredAt = now;
            if (e.NoticeId is { } notice) SetDelivery(state, notice, state.Hub.NotificationMode == NotificationMode.Disabled ? "disabled" : "local");
            return;
        }
        e.Status = "pending";
        var minutes = e.Attempts switch { 1 => 1, 2 => 5, 3 => 15, 4 => 60, _ => 360 };
        e.NextAttemptAt = now.AddMinutes(minutes);
        var deadline = e.CreatedAt.AddMinutes(state.Hub.HubFallbackAfterMinutes);
        if (e.Kind == "notification" && e.NextAttemptAt > deadline) e.NextAttemptAt = deadline;
    }
}
