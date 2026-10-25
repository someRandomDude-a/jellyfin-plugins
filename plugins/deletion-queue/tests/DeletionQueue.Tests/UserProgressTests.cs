using System.Text.Json;
using DeletionQueue.Core;
using static DeletionQueue.Tests.QueueRulesTests;

namespace DeletionQueue.Tests;

public sealed class UserProgressTests
{
    [Fact] public void CompleteNativeHistoryReplacesObservationWarmupButNotConsentOrDelay()
    {
        var state = State(); var p = Propose(state); state.CoverageSince = Now;
        QueueRules.Evaluate(state, p, Good, Now); Assert.Equal("ActivityCoverageIncomplete", p.Reason);
        var native = Good with { NativeHistoryVerified = true };
        QueueRules.Evaluate(state, p, native, Now); Assert.Equal(ProposalStatus.Waiting, p.Status);
        Assert.True(p.NotBefore >= Now.AddHours(48));
        QueueRules.RecordActivity(state, Viewer.Id, p.Target.ActivityKey, null, false, Now);
        QueueRules.Evaluate(state, p, native, Now); Assert.Equal("ConsentRequired", p.Reason);
        QueueRules.Evaluate(state, p, native with { CompleteActivityScan = false }, Now);
        Assert.Equal("ActivityCoverageIncomplete", p.Reason);
    }

    [Fact] public void UndoDuringDelayRetainsTheActualStoppingCheckpointDateAndPublicReason()
    {
        var state = State(); var p = Propose(state); Evaluate(state, p);
        var checksAt = p.EligibleSince; var closedAt = Now.AddHours(12);
        QueueRules.Undo(state, p.Id, Owner.Id, p.Revision, closedAt);
        var result = QueueRules.Project(state, p, Owner.Id, closedAt);
        Assert.Equal("delay", result.ClosedCheckpoint); Assert.Contains("Undo", result.ClosureExplanation);
        var stopped = Assert.Single(result.Timeline, step => step.State == "cancelled");
        Assert.Equal("delay", stopped.Key); Assert.Equal(p.NotBefore, stopped.At); Assert.Equal("Abandoned earliest date", stopped.DateLabel);
        Assert.Equal(closedAt, result.Timeline.Single(step => step.Key == "closed").At);
        Assert.Equal(checksAt, result.Timeline.Single(step => step.Key == "checks").At);
        Assert.Equal(state.Policy.ActivityDays, result.ActivityWindowDays); Assert.Equal(p.DelayFloorHours, result.SafetyDelayHours);
        Assert.Equal(closedAt, result.EvaluatedAt);
    }

    [Fact] public void DenialAndExpiryExplainWhyWithoutDisclosingWhoAndLegacyHistoryDoesNotGuessAStage()
    {
        var state = State(); var p = Propose(state);
        QueueRules.Deny(state, p.Id, new(Guid.NewGuid(), true, true), p.Revision, QueueRules.AdministratorSnapshot(state, p), true, Now);
        var denied = QueueRules.Project(state, p, Owner.Id, Now);
        Assert.Equal("checks", denied.ClosedCheckpoint); Assert.Contains("denied", denied.ClosureExplanation);
        p.ClosedCheckpoint = null;
        Assert.Null(QueueRules.Project(state, p, Owner.Id, Now).ClosedCheckpoint);
        var expiredState = State(); var expired = Propose(expiredState); QueueRules.Expire(expired, Now.AddDays(31));
        var result = QueueRules.Project(expiredState, expired, Owner.Id, Now.AddDays(31));
        Assert.Equal("checks", result.ClosedCheckpoint); Assert.Contains("30 days", result.ClosureExplanation);
    }

