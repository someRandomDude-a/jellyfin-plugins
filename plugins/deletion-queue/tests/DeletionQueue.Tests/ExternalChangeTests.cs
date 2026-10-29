using DeletionQueue.Core;

namespace DeletionQueue.Tests;

public sealed class ExternalChangeTests
{
    [Theory][InlineData("RemovedOutsideQueue")][InlineData("RequestClosedOutsideQueue")][InlineData("UpstreamTargetMissing")]
    public void ExternalChangesCloseUnclaimedWorkWithoutInventingADeletionCheckpoint(string reason)
    {
        var state=QueueRulesTests.State();var p=QueueRulesTests.Propose(state);var target=p.Target;
        NoticeRules.Publish(state,p);QueueRules.CloseExternalChange(state,p.Id,p.Revision,target.Fingerprint,new(reason),QueueRulesTests.Now.AddMinutes(1));
        NoticeRules.Publish(state,p);NoticeRules.Publish(state,p);
        Assert.Equal(ProposalStatus.Cancelled,p.Status);Assert.Equal(reason,p.Reason);Assert.Null(p.Claim);Assert.Null(p.FilesDeletedAt);Assert.Null(p.DeletionEvidence);
        Assert.Equal(2,state.Notices.Count);Assert.DoesNotContain(state.Notices,n=>n.Text.Contains("Undo"));
        var row=QueueRules.Project(state,p,p.OwnerId,QueueRulesTests.Now.AddMinutes(1));Assert.False(row.CanUndo);Assert.Null(row.ScheduledFor);Assert.Equal(reason,row.Reason);
        Assert.Throws<QueueException>(()=>QueueRules.ProposeAutomatically(state,QueueRulesTests.Owner,target,QueueRulesTests.Now.AddMinutes(2)));
    }
    [Fact] public void StaleFingerprintRevisionOrConnectionCannotCloseAnotherTarget()
    {
        var state=QueueRulesTests.State();var p=QueueRulesTests.Propose(state);var change=new ExternalTargetChange("RemovedOutsideQueue");
        Assert.Throws<QueueException>(()=>QueueRules.CloseExternalChange(state,p.Id,p.Revision-1,p.Target.Fingerprint,change,QueueRulesTests.Now));
        Assert.Throws<QueueException>(()=>QueueRules.CloseExternalChange(state,p.Id,p.Revision,"another-target",change,QueueRulesTests.Now));
        state.Seerr.Revision++;
        Assert.Throws<QueueException>(()=>QueueRules.CloseExternalChange(state,p.Id,p.Revision,p.Target.Fingerprint,change,QueueRulesTests.Now));Assert.False(p.Terminal);
    }
    [Fact] public void AnUncertainClaimCannotBeSilentlyCancelledByAnExternalObservation()
    {
        var state=QueueRulesTests.State();var p=QueueRulesTests.Propose(state);p.Status=ProposalStatus.Uncertain;p.Claim=new(Guid.NewGuid(),QueueRulesTests.Now,p.Target.Fingerprint,p.Revision);
        Assert.Throws<QueueException>(()=>QueueRules.CloseExternalChange(state,p.Id,p.Revision,p.Target.Fingerprint,new("RemovedOutsideQueue"),QueueRulesTests.Now));
        Assert.Equal(ProposalStatus.Uncertain,p.Status);Assert.NotNull(p.Claim);Assert.Null(p.FilesDeletedAt);
    }
}
