using System.Text.Json;
using DeletionQueue.Core;
using static DeletionQueue.Tests.QueueRulesTests;

namespace DeletionQueue.Tests;

public sealed class RemovalLifecycleTests
{
    [Fact] public void OptionalActivityWaitingClockIsNotRenewedByPlaybackAndCanBeDisabled() {
        var state=State();var p=Propose(state);
        QueueRules.Expire(state,p,Now.AddDays(400));Assert.False(p.Terminal);Assert.Null(QueueRules.Project(state,p,Owner.Id,Now.AddDays(400)).ExpiresAt);
        state.Policy.ActivityWaitTimeoutEnabled=true;state.Policy.ActivityWaitTimeoutDays=45;
        QueueRules.RecordActivity(state,Owner.Id,p.Target.ActivityKey,"new-play",false,Now.AddDays(44));
        Assert.Equal(Now.AddDays(45),QueueRules.ExpirationDeadline(state,p));
        QueueRules.Expire(state,p,Now.AddDays(45));Assert.Equal("ActivityWaitExpired",p.Reason);Assert.Equal(Now.AddDays(45),p.ClosedAt);
        Assert.Equal("activity-timeout",p.ClosedCheckpoint);
        var graph=QueueRules.Project(state,p,Owner.Id,Now.AddDays(46)).Timeline.Single(s=>s.Key=="checks").Paths.Single(s=>s.Key=="activity-timeout");
        Assert.Equal(45,graph.DurationDays);Assert.Equal("cancelled",graph.State);
        state.Policy.ActivityWaitTimeoutEnabled=false;state.Policy.ActivityWaitTimeoutDays=90;
        Assert.Equal(45,QueueRules.Project(state,p,Owner.Id,Now.AddDays(47)).Timeline.Single(s=>s.Key=="checks").Paths.Single(s=>s.Key=="activity-timeout").DurationDays);
    }
    [Fact] public void AdministratorReviewClockAndAcceptedDelayAreSeparateFromActivityWaiting() {
        var state=State();state.Policy.ActivityWaitTimeoutEnabled=true;state.Policy.RequireAdministratorApproval=true;state.Policy.AdministratorApprovalDays=60;
        var p=Propose(state);Evaluate(state,p);Assert.Equal("AdministratorApprovalRequired",p.Reason);
        QueueRules.Expire(state,p,Now.AddDays(46));Assert.False(p.Terminal);Assert.Equal(Now.AddDays(60),QueueRules.ExpirationDeadline(state,p));
        QueueRules.Expire(state,p,Now.AddDays(60));Assert.Equal("AdministratorApprovalExpired",p.Reason);
        var accepted=State();accepted.Policy.ActivityWaitTimeoutEnabled=true;var waiting=Propose(accepted);
        Evaluate(accepted,waiting,Now.AddDays(44),Good with{NativeHistoryVerified=true});Assert.Equal(ProposalStatus.Waiting,waiting.Status);
        QueueRules.Expire(accepted,waiting,Now.AddDays(46));Assert.Equal(ProposalStatus.Waiting,waiting.Status);
    }
    [Fact] public void RepeatedRequestsReuseConsentWithoutRenewingOrDuplicating()
    {
        var state = State(); var p = Propose(state); var revision = p.Revision;
        var joined = QueueRules.Propose(state, Owner, p.Target, true, QueueRules.Snapshot(state, p.Target, Owner.Id), Now.AddDays(10));
        Assert.Same(p, joined); Assert.Single(state.Proposals); Assert.Equal(revision, p.Revision); Assert.Equal(Now, p.CreatedAt);
        QueueRules.Propose(state, Viewer, p.Target, true, QueueRules.Snapshot(state, p.Target, Viewer.Id), Now.AddDays(10));
        Assert.Equal(Answer.Yes, p.Consents[Viewer.Id].Answer); Assert.Single(state.Proposals);
    }
    [Fact] public void UnacceptedRequestsUseTheOptionalWaitingLimitAndCanBeRecreated()
    {
        var state = State();state.Policy.ActivityWaitTimeoutEnabled=true; var old = Propose(state);
        QueueRules.Expire(state,old,Now.AddDays(31));Assert.False(old.Terminal);
        var next = QueueRules.Propose(state, Owner, old.Target, true, QueueRules.Snapshot(state, old.Target, Owner.Id), Now.AddDays(45));
        Assert.NotEqual(old.Id, next.Id); Assert.Equal(ProposalStatus.Expired, old.Status); Assert.Equal("ActivityWaitExpired", old.Reason);
        Assert.Equal(2, state.Proposals.Count);
    }
    [Fact] public void AcceptedOrInFlightRequestsAreNotExpiredByTheIntakeDeadline()
    {
        var state = State();state.Policy.ActivityWaitTimeoutEnabled=true; var p = Propose(state); Evaluate(state, p);
        QueueRules.Expire(state,p, Now.AddDays(46)); Assert.Equal(ProposalStatus.Waiting, p.Status);
        p.Status = ProposalStatus.Uncertain; p.Claim = new(Guid.NewGuid(), Now, p.Target.Fingerprint, p.Revision);
        QueueRules.Expire(state,p, Now.AddDays(46)); Assert.Equal(ProposalStatus.Uncertain, p.Status);
    }
    [Fact] public void AdministratorRequesterApprovesWithoutBypassingOtherViewers()
    {
        var state = State(); state.Policy.RequireAdministratorApproval = true;
        QueueRules.RecordActivity(state, Viewer.Id, Target.ActivityKey, null, false, Now);
        var p = Propose(state, caller: Owner with { Administrator = true });
        Assert.True(QueueRules.AdministratorApprovalCurrent(state, p));
        Evaluate(state, p, evidence: Good with { AdministratorAuthorized = true }); Assert.Equal("ConsentRequired", p.Reason);
        QueueRules.RecordActivity(state, Viewer.Id, p.Target.ActivityKey, null, false, Now.AddMinutes(1));
        Assert.False(QueueRules.AdministratorApprovalCurrent(state, p));
    }
    [Fact] public void AdministratorDenialExpiresRequestImmediately()
    {
        var state = State(); state.Policy.RequireAdministratorApproval = true; var p = Propose(state);
        QueueRules.Deny(state, p.Id, Viewer with { Administrator = true }, p.Revision, QueueRules.AdministratorSnapshot(state, p), true, Now);
        Assert.True(p.Terminal); Assert.Equal("AdministratorDenied", p.Reason);
        Assert.NotEqual(p.Id, QueueRules.Propose(state, Owner, p.Target, true, QueueRules.Snapshot(state, p.Target, Owner.Id), Now).Id);
    }
    [Theory] [InlineData(48, "2")] [InlineData(49, "2.04")]
    public void PlaybackResetsDeadlineAndPublishesOneOwnerOnlyNoticePerPlaySession(int hours, string days)
    {
        var state = State(); state.Schema = 2; state.Hub.NotificationMode = NotificationMode.Hub; state.Hub.PublishAudit = false; state.Policy.DelayHours = hours;
        var p = Propose(state); Evaluate(state, p);
        var at = Now.AddDays(1); var before = p.Revision;
        QueueRules.RecordActivity(state, Owner.Id, p.Target.ActivityKey, "play", false, at);
        Assert.Equal(at.AddHours(hours), p.NotBefore); Assert.Equal(at, p.LastPlaybackResetAt);
        Assert.Null(QueueRules.Project(state, p, Owner.Id, at).OwnAnswer);
        HubIntegration.Capture(state, new Dictionary<Guid,long> { [p.Id] = before }, [], "activity", "ActivityCaptured", at, true);
        var reset = Assert.Single(state.Outbox, e => JsonDocument.Parse(e.PayloadJson).RootElement.GetProperty("type").GetString() == "removal.playback_reset");
        var payload = JsonDocument.Parse(reset.PayloadJson).RootElement;
        Assert.Equal(Owner.Id, payload.GetProperty("audience")[0].GetGuid()); Assert.Equal(days, payload.GetProperty("metadata").GetProperty("days").GetString());
        before = p.Revision; QueueRules.RecordActivity(state, Owner.Id, p.Target.ActivityKey, "play", false, at.AddMinutes(1));
        HubIntegration.Capture(state, new Dictionary<Guid,long> { [p.Id] = before }, [], "activity", "ActivityCaptured", at.AddMinutes(1), true);
        Assert.Single(state.Outbox, e => e.PayloadJson.Contains("removal.playback_reset"));
    }
}
