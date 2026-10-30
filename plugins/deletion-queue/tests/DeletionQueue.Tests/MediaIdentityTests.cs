using DeletionQueue.Core;

namespace DeletionQueue.Tests;

public sealed class MediaIdentityTests
{
    [Fact] public void ConnectionRevisionTitleAndOldHashAreNotMediaIdentity()
    {
        var state=QueueRulesTests.State();var p=QueueRulesTests.Propose(state);var original=p.Target;
        var current=original with{ConfigurationRevision=original.ConfigurationRevision+10,Title="Renamed",Fingerprint="new hash"};
        Assert.Empty(MediaIdentity.Differences(original,current));
        Assert.Equal(MediaIdentity.Fingerprint(original),MediaIdentity.Fingerprint(current));
        var savedConsent=p.Consents.ToDictionary(pair=>pair.Key,pair=>pair.Value);
        Assert.Equal(original.Fingerprint,MediaIdentity.PreserveExistingFingerprint(current,state.Proposals).Fingerprint);
        Assert.Equal(savedConsent,p.Consents);
    }
    [Fact] public void ConcreteIdentityChangesAreExplainedAndCannotReuseConsentFingerprint()
    {
        var state=QueueRulesTests.State();var p=QueueRulesTests.Propose(state);var original=p.Target;
        foreach(var current in new[]{original with{JellyfinItemId=Guid.NewGuid()},original with{SeerrMediaId=original.SeerrMediaId+1},original with{Is4K=!original.Is4K},original with{ActivityKey="tv:other"},original with{DestructiveScopeKey="sonarr|99|123"},original with{RequestOnly=!original.RequestOnly},original with{RequestId=123},original with{RequestOwnerId=Guid.NewGuid()}}){
            Assert.NotEmpty(MediaIdentity.Differences(original,current));
            Assert.NotEqual(original.Fingerprint,MediaIdentity.PreserveExistingFingerprint(current,state.Proposals).Fingerprint);
        }
    }
    [Fact] public void ConfigurationSaveDoesNotPreventFreshExternalObservation()
    {
        var state=QueueRulesTests.State();var p=QueueRulesTests.Propose(state);state.Seerr.Revision++;
        QueueRules.CloseExternalChange(state,p.Id,p.Revision,p.Target.Fingerprint,new("RemovedOutsideQueue"){ConnectionRevision=state.Seerr.Revision},QueueRulesTests.Now);
        Assert.True(p.Terminal);Assert.Null(p.FilesDeletedAt);Assert.Null(p.Claim);
    }
    [Fact] public void FailureDiagnosticsAreAllowlistedNotExceptionMessages()
    {
        var state=QueueRulesTests.State();var p=QueueRulesTests.Propose(state);
        QueueRules.VerificationUnavailable(state,p,QueueRulesTests.Now,"SeerrServerAssociationUnverified");Assert.Equal("SeerrServerAssociationUnverified",p.LastVerificationFailureCode);
        QueueRules.VerificationUnavailable(state,p,QueueRulesTests.Now,"SECRET exception prose");Assert.Equal("VerificationUnavailable",p.LastVerificationFailureCode);
    }
    [Fact] public void OrdinaryProjectionDoesNotExposeAdministratorTargetDiagnostics()
    {
        var state=QueueRulesTests.State();var p=QueueRulesTests.Propose(state);
        p.LastCheckEvidence=new(true,false,true,true,true){TargetDifferences=[new("Sonarr/Radarr target","PRIVATE_OLD_ID","PRIVATE_NEW_ID")]};
        p.LastVerificationFailureCode="SeerrServerAssociationUnverified";
        var json=System.Text.Json.JsonSerializer.Serialize(QueueRules.Project(state,p,p.OwnerId,QueueRulesTests.Now));
        Assert.DoesNotContain("PRIVATE_",json);Assert.DoesNotContain("TargetDifferences",json);Assert.DoesNotContain("LastVerificationFailureCode",json);
    }
}
