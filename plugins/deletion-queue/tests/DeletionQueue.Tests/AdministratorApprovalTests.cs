using System.Text.Json;
using DeletionQueue.Core;

namespace DeletionQueue.Tests;

public sealed class AdministratorApprovalTests
{
    [Fact]public void AdministratorCreatorsAutoApproveCurrentPolicyButAutomationAndDemotionDoNot(){
        var s=QueueRulesTests.State();var target=QueueRulesTests.Target;
        var p=QueueRules.Propose(s,Administrator,target,true,QueueRules.Snapshot(s,target,Administrator.Id),QueueRulesTests.Now);
        Assert.Equal(Administrator.Id,p.AdministratorRequestedBy);QueueRules.UpdatePolicy(s,new Policy{RequireAdministratorApproval=true},s.Policy.Revision);
        QueueRules.Evaluate(s,p,Good with{AdministratorCreatorAuthorized=true},QueueRulesTests.Now);Assert.True(QueueRules.AdministratorApprovalCurrent(s,p));Assert.Equal(ProposalStatus.Waiting,p.Status);
        QueueRules.UpdatePolicy(s,new Policy{RequireAdministratorApproval=true},s.Policy.Revision);QueueRules.Evaluate(s,p,QueueRulesTests.Good,QueueRulesTests.Now);Assert.Equal("AdministratorApprovalRequired",p.Reason);
        var other=QueueRulesTests.State();other.Policy.RequireAdministratorApproval=true;var automatic=QueueRules.ProposeAutomatically(other,Administrator,target,QueueRulesTests.Now);
        QueueRules.Evaluate(other,automatic,Good with{AdministratorCreatorAuthorized=true,AutomaticRuleVerified=true},QueueRulesTests.Now);Assert.Null(automatic.AdministratorApproval);Assert.Null(automatic.AdministratorRequestedBy);Assert.Empty(automatic.Consents);
    }
    [Fact]public void ApprovalRemindersAreSnapshotScopedAndLinkToTheFocusedApprovalView(){
        var (s,p)=Pending();s.Schema=2;s.Hub.NotificationMode=NotificationMode.Auto;QueueRulesTests.Evaluate(s,p,evidence:Good);
        var old=new Dictionary<Guid,long>{{p.Id,-1}};HubIntegration.Capture(s,old,[],"test","EligibilityEvaluated",QueueRulesTests.Now,true);
        var reminders=s.Outbox.Where(e=>e.Kind=="notification"&&e.PayloadJson.Contains("proposal.approval_required")).ToArray();var reminder=Assert.Single(reminders);
        using var doc=JsonDocument.Parse(reminder.PayloadJson);Assert.True(doc.RootElement.GetProperty("adminOnly").GetBoolean());Assert.Empty(doc.RootElement.GetProperty("audience").EnumerateArray());Assert.Equal("#/home?deletionQueueApprovals=1",doc.RootElement.GetProperty("link").GetString());
        HubIntegration.Capture(s,old,[],"test","EligibilityEvaluated",QueueRulesTests.Now,true);Assert.Single(s.Outbox,e=>e.Kind=="notification"&&e.PayloadJson.Contains("proposal.approval_required"));
        Approve(s,p);HubIntegration.Capture(s,old,[],"test","AdministratorApprovalChanged",QueueRulesTests.Now,true);Assert.Null(p.LastApprovalNoticeSnapshot);Assert.Contains(s.Outbox,e=>e.Kind=="notification"&&e.PayloadJson.Contains("no longer awaiting administrator approval"));
    }
    private static readonly Account Administrator = new(Guid.NewGuid(), true, true);
    private static readonly EvaluationEvidence Good = QueueRulesTests.Good with { AdministratorAuthorized = true };
    private static (QueueState State, Proposal Proposal) Pending()
    {
        var s = QueueRulesTests.State(); s.Policy.RequireAdministratorApproval = true;
        return (s, QueueRulesTests.Propose(s));
    }
    private static void Approve(QueueState s, Proposal p, bool approved = true) => QueueRules.AdministratorDecision(s,
        p.Id, Administrator, p.Revision, QueueRules.AdministratorSnapshot(s, p), approved, true, QueueRulesTests.Now);

