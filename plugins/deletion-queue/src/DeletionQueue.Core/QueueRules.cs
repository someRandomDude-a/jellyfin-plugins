using System.Security.Cryptography;
using System.Text;

namespace DeletionQueue.Core;

public static class QueueRules
{
    public static ProposalAuthority Authority(QueueState state, Guid user) =>
        state.Policy.Accounts.GetValueOrDefault(user, state.Policy.DefaultAuthority);

    public static void RequireProposalAuthority(QueueState state, Account caller, VerifiedTarget target, bool accessible)
    {
        if (!caller.Enabled || !accessible || !target.MappingVerified || !Enum.IsDefined(target.Kind) ||
            target.SeerrMediaId <= 0 || target.JellyfinItemId == Guid.Empty && !(target.RequestId > 0 && target.RequestOwnerId == caller.Id && target.OwnRequestVerified) || string.IsNullOrWhiteSpace(target.Key) ||
            string.IsNullOrWhiteSpace(target.Fingerprint) || string.IsNullOrWhiteSpace(target.ActivityKey))
            throw new QueueException("TargetUnavailable");
        var authority = Authority(state, caller.Id);
        if (authority == ProposalAuthority.Denied ||
            (authority == ProposalAuthority.OwnRequests && !target.OwnRequestVerified))
            throw new QueueException("ProposalNotPermitted");
    }

    public static Proposal Propose(QueueState state, Account caller, VerifiedTarget target,
        bool accessible, string expectedSnapshot, DateTimeOffset now)
    {
        RequireProposalAuthority(state, caller, target, accessible);
        foreach (var existing in state.Proposals) Expire(state, existing, now);
        var proposal = state.Proposals.SingleOrDefault(p => !p.Terminal && (p.Target.Key == target.Key ||
            (target.DestructiveScopeKey is not null && p.Target.DestructiveScopeKey == target.DestructiveScopeKey)));
        if (proposal?.Claim is not null) throw new QueueException("AlreadyClaimed");
        if (proposal is not null && proposal.Target.Fingerprint != target.Fingerprint)
            throw new QueueException("TargetChanged");
        if (Snapshot(state, target, caller.Id) != expectedSnapshot) throw new QueueException("SnapshotChanged");
        if (proposal is not null && proposal.Consents.TryGetValue(caller.Id, out var existingConsent) && existingConsent.Answer == Answer.Yes && existingConsent.Snapshot == expectedSnapshot &&
            (!caller.Administrator || !state.Policy.RequireAdministratorApproval || AdministratorApprovalCurrent(state, proposal))) return proposal;
        if (proposal is null)
        {
            if (state.Proposals.Count >= 1000) throw new QueueException("ProposalCapacityReached");
            proposal = new Proposal { OwnerId = caller.Id, Target = target, CreatedAt = now,AdministratorRequestedBy=caller.Administrator?caller.Id:null };
            state.Proposals.Add(proposal);
        }
        if (proposal.Consents.Count >= 1000 && !proposal.Consents.ContainsKey(caller.Id)) throw new QueueException("ConsentCapacityReached");
        proposal.Consents[caller.Id] = new Consent(Answer.Yes, expectedSnapshot, now);
        if (caller.Administrator && state.Policy.RequireAdministratorApproval)
            proposal.AdministratorApproval = new(caller.Id, AdministratorSnapshot(state, proposal), now);
        ExtendDelay(state, proposal, caller.Id);
        ExtendDeadline(proposal);
        proposal.Revision++;
        return proposal;
    }

    public static Proposal ProposeAutomatically(QueueState state, Account actor, VerifiedTarget target, DateTimeOffset now)
    {
        RequireProposalAuthority(state, actor, target, true);
        foreach (var p in state.Proposals) Expire(state, p, now);
        if (state.Proposals.Any(p => p.Target.Key == target.Key && (p.Status == ProposalStatus.Cancelled || p.Reason == "AdministratorDenied")))
            throw new QueueException("PriorRemovalDeclined");
        var existing = state.Proposals.SingleOrDefault(p => !p.Terminal && (p.Target.Key == target.Key ||
            target.DestructiveScopeKey is not null && p.Target.DestructiveScopeKey == target.DestructiveScopeKey));
        if (existing is not null) {
            if (existing.Target.Fingerprint != target.Fingerprint) throw new QueueException("TargetChanged");
            return existing; // Matching again is not a user's consent, approval or renewed expiry.
        }
        var created = Propose(state, actor with { Administrator = false }, target, true, Snapshot(state, target, actor.Id), now);
        created.Consents.Remove(actor.Id); // An administrator enabling automation has not watched or voted.
        created.AdministratorApproval = null;
        created.RequiresAutomaticRuleValidation = true;
        return created;
    }

