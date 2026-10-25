namespace DeletionQueue.Core;

// Shared operational milestones only. Never project identities, individual votes,
// viewer activity times, administrator identity, storage evidence or upstream data.
internal sealed record UserProgress(string Label, string Help, DateTimeOffset? ScheduledFor, RemovalCheckpoint[] Timeline)
{
    public static string Checkpoint(Proposal p) => p.Status is ProposalStatus.Waiting or ProposalStatus.Ready ? "delay" : "checks";
    public static string PublicReason(string reason) => reason switch {
        "ActivityCoverageIncomplete" or "ConsentRequired" or "AdministratorApprovalRequired" or "PlaybackActive" or "Suspended" or "PolicyChanged" or
        "ClockUnverified" or "OwnerNotAuthorized" or "TargetVerificationRequired" or "VerificationRequired" or "LocalCheckpointRequired" or "SeerrDelegationRequired" or
        "DelayRequired" or "Ready" or "Claimed" or "OutcomeUncertain" or "FilesDeleted" or "Completed" or "Cancelled" or "AdministratorDenied" or "RequestExpired" => reason,
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
            new("checks", "Checks & consent", closedWithoutRemoval ? "stopped" : checksPassed ? "complete" : "current", checksPassed ? p.EligibleSince : null),
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
        return new(label, help, scheduled, steps);
    }
    private static string Reason(string reason) => reason switch {
        "ActivityCoverageIncomplete" => "Viewing-activity checks are not complete yet. Removal stays blocked until the required observation window is verified.",
        "ConsentRequired" => "Required consent is missing or no longer current. Removal stays blocked until consent and safety checks are satisfied.",
        "AdministratorApprovalRequired" => "Administrator approval is required before this request can proceed.",
        "PlaybackActive" => "The media is currently in use. Removal will wait until safety checks can pass.",
        "Suspended" or "PolicyChanged" => "Removal is paused while the current policy and safety requirements are checked.",
        "ClockUnverified" => "The server clock could not be verified. Removal is paused until timing checks recover.",
        "OwnerNotAuthorized" => "This request no longer meets the required access or ownership checks.",
        "TargetVerificationRequired" or "VerificationRequired" => "The media target and current safety conditions still need verification.",
        "LocalCheckpointRequired" => "The queue's storage checkpoint could not be verified. Removal is paused.",
        "SeerrDelegationRequired" => "The configured removal service could not be verified. Removal is paused.",
        _ => "Removal is waiting for safety checks. No removal time is confirmed."
    };
}