    [Fact] public void ApprovalIsOptionalAndDefaultsOff()
    {
        var s = QueueRulesTests.State(); Assert.False(s.Policy.RequireAdministratorApproval);
        var p = QueueRulesTests.Propose(s); QueueRulesTests.Evaluate(s, p);
        Assert.Equal(ProposalStatus.Waiting, p.Status); Assert.Null(p.AdministratorApproval);
    }
    [Fact]public void HistoryPresentationChangesReevaluateWithoutInvalidatingApprovalOrDelay(){
        var (s,p)=Pending();Approve(s,p);QueueRulesTests.Evaluate(s,p,evidence:Good);var since=p.EligibleSince;var deadline=p.NotBefore;
        QueueRules.UpdatePolicy(s,new Policy{RequireAdministratorApproval=true,RecentHistoryDays=14},s.Policy.Revision);
        Assert.True(QueueRules.AdministratorApprovalCurrent(s,p));Assert.Equal(since,p.EligibleSince);Assert.Equal(deadline,p.NotBefore);
    }
    [Fact] public void RequiredApprovalBlocksAndThenStartsTheFullDelay()
    {
        var (s, p) = Pending(); QueueRulesTests.Evaluate(s, p, evidence: Good);
        Assert.Equal("AdministratorApprovalRequired", p.Reason); Assert.Null(p.EligibleSince);
        Approve(s, p); QueueRulesTests.Evaluate(s, p, evidence: Good);
        Assert.Equal(QueueRulesTests.Now.AddHours(48), p.NotBefore);
    }
    [Theory] [InlineData(false, true, true)] [InlineData(true, false, true)] [InlineData(true, true, false)]
    public void OnlyAnEnabledVisibleAdministratorMayApprove(bool enabled, bool administrator, bool accessible)
    {
        var (s, p) = Pending(); var before = JsonSerializer.Serialize(s);
        Assert.Throws<QueueException>(() => QueueRules.AdministratorDecision(s, p.Id,
            new(Administrator.Id, enabled, administrator), p.Revision, QueueRules.AdministratorSnapshot(s, p), true, accessible, QueueRulesTests.Now));
        Assert.Equal(before, JsonSerializer.Serialize(s));
    }
    [Fact] public void ActivityAndPolicyChangesInvalidateApprovalWithoutAdvancingDeadlines()
    {
        var (s, p) = Pending(); Approve(s, p); QueueRulesTests.Evaluate(s, p, evidence: Good);
        var deadline = p.NotBefore;
        QueueRules.RecordActivity(s, QueueRulesTests.Viewer.Id, p.Target.ActivityKey, null, false, QueueRulesTests.Now);
        Assert.False(QueueRules.AdministratorApprovalCurrent(s, p)); Assert.Equal(deadline, p.NotBefore);
        Approve(s, p);
        QueueRules.UpdatePolicy(s, new Policy { RequireAdministratorApproval = true }, s.Policy.Revision);
        Assert.False(QueueRules.AdministratorApprovalCurrent(s, p)); Assert.Equal(deadline, p.NotBefore);
    }
    [Fact] public void ApprovalDoesNotSupplyViewerConsentOrSurviveDemotion()
    {
        var (s, p) = Pending();
        QueueRules.RecordActivity(s, QueueRulesTests.Viewer.Id, p.Target.ActivityKey, null, false, QueueRulesTests.Now);
        Approve(s, p); QueueRulesTests.Evaluate(s, p, evidence: Good);
        Assert.Equal("ConsentRequired", p.Reason); Assert.False(p.Consents.ContainsKey(Administrator.Id));
        QueueRules.Consent(s, p.Id, QueueRulesTests.Viewer, p.Revision, QueueRules.Snapshot(s, p.Target, QueueRulesTests.Viewer.Id), Answer.Yes, true, QueueRulesTests.Now);
        QueueRulesTests.Evaluate(s, p); Assert.Equal("AdministratorApprovalRequired", p.Reason);
    }
    [Fact] public void StaleDecisionsAndPostClaimDecisionsAreRejected()
    {
        var (s, p) = Pending(); var snapshot = QueueRules.AdministratorSnapshot(s, p);
        QueueRules.RecordActivity(s, Guid.NewGuid(), p.Target.ActivityKey, null, false, QueueRulesTests.Now.AddDays(-30));
        Assert.Throws<QueueException>(() => QueueRules.AdministratorDecision(s, p.Id, Administrator, p.Revision, snapshot, true, true, QueueRulesTests.Now));
        Approve(s, p); QueueRulesTests.Evaluate(s, p, evidence: Good);
        s.Policy.AutomaticDeletion = true; s.Policy.SeerrManagedDeletionAcknowledged = true;
        s.LastHeartbeat = QueueRulesTests.Now.AddHours(48);
        QueueRules.Claim(s, p.Id, p.Revision, Good, s.LastHeartbeat.Value);
        Assert.Throws<QueueException>(() => Approve(s, p, false));
    }
    [Fact] public void RevocationAndReapprovalRestartDelayAndKeepApprovalIdentitiesPrivate()
    {
        var (s, p) = Pending(); Approve(s, p); QueueRulesTests.Evaluate(s, p, evidence: Good);
        var deadline = p.NotBefore; Approve(s, p, false); Assert.Null(p.EligibleSince);
        Approve(s, p); QueueRulesTests.Evaluate(s, p, QueueRulesTests.Now.AddHours(1), Good);
        Assert.True(p.NotBefore >= deadline); Assert.Equal(QueueRulesTests.Now.AddHours(49), p.NotBefore);
        var json = JsonSerializer.Serialize(QueueRules.Project(s, p, QueueRulesTests.Owner.Id, QueueRulesTests.Now));
        Assert.DoesNotContain(Administrator.Id.ToString(), json); Assert.DoesNotContain("AdministratorApproval", json);
    }
    [Fact] public void DisablingRequirementAndResettingUsersNeverAdvanceDeadlineFloors()
    {
        var (s, p) = Pending(); Approve(s, p); QueueRulesTests.Evaluate(s, p, evidence: Good);
        var deadline = p.NotBefore;
        QueueRules.ResetUser(s, p.OwnerId, s.Policy.Revision, 0);
        Assert.True(s.Policy.RequireAdministratorApproval); Assert.Equal(deadline, p.NotBefore);
        QueueRules.UpdatePolicy(s, new Policy { RequireAdministratorApproval = false }, s.Policy.Revision);
        QueueRulesTests.Evaluate(s, p, QueueRulesTests.Now.AddHours(1));
        Assert.True(p.NotBefore >= deadline);
    }
}