    public static void Consent(QueueState state, Guid id, Account caller, long revision,
        string snapshot, Answer answer, bool accessible, DateTimeOffset now)
    {
        if (!caller.Enabled || !accessible) throw new QueueException("TargetUnavailable");
        var proposal = Find(state, id);
        Expire(state, proposal, now);
        RequireMutable(proposal, revision);
        if (Snapshot(state, proposal.Target, caller.Id) != snapshot) throw new QueueException("SnapshotChanged");
        if (proposal.Consents.Count >= 1000 && !proposal.Consents.ContainsKey(caller.Id)) throw new QueueException("ConsentCapacityReached");
        proposal.Consents[caller.Id] = new Consent(answer, snapshot, now);
        ExtendDelay(state, proposal, caller.Id);
        // Conservatively break the epoch on No even before the next evaluator pass.
        if (answer == Answer.No && IsRecent(state, proposal.Target, caller.Id, now)) Block(proposal, "ConsentRequired");
        proposal.Revision++;
    }

    public static void Undo(QueueState state, Guid id, Guid caller, long revision)
        => Undo(state, id, caller, revision, null);
    public static void Undo(QueueState state, Guid id, Guid caller, long revision, DateTimeOffset? now)
    {
        var proposal = Find(state, id);
        if (proposal.OwnerId != caller) throw new QueueException("OwnerOnly");
        RequireMutable(proposal, revision);
        proposal.ClosedCheckpoint = UserProgress.Checkpoint(proposal);
        proposal.ClosedChecksAt = proposal.EligibleSince;
        proposal.Status = ProposalStatus.Cancelled;
        proposal.Reason = "Cancelled";
        proposal.EligibleSince = null;
        proposal.ClosedAt = now;
        proposal.Revision++;
    }

