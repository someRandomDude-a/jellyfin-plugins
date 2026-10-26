namespace DeletionQueue.Core;

// Shared operational milestones only. Never project identities, individual votes,
// viewer activity times, administrator identity, storage evidence or upstream data.
internal sealed record UserProgress(string Label, string Help, DateTimeOffset? ScheduledFor, RemovalCheckpoint[] Timeline)
{
    public static string Checkpoint(Proposal p) => p.Status is ProposalStatus.Waiting or ProposalStatus.Ready ? "delay" : p.Reason == "AdministratorApprovalRequired" ? "approval" : "checks";
    public static string PublicReason(string reason) => reason switch {
        "ActivityCoverageIncomplete" or "ConsentRequired" or "AdministratorApprovalRequired" or "PlaybackActive" or "Suspended" or "PolicyChanged" or
        "ClockUnverified" or "OwnerNotAuthorized" or "TargetVerificationRequired" or "AutomaticRuleVerificationRequired" or "VerificationRequired" or "LocalCheckpointRequired" or "SeerrDelegationRequired" or
         "AdministratorApprovalExpired" or "ActivityWaitExpired" or "VerificationUnavailable" or "DelayRequired" or "Ready" or "Claimed" or "OutcomeUncertain" or "FilesDeleted" or "Completed" or "Cancelled" or "AdministratorDenied" or "RequestExpired" => reason,
        _ => "VerificationRequired"
    };
    public static UserProgress For(QueueState state, Proposal p)
    {
        var queued = p.Status is ProposalStatus.Waiting or ProposalStatus.Ready;
        var automatic = state.Policy.AutomaticDeletion && state.Policy.SeerrManagedDeletionAcknowledged && !state.Policy.Suspended;
        var scheduled = queued && automatic ? p.NotBefore : null;
        var (label, help) = p.Status switch {
            ProposalStatus.Blocked => ("Blocked", Reason(p.Reason)),
            ProposalStatus.Waiting when !automatic => ("Removal paused", "Automatic removal is disabled or paused. The delay date is not a scheduled removal."),
            ProposalStatus.Waiting => ("Scheduled", "Removal can happen no earlier than the displayed date, after fresh safety checks. This is not a guaranteed execution time."),
            ProposalStatus.Ready when !automatic => ("Removal paused", "The delay has passed, but automatic removal is disabled or paused."),
            ProposalStatus.Ready => ("Ready for removal", "The delay has passed. Removal is waiting for the next successful safety check; no exact execution time is guaranteed."),
            ProposalStatus.Claimed => ("Removing", "A removal attempt has started. Its outcome is being checked."),
            ProposalStatus.Uncertain => ("Needs review", "The removal outcome could not be verified. No automatic repeat attempt will be made."),
            ProposalStatus.FilesDeleted => ("Removal confirmed", "Seerr confirmed the removal. The queue is recording completion; request history is retained."),
            ProposalStatus.Completed => ("Completed", "The removal was confirmed and recorded. Request history is retained."),
            ProposalStatus.Cancelled => ("Cancelled", "This removal request was withdrawn using Undo. It will not be executed."),
            ProposalStatus.Expired when p.Reason == "AdministratorDenied" => ("Denied", "This request was denied and closed. A new removal request can be submitted."),
            ProposalStatus.Expired when p.Reason == "AdministratorApprovalExpired" => ("Expired", "The administrator approval deadline passed. This request is closed."),
            ProposalStatus.Expired when p.Reason == "ActivityWaitExpired" => ("Expired", "The activity/consent waiting limit passed. This request is closed without removal."),
            ProposalStatus.Expired => ("Expired", "This request was not accepted within 30 days and is now closed."),
            _ => ("Pending checks", "The queue is checking this request. No removal time is confirmed.")
        };
        var closedWithoutRemoval = p.Terminal && p.Status != ProposalStatus.Completed;
        var started = p.Claim is not null;
        var removed = p.FilesDeletedAt is not null;
        var checksPassed = p.EligibleSince is not null || started || removed;
        var delayPassed = p.Status is ProposalStatus.Ready or ProposalStatus.Claimed or ProposalStatus.Uncertain or ProposalStatus.FilesDeleted or ProposalStatus.Completed;
        var steps = new RemovalCheckpoint[] {
            new("requested", "Requested", "complete", p.CreatedAt),
            new("checks", !checksPassed && !p.Terminal ? p.Reason == "ConsentRequired" ? "Awaiting consent" : p.Reason == "AdministratorApprovalRequired" ? "Administrator review" : "Safety checks" : "Checks & consent", closedWithoutRemoval ? "stopped" : checksPassed ? "complete" : "current", checksPassed ? p.EligibleSince : null),
            new("delay", "Safety delay", closedWithoutRemoval ? "stopped" : delayPassed ? "complete" : queued ? "current" : "pending", queued || started || removed ? p.NotBefore : null),
            new("removing", "Removal started", started ? removed ? "complete" : "current" : closedWithoutRemoval ? "stopped" : "pending", p.Claim?.At),
            new("confirmed", "Removal confirmed", removed ? "complete" : closedWithoutRemoval ? "stopped" : "pending", p.FilesDeletedAt),
            new("closed", p.Terminal ? label : "Completed", p.Terminal ? "current" : "pending", p.ClosedAt)
        };
        if (closedWithoutRemoval && p.ClosedCheckpoint is "checks" or "delay") {
            var index = Array.FindIndex(steps, step => step.Key == p.ClosedCheckpoint);
            steps[index] = steps[index] with { State = "cancelled", At = index == 2 ? p.NotBefore : p.ClosedAt,
                DateLabel = index == 2 && p.NotBefore is not null ? "Abandoned earliest date" : "Stopped at" };
            if (index > 1) steps[1] = steps[1] with { State = "complete", At = p.ClosedChecksAt };
            steps[^1] = steps[^1] with { State = "complete" };
        }
        steps[1] = steps[1] with { Label = p.Inactive && p.ActivityVerifiedAt is not null ? "Inactive" : "Consent and activity", At = steps[1].At ?? p.ActivityVerifiedAt };
        if (state.Policy.RequireAdministratorApproval || p.ApprovalWaitingSince is not null || p.AdministratorApproval is not null || p.Reason == "AdministratorDenied") {
            var approved = p.AdministratorApproval is not null && (p.Terminal || QueueRules.AdministratorApprovalCurrent(state, p));
            var denied = p.Reason == "AdministratorDenied";
            var expired = p.Reason == "AdministratorApprovalExpired";
            if ((denied || expired) && steps[1].State == "cancelled") steps[1] = steps[1] with { State = "stopped" };
            var approval = new RemovalCheckpoint("approval", "Administrator consent", denied || expired ? "cancelled" : approved ? "complete" : p.Reason == "AdministratorApprovalRequired" ? "current" : p.Terminal ? "stopped" : "pending",
                denied ? p.ClosedAt : approved ? null : p.ApprovalDeadline) { Decision = denied ? "denied" : approved ? "approved" : p.Reason == "AdministratorApprovalExpired" ? "expired" : "pending", DateLabel = !approved && !denied && p.ApprovalDeadline is not null ? "Decision deadline" : null };
            steps = steps.Take(2).Append(approval).Concat(steps.Skip(2)).ToArray();
        }
        // These are alternative eligibility routes, not separate mandatory waits:
        // no recent viewers OR current consent from every relevant recent viewer.
        // Both still require verified history, no active playback and all other gates.
        var checksComplete = checksPassed || p.Reason == "AdministratorApprovalRequired" || p.ClosedChecksAt is not null;
        var inactive = p.Inactive && p.ActivityVerifiedAt is not null;
        var lookback = state.Activity.Where(a=>a.Key==p.Target.ActivityKey).Select(a=>QueueRules.ActivityDays(state,a.UserId)).DefaultIfEmpty(state.Policy.ActivityDays).Max();
        steps[1] = steps[1] with { Paths = [
            new("activity", "All users inactive", inactive && checksComplete ? "complete" : checksComplete ? "skipped" : p.Terminal ? "stopped" : p.Reason == "ConsentRequired" ? "skipped" : "current") { DurationDays = lookback },
            new("consent", "All active viewers consent", checksComplete ? inactive ? "skipped" : "complete" : p.Terminal ? "stopped" : p.Reason == "ConsentRequired" ? "current" : "pending")
        ] };
        if(state.Policy.ActivityWaitTimeoutEnabled || p.Reason=="ActivityWaitExpired") {
            var expired=p.Reason=="ActivityWaitExpired";
            var limit=expired && p.ClosedAt is {} closed ? (closed-p.CreatedAt).TotalDays : state.Policy.ActivityWaitTimeoutDays;
            var deadline=expired?p.ClosedAt:p.Status==ProposalStatus.Blocked && !QueueRules.AdministratorReviewWaiting(state,p)?p.CreatedAt.AddDays(state.Policy.ActivityWaitTimeoutDays):(DateTimeOffset?)null;
            steps[1]=steps[1] with{Paths=steps[1].Paths.Append(new("activity-timeout","Activity/consent wait expired",expired?"cancelled":checksComplete||p.Terminal?"stopped":"pending",deadline){DurationDays=limit,DateLabel="Waiting deadline (from request creation)"}).ToArray()};
        }
        var delayIndex = Array.FindIndex(steps,s=>s.Key=="delay");
        steps[delayIndex] = steps[delayIndex] with { DurationHours = p.Terminal ? p.DelayFloorHours : Math.Max(p.DelayFloorHours,state.Policy.DelayHours) };
        var approvalIndex = Array.FindIndex(steps,s=>s.Key=="approval");
        if(approvalIndex>=0 && (state.Policy.RequireAdministratorApproval || p.Terminal || p.AdministratorApproval is not null)) {
            var approval=steps[approvalIndex];
            var limit=p.ApprovalDeadline is {} deadline && p.ApprovalWaitingSince is {} since
                ? (deadline-since).TotalDays : Math.Max(state.Policy.AdministratorApprovalDays,state.Preferences.GetValueOrDefault(p.OwnerId)?.AdministratorApprovalDays??0);
            steps[approvalIndex]=approval with { DurationDays=limit, Paths=[
                new("approved","Approved",approval.Decision=="approved"?"complete":closedWithoutRemoval?"stopped":"pending"),
                new("approval-timeout","Denied or timed out",approval.Decision is "denied" or "expired"?"cancelled":approval.Decision=="approved"?"skipped":p.Terminal?"stopped":"pending",approval.Decision=="denied"?p.ClosedAt:approval.Decision=="approved"?null:p.ApprovalDeadline){DurationDays=limit,DateLabel="Decision deadline"}
            ] };
        }
        return new(label, help, scheduled, steps);
    }
    private static string Reason(string reason) => reason switch {
        "ActivityCoverageIncomplete" => "Jellyfin playback history could not be fully verified yet. Removal stays blocked until a complete media-history check and healthy activity collector are available.",
        "VerificationUnavailable" => "Fresh verification could not be completed. The queue will retry automatically; no removal is scheduled until checks succeed.",
        "ConsentRequired" => "Required consent is missing or no longer current. Removal stays blocked until consent and safety checks are satisfied.",
        "AdministratorApprovalRequired" => "Administrator approval is required before this request can proceed.",
        "PlaybackActive" => "The media is currently in use. Removal will wait until safety checks can pass.",
        "Suspended" or "PolicyChanged" => "Removal is paused while the current policy and safety requirements are checked.",
        "ClockUnverified" => "The server clock could not be verified. Removal is paused until timing checks recover.",
        "OwnerNotAuthorized" => "This request no longer meets the required access or ownership checks.",
        "AutomaticRuleVerificationRequired" => "The originating automatic rule must still be enabled and match current evidence. Missing or changed conditions block progress.",
        "TargetVerificationRequired" or "VerificationRequired" => "The media target and current safety conditions still need verification.",
        "LocalCheckpointRequired" => "The queue's storage checkpoint could not be verified. Removal is paused.",
        "SeerrDelegationRequired" => "The configured removal service could not be verified. Removal is paused.",
        _ => "Removal is waiting for safety checks. No removal time is confirmed."
    };
}
