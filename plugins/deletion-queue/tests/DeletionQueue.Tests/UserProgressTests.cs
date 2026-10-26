using System.Text.Json;
using DeletionQueue.Core;
using static DeletionQueue.Tests.QueueRulesTests;

namespace DeletionQueue.Tests;

public sealed class UserProgressTests
{
    [Fact] public void InactivityAndUnanimousRecentViewerConsentAreAlternativeProjectedPaths() {
        var state=State();var p=Propose(state);Evaluate(state,p);
        var paths=QueueRules.Project(state,p,Owner.Id,Now).Timeline.Single(s=>s.Key=="checks").Paths;
        Assert.Equal("complete",paths.Single(s=>s.Key=="activity").State);Assert.Equal("skipped",paths.Single(s=>s.Key=="consent").State);
        Assert.Equal(state.Policy.ActivityDays,paths.Single(s=>s.Key=="activity").DurationDays);
        QueueRules.RecordActivity(state,Viewer.Id,p.Target.ActivityKey,null,false,Now);
        Evaluate(state,p);paths=QueueRules.Project(state,p,Owner.Id,Now).Timeline.Single(s=>s.Key=="checks").Paths;
        Assert.Equal("current",paths.Single(s=>s.Key=="consent").State);Assert.Equal("skipped",paths.Single(s=>s.Key=="activity").State);
        QueueRules.Consent(state,p.Id,Viewer,p.Revision,QueueRules.Snapshot(state,p.Target,Viewer.Id),Answer.Yes,true,Now);
        Evaluate(state,p);paths=QueueRules.Project(state,p,Owner.Id,Now).Timeline.Single(s=>s.Key=="checks").Paths;
        Assert.Equal("complete",paths.Single(s=>s.Key=="consent").State);Assert.Equal("skipped",paths.Single(s=>s.Key=="activity").State);
        QueueRules.RecordActivity(state,Viewer.Id,p.Target.ActivityKey,"playing",false,Now);
        QueueRules.Consent(state,p.Id,Viewer,p.Revision,QueueRules.Snapshot(state,p.Target,Viewer.Id),Answer.Yes,true,Now);
        Evaluate(state,p);Assert.Equal("PlaybackActive",p.Reason);
        Assert.DoesNotContain(QueueRules.Project(state,p,Owner.Id,Now).Timeline.Single(s=>s.Key=="checks").Paths,s=>s.State=="complete");
    }
    [Fact] public void ApprovalForkUsesConfiguredDeadlineAndTimeoutDoesNotEnterDelay() {
        var state=State();state.Policy.RequireAdministratorApproval=true;state.Policy.AdministratorApprovalDays=30;
        var p=Propose(state);Evaluate(state,p);
        var approval=QueueRules.Project(state,p,Owner.Id,Now).Timeline.Single(s=>s.Key=="approval");
        Assert.Equal(30,approval.DurationDays);Assert.Equal(Now.AddDays(30),approval.Paths.Single(s=>s.Key=="approval-timeout").At);
        Assert.Equal("complete",QueueRules.Project(state,p,Owner.Id,Now).Timeline.Single(s=>s.Key=="checks").Paths.Single(s=>s.Key=="activity").State);
        QueueRules.Expire(state,p,Now.AddDays(30));var result=QueueRules.Project(state,p,Owner.Id,Now.AddDays(30));
        Assert.Equal("AdministratorApprovalExpired",result.Reason);Assert.Null(result.ScheduledFor);
        approval=result.Timeline.Single(s=>s.Key=="approval");Assert.Equal("cancelled",approval.Paths.Single(s=>s.Key=="approval-timeout").State);
        Assert.Equal("stopped",approval.Paths.Single(s=>s.Key=="approved").State);Assert.Equal("stopped",result.Timeline.Single(s=>s.Key=="delay").State);
    }
    [Fact] public void LegacyAdministratorWaitDoesNotRenewItsClockOnUpgrade()
    {
        var state = State(); state.Policy.RequireAdministratorApproval = true;
        var p = Propose(state); p.CreatedAt = Now.AddDays(-10); p.Reason = "AdministratorApprovalRequired";
        QueueRules.Evaluate(state, p, Good, Now);
        Assert.Equal(Now.AddDays(-10), p.ApprovalWaitingSince);
        Assert.Equal(Now.AddDays(20), p.ApprovalDeadline);
    }
    [Fact] public void PersonalWindowRequiresConsentFromRecentParticipantsButNotUninvolvedAccounts()
    {
        var state = State(); state.Policy.ActivityDays = 14;
        state.Preferences[Viewer.Id] = new Preferences { ActivityDays = 30 };
        var p = Propose(state);
        QueueRules.RecordActivity(state, Viewer.Id, p.Target.ActivityKey, null, false, Now.AddDays(-20));
        QueueRules.Evaluate(state, p, Good, Now); Assert.Equal("ConsentRequired", p.Reason);
        QueueRules.Consent(state, p.Id, Viewer, p.Revision, QueueRules.Snapshot(state, p.Target, Viewer.Id), Answer.Yes, true, Now);
        QueueRules.Evaluate(state, p, Good, Now); Assert.Equal(ProposalStatus.Waiting, p.Status);
        Assert.False(p.Inactive);
    }
    [Fact] public void AdministratorTimeoutUsesTheLongerPersonalWindowAndIsNotRenewedByEvaluation()
    {
        var state = State(); state.Policy.RequireAdministratorApproval = true; state.Policy.AdministratorApprovalDays = 45;
        state.Preferences[Owner.Id] = new Preferences { AdministratorApprovalDays = 60 };
        var p = Propose(state); QueueRules.Evaluate(state, p, Good, Now);
        Assert.Equal(Now.AddDays(60), p.ApprovalDeadline);
        QueueRules.Evaluate(state, p, Good, Now.AddHours(1)); Assert.Equal(Now.AddDays(60), p.ApprovalDeadline);
        QueueRules.Expire(state,p, Now.AddDays(31)); Assert.False(p.Terminal);
        QueueRules.Expire(state,p, Now.AddDays(60)); Assert.Equal("AdministratorApprovalExpired", p.Reason);
        Assert.Equal("expired", QueueRules.Project(state, p, Owner.Id, Now.AddDays(60)).Timeline.Single(step => step.Key == "approval").Decision);
    }
    [Fact] public void CompleteNativeHistoryReplacesObservationWarmupButNotConsentOrDelay()
    {
        var state = State(); var p = Propose(state); state.CoverageSince = Now;
        QueueRules.Evaluate(state, p, Good, Now); Assert.Equal("ActivityCoverageIncomplete", p.Reason);
        var native = Good with { NativeHistoryVerified = true };
        QueueRules.Evaluate(state, p, native, Now); Assert.Equal(ProposalStatus.Waiting, p.Status);
        Assert.True(p.NotBefore >= Now.AddHours(48));
        QueueRules.RecordActivity(state, Viewer.Id, p.Target.ActivityKey, null, false, Now);
        Assert.False(p.Inactive);
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
        var expiredState = State();expiredState.Policy.ActivityWaitTimeoutEnabled=true; var expired = Propose(expiredState); QueueRules.Expire(expiredState,expired, Now.AddDays(45));
        var result = QueueRules.Project(expiredState, expired, Owner.Id, Now.AddDays(45));
        Assert.Equal("activity-timeout", result.ClosedCheckpoint); Assert.Contains("waiting limit", result.ClosureExplanation);
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