    public static string AdministratorSnapshot(QueueState state, Proposal proposal)
    {
        var activity = string.Join('|', state.Activity.Where(a => a.Key == proposal.Target.ActivityKey)
            .OrderBy(a => a.UserId).Select(a => $"{a.UserId:D}:{a.Revision}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{proposal.Id:D}|{proposal.Target.Fingerprint}|{state.Policy.Revision}|{activity}")));
    }

    public static bool AdministratorApprovalCurrent(QueueState state, Proposal proposal) =>
        proposal.AdministratorApproval is { } approval && approval.Snapshot == AdministratorSnapshot(state, proposal);

    public static void AdministratorDecision(QueueState state, Guid id, Account caller, long revision,
        string snapshot, bool approved, bool accessible, DateTimeOffset now)
    {
        if (!caller.Enabled || !caller.Administrator || !accessible) throw new QueueException("AccessDenied");
        if (!state.Policy.RequireAdministratorApproval) throw new QueueException("AdministratorApprovalNotRequired");
        var p = Find(state, id);
        Expire(state, p, now);
        RequireMutable(p, revision);
        if (snapshot != AdministratorSnapshot(state, p)) throw new QueueException("SnapshotChanged");
        p.AdministratorApproval = approved ? new(caller.Id, snapshot, now) : null;
        if(!approved)p.CreatorAutoApprovalDisabled=true;
        Block(p, approved ? "VerificationRequired" : "AdministratorApprovalRequired");
        p.Revision++;
    }
    public static void Deny(QueueState state, Guid id, Account caller, long revision, string snapshot, bool accessible, DateTimeOffset now)
    {
        if (!caller.Enabled || !caller.Administrator || !accessible) throw new QueueException("AccessDenied");
        var p = Find(state, id); Expire(state, p, now); RequireMutable(p, revision);
        if (snapshot != AdministratorSnapshot(state, p)) throw new QueueException("SnapshotChanged");
        p.ClosedCheckpoint = UserProgress.Checkpoint(p);
        p.ClosedChecksAt = p.EligibleSince;
        p.AdministratorApproval = null; p.Status = ProposalStatus.Expired; p.Reason = "AdministratorDenied"; p.EligibleSince = null; p.Revision++;
        p.ClosedAt = now;
    }

    public static void RecordActivity(QueueState state, Guid user, string key, string? session,
        bool stopped, DateTimeOffset now)
    {
        var activity = state.Activity.SingleOrDefault(a => a.UserId == user && a.Key == key);
        if (activity is null)
        {
            if (state.Activity.Count >= 10000) throw new QueueException("ActivityCapacityReached");
            activity = new Activity { UserId = user, Key = key };
            state.Activity.Add(activity);
        }
        activity.Revision++;
        activity.At = now;
        var newPlayback = !string.IsNullOrEmpty(session) && !stopped && !activity.Sessions.Contains(session);
        if (!string.IsNullOrEmpty(session))
        {
            if (stopped) activity.Sessions.Remove(session);
            else { if (activity.Sessions.Count >= 1024 && !activity.Sessions.Contains(session)) throw new QueueException("SessionCapacityReached"); activity.Sessions.Add(session); }
        }
        foreach (var p in state.Proposals.Where(p => !p.Terminal && p.Claim is null && p.Target.ActivityKey == key))
        {
            Expire(state, p, now); if (p.Terminal) continue;
            Block(p, "ConsentRequired");
            if (newPlayback && p.OwnerId == user) {
                ExtendDelay(state, p, user);
                p.LastPlaybackResetAt = now;
                var deadline = now.AddHours(p.DelayFloorHours);
                if (p.NotBefore is null || deadline > p.NotBefore) p.NotBefore = deadline;
            }
            p.Revision++;
        }
    }

    public static string Snapshot(QueueState state, VerifiedTarget target, Guid user)
    {
        var activity = state.Activity.SingleOrDefault(a => a.UserId == user && a.Key == target.ActivityKey);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{target.Fingerprint}|{user:D}|{activity?.Revision ?? 0}")));
    }

    public static void Evaluate(QueueState state, Proposal proposal, EvaluationEvidence evidence, DateTimeOffset now)
    {
        Expire(state, proposal, now);
        if (proposal.Terminal || proposal.Claim is not null) return;
        proposal.LastCheckedAt = now;
        proposal.LastCheckEvidence = evidence;
        var reason = !state.ClockHealthy ? "ClockUnverified"
            : state.Policy.Suspended ? "Suspended"
            : !evidence.OwnerAuthorized ? "OwnerNotAuthorized"
            : !evidence.TargetVerified ? "TargetVerificationRequired"
            : proposal.RequiresAutomaticRuleValidation && !evidence.AutomaticRuleVerified ? "AutomaticRuleVerificationRequired"
            : (!CoverageComplete(state, now) && !evidence.NativeHistoryVerified) || !evidence.CompleteActivityScan ? "ActivityCoverageIncomplete"
            : !evidence.LocalCheckpointVerified ? "LocalCheckpointRequired"
            : !evidence.SeerrDelegationVerified ? "SeerrDelegationRequired" : null;
        var viewers = state.Activity.Where(a => a.Key == proposal.Target.ActivityKey &&
            (a.At >= now.AddDays(-ActivityDays(state, a.UserId)) || a.Sessions.Count > 0)).ToList();
        foreach (var viewer in viewers) ExtendDelay(state, proposal, viewer.UserId);
        if (reason is null) { proposal.ActivityVerifiedAt ??= now; proposal.Inactive = viewers.Count == 0; }
        foreach (var participant in proposal.Consents.Keys) ExtendDelay(state, proposal, participant);
        proposal.DelayFloorHours = Math.Max(proposal.DelayFloorHours, state.Policy.DelayHours);
        reason ??= viewers.Any(v => v.Sessions.Count > 0) ? "PlaybackActive" : null;
        reason ??= viewers.Any(v => !proposal.Consents.TryGetValue(v.UserId, out var consent) ||
            consent.Answer != Answer.Yes || consent.Snapshot != Snapshot(state, proposal.Target, v.UserId))
             ? "ConsentRequired" : null;
        if(reason is null&&state.Policy.RequireAdministratorApproval&&!proposal.CreatorAutoApprovalDisabled&&proposal.AdministratorRequestedBy==proposal.OwnerId&&!proposal.RequiresAutomaticRuleValidation&&evidence.AdministratorCreatorAuthorized&&!AdministratorApprovalCurrent(state,proposal))
            proposal.AdministratorApproval=new(proposal.OwnerId,AdministratorSnapshot(state,proposal),now);
        reason ??= state.Policy.RequireAdministratorApproval &&
            (!AdministratorApprovalCurrent(state, proposal) || !evidence.AdministratorAuthorized)
            ? "AdministratorApprovalRequired" : null;
        if (reason == "AdministratorApprovalRequired") {
            // Older saved requests lack the review-start milestone. Preserve
            // their original expiry anchor rather than renew the clock on upgrade.
            proposal.ApprovalWaitingSince ??= proposal.Reason == "AdministratorApprovalRequired" ? proposal.LastPlaybackResetAt ?? proposal.CreatedAt : now;
            ExtendApprovalDeadline(state, proposal);
        }
        if (reason is not null) { Block(proposal, reason); return; }
        proposal.EligibleSince ??= now;
        var deadline = proposal.EligibleSince.Value.AddHours(proposal.DelayFloorHours);
        if (proposal.NotBefore is null || deadline > proposal.NotBefore) proposal.NotBefore = deadline;
        proposal.Status = now >= proposal.NotBefore ? ProposalStatus.Ready : ProposalStatus.Waiting;
        proposal.Reason = proposal.Status == ProposalStatus.Ready ? "Ready" : "DelayRequired";
    }
    internal static bool AdministratorReviewWaiting(QueueState state,Proposal proposal)=>state.Policy.RequireAdministratorApproval && proposal.ApprovalDeadline is not null &&
        (proposal.AdministratorApproval is null || !AdministratorApprovalCurrent(state,proposal) || proposal.Reason=="AdministratorApprovalRequired");
    public static DateTimeOffset? ExpirationDeadline(QueueState state, Proposal proposal) =>
        proposal.Status != ProposalStatus.Blocked ? null :
        AdministratorReviewWaiting(state,proposal) ? proposal.ApprovalDeadline :
        state.Policy.ActivityWaitTimeoutEnabled ? proposal.CreatedAt.AddDays(state.Policy.ActivityWaitTimeoutDays) : null;

    public static void Expire(QueueState state, Proposal proposal, DateTimeOffset now)
    {
        var deadline = ExpirationDeadline(state,proposal);
        if (deadline is {} expires && proposal.Claim is null && proposal.FilesDeletedAt is null && now >= expires) {
            var approval = AdministratorReviewWaiting(state,proposal);
            proposal.ClosedCheckpoint = approval ? "approval" : "activity-timeout";
            proposal.Status = ProposalStatus.Expired;
            proposal.Reason = approval ? "AdministratorApprovalExpired" : "ActivityWaitExpired";
            proposal.EligibleSince = null;
            proposal.ClosedAt = expires;
            proposal.Revision++;
        }
    }

    public static void VerificationUnavailable(QueueState state, Proposal proposal, DateTimeOffset now)
    {
        Expire(state, proposal, now);
        if (!proposal.Terminal && proposal.Claim is null) {
            proposal.LastCheckedAt = now;
            proposal.LastCheckEvidence = null;
            Block(proposal, "VerificationUnavailable");
        }
    }

    public static ExecutionClaim Claim(QueueState state, Guid id, long revision, EvaluationEvidence evidence, DateTimeOffset now)
    {
        var proposal = Find(state, id);
        RequireMutable(proposal, revision);
        Evaluate(state, proposal, evidence, now);
        if (!state.Policy.AutomaticDeletion || !state.Policy.SeerrManagedDeletionAcknowledged || proposal.Status != ProposalStatus.Ready)
            throw new QueueException("ExecutionBlocked");
        proposal.Claim = new ExecutionClaim(Guid.NewGuid(), now, proposal.Target.Fingerprint, proposal.Revision);
        proposal.Status = ProposalStatus.Claimed;
        proposal.Reason = "Claimed";
        proposal.Revision++;
        return proposal.Claim;
    }

    public static void Uncertain(QueueState state, Guid id)
    {
        var p = Find(state, id);
        if (p.Claim is null || p.Terminal || p.FilesDeletedAt is not null) throw new QueueException("InvalidCheckpoint");
        p.Status = ProposalStatus.Uncertain;
        p.Reason = "OutcomeUncertain";
        p.Revision++;
    }

    public static void FilesDeleted(QueueState state, Guid id, string fingerprint, string evidence, DateTimeOffset now)
    {
        var p = Find(state, id);
        if (p.Claim is null || p.Terminal || p.Claim.TargetFingerprint != fingerprint || string.IsNullOrWhiteSpace(evidence))
            throw new QueueException("InvalidCheckpoint");
        if (p.FilesDeletedAt is not null) throw new QueueException("CheckpointAlreadyRecorded");
        p.FilesDeletedAt = now;
        p.DeletionEvidence = evidence;
        p.Status = ProposalStatus.FilesDeleted;
        p.Reason = "FilesDeleted";
        p.Revision++;
    }

    public static void Complete(QueueState state, Guid id)
        => Complete(state, id, null);
    public static void Complete(QueueState state, Guid id, DateTimeOffset? now)
    {
        var p = Find(state, id);
        if (p.FilesDeletedAt is null || p.Status != ProposalStatus.FilesDeleted) throw new QueueException("FilesDeletedCheckpointRequired");
        p.CleanupOutcome = p.Target.RequestOnly ? "RequestDeclinedHistoryRetained" : "NotApplicableArrRecordRemovedBySeerr";
        p.Status = ProposalStatus.Completed;
        p.Reason = "Completed";
        p.ClosedAt = now;
        p.Revision++;
    }

    public static void CloseExternalChange(QueueState state, Guid id, long revision, string fingerprint, ExternalTargetChange change, DateTimeOffset now)
    {
        var p=Find(state,id);
        if(p.Revision!=revision||p.Target.ConfigurationRevision!=state.Seerr.Revision)throw new QueueException("RevisionChanged");
        if(p.Terminal||p.Claim is not null||p.FilesDeletedAt is not null||p.Target.Fingerprint!=fingerprint||
            change.Reason is not ("RemovedOutsideQueue" or "RequestClosedOutsideQueue" or "UpstreamTargetMissing"))throw new QueueException("InvalidCheckpoint");
        p.ClosedCheckpoint=UserProgress.Checkpoint(p);p.ClosedChecksAt=p.EligibleSince;
        p.Status=ProposalStatus.Cancelled;p.Reason=change.Reason;p.EligibleSince=null;p.ClosedAt=now;p.LastCheckedAt=now;p.Revision++;
        // No claim/FilesDeleted checkpoint: the queue did not perform removal.
    }

    public static void UpdatePolicy(QueueState state, Policy policy, long expectedRevision)
    {
        if (state.Policy.Revision != expectedRevision) throw new QueueException("RevisionChanged");
        if (policy is null || policy.Accounts is null || policy.Accounts.Count > 1000 || policy.Accounts.ContainsKey(Guid.Empty) ||
            policy.ActivityDays is < 1 or > 365 || policy.DelayHours is < 48 or > 8760 || policy.AdministratorApprovalDays is < 1 or > 365 || policy.ActivityWaitTimeoutDays is < 1 or > 365 || policy.RecentHistoryDays is <1 or >365 ||
            !Enum.IsDefined(policy.DefaultAuthority) || policy.Accounts.Values.Any(v => !Enum.IsDefined(v)))
            throw new QueueException("InvalidPolicy");
        var oldPolicy = state.Policy;
        var historyOnly=oldPolicy.RecentHistoryDays!=policy.RecentHistoryDays&&oldPolicy.DefaultAuthority==policy.DefaultAuthority&&oldPolicy.ActivityDays==policy.ActivityDays&&oldPolicy.DelayHours==policy.DelayHours&&oldPolicy.AdministratorApprovalDays==policy.AdministratorApprovalDays&&oldPolicy.ActivityWaitTimeoutEnabled==policy.ActivityWaitTimeoutEnabled&&oldPolicy.ActivityWaitTimeoutDays==policy.ActivityWaitTimeoutDays&&oldPolicy.AutomaticDeletion==policy.AutomaticDeletion&&oldPolicy.RequireAdministratorApproval==policy.RequireAdministratorApproval&&oldPolicy.SeerrManagedDeletionAcknowledged==policy.SeerrManagedDeletionAcknowledged&&oldPolicy.Suspended==policy.Suspended&&oldPolicy.Accounts.Count==policy.Accounts.Count&&oldPolicy.Accounts.All(pair=>policy.Accounts.TryGetValue(pair.Key,out var value)&&value==pair.Value);
        Dictionary<Guid,AdministratorApproval> preserved=historyOnly?state.Proposals.Where(p=>!p.Terminal&&p.Claim is null&&AdministratorApprovalCurrent(state,p)).ToDictionary(p=>p.Id,p=>p.AdministratorApproval!):[];
        var windowChanged = oldPolicy.ActivityDays != policy.ActivityDays;
        policy.Revision = expectedRevision + 1;
        state.Policy = policy;
        foreach (var p in state.Proposals.Where(p => !p.Terminal && p.Claim is null))
        {
            ExtendApprovalDeadline(state, p);
            p.DelayFloorHours = Math.Max(p.DelayFloorHours, policy.DelayHours);
            var oldAuthority = oldPolicy.Accounts.GetValueOrDefault(p.OwnerId, oldPolicy.DefaultAuthority);
            if (historyOnly&&preserved.TryGetValue(p.Id,out var approval))p.AdministratorApproval=approval with{Snapshot=AdministratorSnapshot(state,p)};
            if (!historyOnly&&(windowChanged || policy.Suspended || policy.RequireAdministratorApproval || oldAuthority != Authority(state, p.OwnerId))) Block(p, "PolicyChanged");
            else ExtendDeadline(p);
            p.Revision++;
        }
    }

    public static void UpdatePreferences(QueueState state, Guid user, long revision, int hours, bool notifications)
    {
        if (hours is < 48 or > 8760) throw new QueueException("InvalidDelay");
        var preferences = state.Preferences.GetValueOrDefault(user) ?? new Preferences();
        if (preferences.Revision != revision) throw new QueueException("RevisionChanged");
        preferences.DelayHours = hours;
        preferences.Notifications = notifications;
        preferences.Revision++;
        state.Preferences[user] = preferences;
        foreach (var p in state.Proposals.Where(p => !p.Terminal && p.Claim is null &&
                     (p.OwnerId == user || p.Consents.ContainsKey(user) || state.Activity.Any(a => a.UserId == user && a.Key == p.Target.ActivityKey))))
        {
            ExtendDelay(state, p, user);
            ExtendDeadline(p);
            p.Revision++;
        }
    }
    public static void UpdatePersonalTimelines(QueueState state, Guid user, int activityDays, int approvalDays,int? recentHistoryDays=null)
    {
        if (activityDays is < 0 or > 365 || approvalDays is < 0 or > 365 || recentHistoryDays is <0 or >365) throw new QueueException("InvalidDelay");
        var preferences = state.Preferences[user];
        if(recentHistoryDays is {} days)preferences.RecentHistoryDays=days;
        if (preferences.ActivityDays == activityDays && preferences.AdministratorApprovalDays == approvalDays) return;
        var activityChanged = preferences.ActivityDays != activityDays;
        preferences.ActivityDays = activityDays; preferences.AdministratorApprovalDays = approvalDays;
        foreach (var p in state.Proposals.Where(p => !p.Terminal && p.Claim is null && (p.OwnerId == user || activityChanged && state.Activity.Any(a => a.UserId == user && a.Key == p.Target.ActivityKey)))) {
            ExtendApprovalDeadline(state, p);
            Block(p, "PolicyChanged"); p.AdministratorApproval = null; p.Revision++;
        }
    }

    public static void Subscription(QueueState state, Guid id, Guid user, long revision, bool subscribed)
    {
        _ = Find(state, id);
        var pref = state.Preferences.GetValueOrDefault(user) ?? new Preferences();
        if (pref.Revision != revision) throw new QueueException("RevisionChanged");
        if (subscribed && pref.Subscriptions.Count >= 1000 && !pref.Subscriptions.Contains(id)) throw new QueueException("SubscriptionCapacityReached");
        if (subscribed) pref.Subscriptions.Add(id); else pref.Subscriptions.Remove(id);
        pref.Revision++;
        state.Preferences[user] = pref;
    }

    public static void ResetUser(QueueState state, Guid user, long policyRevision, long preferencesRevision)
    {
        if (user == Guid.Empty) throw new QueueException("NotFound");
        if ((state.Preferences.GetValueOrDefault(user)?.Revision ?? 0) != preferencesRevision)
            throw new QueueException("RevisionChanged");
        var old = state.Policy;
        var accounts = new Dictionary<Guid, ProposalAuthority>(old.Accounts);
        accounts.Remove(user);
        UpdatePolicy(state, new Policy { Accounts = accounts, DefaultAuthority = old.DefaultAuthority,
            ActivityDays = old.ActivityDays, DelayHours = old.DelayHours, AdministratorApprovalDays = old.AdministratorApprovalDays,
            ActivityWaitTimeoutEnabled = old.ActivityWaitTimeoutEnabled, ActivityWaitTimeoutDays = old.ActivityWaitTimeoutDays,
            RecentHistoryDays=old.RecentHistoryDays,
             AutomaticDeletion = old.AutomaticDeletion, SeerrManagedDeletionAcknowledged = old.SeerrManagedDeletionAcknowledged,
            RequireAdministratorApproval = old.RequireAdministratorApproval,
            Suspended = old.Suspended }, policyRevision);
        UpdatePreferences(state, user, preferencesRevision, 48, true);
        UpdatePersonalTimelines(state, user, 0, 0,0);
        state.Preferences[user].Subscriptions.Clear();
    }
    public static int ResetAllUserPermissions(QueueState state, long policyRevision)
    {
        var policy = state.Policy; var count = policy.Accounts.Count;
        UpdatePolicy(state, new Policy {
            Accounts = [], DefaultAuthority = policy.DefaultAuthority, ActivityDays = policy.ActivityDays, AdministratorApprovalDays = policy.AdministratorApprovalDays,
            ActivityWaitTimeoutEnabled = policy.ActivityWaitTimeoutEnabled, ActivityWaitTimeoutDays = policy.ActivityWaitTimeoutDays,
            RecentHistoryDays=policy.RecentHistoryDays,
            DelayHours = policy.DelayHours, AutomaticDeletion = policy.AutomaticDeletion,
            SeerrManagedDeletionAcknowledged = policy.SeerrManagedDeletionAcknowledged,
            RequireAdministratorApproval = policy.RequireAdministratorApproval, Suspended = policy.Suspended
        }, policyRevision);
        return count;
    }

    public static UserProposal Project(QueueState state, Proposal p, Guid caller, DateTimeOffset now)
    {
        Expire(state, p, now);
        var snapshot = Snapshot(state, p.Target, caller);
        p.Consents.TryGetValue(caller, out var consent);
        var answer = consent?.Snapshot == snapshot ? consent.Answer : (Answer?)null;
        var ownActivity = state.Activity.SingleOrDefault(a => a.UserId == caller && a.Key == p.Target.ActivityKey);
        var action = !p.Terminal && p.Claim is null && IsRecent(state, p.Target, caller, now) && answer != Answer.Yes
            ? "ApproveCurrentSnapshot" : "None";
        var progress = UserProgress.For(state, p);
        return new UserProposal(p.Id, p.Target.Title, p.OwnerId == caller ? "Yours" : "Anonymous",
            p.Status.ToString(), UserProgress.PublicReason(p.Reason), p.Revision, p.NotBefore, p.OwnerId == caller && !p.Terminal && p.Claim is null,
            snapshot, answer, ownActivity?.At, action) {
                CreatedAt = p.CreatedAt,
                StatusLabel = progress.Label,
                StatusHelp = progress.Help,
                ScheduledFor = progress.ScheduledFor,
                Timeline = progress.Timeline,
                EvaluatedAt = now,
                LastCheckedAt = p.LastCheckedAt,
                CheckRefreshHelp = p.Terminal || p.Claim is not null ? "" : "Safety checks retry about every minute while the server is running, and after relevant changes. Slow checks may take longer. Rechecking does not execute removal.",
                ActivityWindowDays = state.Policy.ActivityDays,
                SafetyDelayHours = p.Terminal ? p.DelayFloorHours : Math.Max(state.Policy.DelayHours, p.DelayFloorHours),
                ClosedCheckpoint = p.ClosedCheckpoint is "checks" or "delay" or "approval" or "activity-timeout" ? p.ClosedCheckpoint : null,
                ClosureExplanation = p.Terminal && p.Status != ProposalStatus.Completed ? progress.Help : null,
                MediaKind = p.Target.Kind.ToString(),
                CanConsent = !p.Terminal && p.Claim is null && IsRecent(state, p.Target, caller, now),
                ConsentStatus = answer==Answer.Yes?"Allowed":answer==Answer.No?"Kept":p.Inactive&&p.ActivityVerifiedAt is not null?"Not needed":IsRecent(state,p.Target,caller,now)?"Awaiting consent":"Not required",
                ActivityStatus = p.Reason is "ActivityCoverageIncomplete" or "VerificationUnavailable"?"Checking":p.Reason=="PlaybackActive"?"Playing":p.Inactive&&p.ActivityVerifiedAt is not null?"Inactive":p.ActivityVerifiedAt is not null?"Recent activity":"Checking",
                RequesterLabel = p.RequiresAutomaticRuleValidation&&p.RequesterId is null?"Automation · requester unknown":p.OwnerId==caller?"You":"Another requester",
                ExpiresAt = ExpirationDeadline(state,p),
                Stage = p.Terminal ? "History" : p.Status is ProposalStatus.Waiting or ProposalStatus.Ready ? "Queued" :
                    p.Status is ProposalStatus.Claimed or ProposalStatus.Uncertain or ProposalStatus.FilesDeleted ? "Processing" :
                    p.Reason == "AdministratorApprovalRequired" ? "Administrator review" : p.Reason == "ConsentRequired" ? "Consent" : "Safety checks"
            };
    }

    public static bool CoverageComplete(QueueState state, DateTimeOffset now) => state.CoverageHealthy &&
        state.CoverageSince <= now.AddDays(-state.Policy.ActivityDays) && state.LastHeartbeat >= now.AddMinutes(-2);

    public static Proposal Find(QueueState state, Guid id) => state.Proposals.SingleOrDefault(p => p.Id == id)
        ?? throw new QueueException("NotFound");

    public static void BreakCoverage(QueueState state, string reason, DateTimeOffset now)
    {
        state.CoverageSince = now;
        state.CoverageHealthy = false;
        state.CoverageProblem = reason;
        foreach (var p in state.Proposals.Where(p => !p.Terminal && p.Claim is null))
        { Block(p, "ActivityCoverageIncomplete"); p.Revision++; }
    }

    public static int ActivityDays(QueueState state, Guid user) => Math.Max(state.Policy.ActivityDays, state.Preferences.GetValueOrDefault(user)?.ActivityDays ?? 0);
    private static bool IsRecent(QueueState state, VerifiedTarget target, Guid user, DateTimeOffset now) =>
        state.Activity.Any(a => a.UserId == user && a.Key == target.ActivityKey &&
            (a.At >= now.AddDays(-ActivityDays(state, user)) || a.Sessions.Count > 0));

    private static void RequireMutable(Proposal p, long revision)
    {
        if (p.Revision != revision) throw new QueueException("RevisionChanged");
        if (p.Terminal || p.Claim is not null) throw new QueueException("AlreadyClaimedOrClosed");
    }

    private static void ExtendDelay(QueueState state, Proposal p, Guid user) => p.DelayFloorHours = Math.Max(
        p.DelayFloorHours, Math.Max(state.Policy.DelayHours, state.Preferences.GetValueOrDefault(user)?.DelayHours ?? 48));

    private static void ExtendApprovalDeadline(QueueState state, Proposal p)
    {
        if (p.ApprovalWaitingSince is not { } since) return;
        var days = Math.Max(state.Policy.AdministratorApprovalDays, state.Preferences.GetValueOrDefault(p.OwnerId)?.AdministratorApprovalDays ?? 0);
        var deadline = since.AddDays(days);
        if (p.ApprovalDeadline is null || deadline > p.ApprovalDeadline) p.ApprovalDeadline = deadline;
    }

    private static void ExtendDeadline(Proposal p)
    {
        if (p.EligibleSince is not { } since) return;
        var next = since.AddHours(p.DelayFloorHours);
        if (p.NotBefore is null || next > p.NotBefore) p.NotBefore = next;
    }

    private static void Block(Proposal p, string reason)
    { p.Status = ProposalStatus.Blocked; p.Reason = reason; p.EligibleSince = null;
        if (reason != "AdministratorApprovalRequired") p.Inactive = false;
    }
}