    [Fact] public void BlockedRequestsExplainTheGateWithoutAdvertisingAStaleSchedule()
    {
        var state = State(); var p = Propose(state); Evaluate(state, p);
        QueueRules.RecordActivity(state, Viewer.Id, p.Target.ActivityKey, null, false, Now.AddMinutes(1));
        var result = QueueRules.Project(state, p, Owner.Id, Now.AddMinutes(1));
        Assert.NotNull(p.NotBefore); Assert.Null(result.ScheduledFor); Assert.Equal("Blocked", result.StatusLabel);
        Assert.Contains("consent", result.StatusHelp);
        Assert.Equal("current", result.Timeline.Single(step => step.Key == "checks").State);
        Assert.Null(result.Timeline.Single(step => step.Key == "delay").At);
    }
    [Fact] public void QueuedDatesAreConditionalOnAutomaticRemovalAndRemainEarliestTimes()
    {
        var state = State(); var p = Propose(state); Evaluate(state, p);
        var paused = QueueRules.Project(state, p, Owner.Id, Now);
        Assert.Equal("Removal paused", paused.StatusLabel); Assert.Null(paused.ScheduledFor);
        state.Policy.AutomaticDeletion = true; state.Policy.SeerrManagedDeletionAcknowledged = true;
        var scheduled = QueueRules.Project(state, p, Owner.Id, Now);
        Assert.Equal("Scheduled", scheduled.StatusLabel); Assert.Equal(Now.AddHours(48), scheduled.ScheduledFor);
        Assert.Contains("not a guaranteed", scheduled.StatusHelp);
        Assert.Equal("current", scheduled.Timeline.Single(step => step.Key == "delay").State);
        Evaluate(state, p, Now.AddHours(48));
        Assert.Equal("Ready for removal", QueueRules.Project(state, p, Owner.Id, Now.AddHours(48)).StatusLabel);
    }
    [Fact] public void TimelineProjectsOnlySharedOperationalDatesNotIndividualVotesOrActivity()
    {
        var state = State(); var p = Propose(state);
        var privateActivity = Now.AddMinutes(-13); var privateVote = Now.AddMinutes(-11); var privateApproval = Now.AddMinutes(-7);
        state.Activity.Add(new Activity { UserId = Viewer.Id, Key = p.Target.ActivityKey, At = privateActivity });
        p.Consents[Viewer.Id] = new(Answer.Yes, "private-snapshot", privateVote);
        p.AdministratorApproval = new(Viewer.Id, "private-approval", privateApproval);
        var json = JsonSerializer.Serialize(QueueRules.Project(state, p, Owner.Id, Now));
        Assert.DoesNotContain(Viewer.Id.ToString(), json); Assert.DoesNotContain(privateActivity.ToString("O"), json);
        Assert.DoesNotContain("private-snapshot", json); Assert.DoesNotContain("private-approval", json);
        var timeline = QueueRules.Project(state, p, Owner.Id, Now).Timeline;
        Assert.DoesNotContain(timeline, step => step.At == privateActivity || step.At == privateVote || step.At == privateApproval);
        p.Reason = "upstream secret /personal/path";
        var safe = QueueRules.Project(state, p, Owner.Id, Now);
        Assert.Equal("VerificationRequired", safe.Reason); Assert.DoesNotContain("secret", safe.StatusHelp);
    }
    [Fact] public void NewClosedMilestonesHaveRealDatesAndOldHistoryDoesNotInventThem()
    {
        var state = State(); var p = Propose(state);
        QueueRules.Undo(state, p.Id, Owner.Id, p.Revision, Now.AddMinutes(5));
        var closed = QueueRules.Project(state, p, Owner.Id, Now.AddMinutes(5));
        Assert.Equal(Now.AddMinutes(5), closed.Timeline.Single(step => step.Key == "closed").At);
        p.ClosedAt = null;
        Assert.Null(QueueRules.Project(state, p, Owner.Id, Now).Timeline.Single(step => step.Key == "closed").At);
    }
    [Fact] public void CompletionShowsClaimConfirmationAndCompletionCheckpointsWithoutJournalEvidence()
    {
        var state = State(); state.Policy.AutomaticDeletion = true; state.Policy.SeerrManagedDeletionAcknowledged = true;
        var p = Propose(state); Evaluate(state, p); Evaluate(state, p, Now.AddHours(48));
        var claim = QueueRules.Claim(state, p.Id, p.Revision, Good, Now.AddHours(48));
        var confirmedAt = Now.AddHours(48).AddSeconds(4); var closedAt = confirmedAt.AddSeconds(1);
        QueueRules.FilesDeleted(state, p.Id, p.Target.Fingerprint, "private upstream evidence", confirmedAt);
        QueueRules.Complete(state, p.Id, closedAt);
        var result = QueueRules.Project(state, p, Owner.Id, closedAt);
        Assert.Equal(claim.At, result.Timeline.Single(step => step.Key == "removing").At);
        Assert.Equal(confirmedAt, result.Timeline.Single(step => step.Key == "confirmed").At);
        Assert.Equal(closedAt, result.Timeline.Single(step => step.Key == "closed").At);
        Assert.DoesNotContain("private upstream evidence", JsonSerializer.Serialize(result));
        Assert.Null(result.ScheduledFor);
    }
}
