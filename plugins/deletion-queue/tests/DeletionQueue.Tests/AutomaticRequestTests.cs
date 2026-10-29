using DeletionQueue.Core;
using static DeletionQueue.Tests.QueueRulesTests;

namespace DeletionQueue.Tests;

public sealed class AutomaticRequestTests
{
    [Fact] public void AutomationNeverVotesOrApprovesAndRepeatedMatchesDoNotRenew()
    {
        var state = State(); var target = Propose(state).Target; state.Proposals.Clear();
        state.Policy.RequireAdministratorApproval = true;
        var request = QueueRules.ProposeAutomatically(state, Owner with { Administrator = true }, target, Now);
        Assert.Empty(request.Consents); Assert.Null(request.AdministratorApproval);
        var revision = request.Revision;
        Assert.Same(request, QueueRules.ProposeAutomatically(state, Owner with { Administrator = true }, target, Now.AddDays(1)));
        Assert.Equal(Now, request.CreatedAt); Assert.Equal(revision, request.Revision);
    }
    [Fact] public void AutomationRespectsUndoInsteadOfRecreatingTheRequest()
    {
        var state = State(); var p = Propose(state);
        QueueRules.Undo(state, p.Id, Owner.Id, p.Revision, Now);
        Assert.Equal("PriorRemovalDeclined", Assert.Throws<QueueException>(() => QueueRules.ProposeAutomatically(state, Owner, p.Target, Now)).Code);
        Assert.Single(state.Proposals);
    }
    [Fact] public void AutomaticConditionsAreRequiredAtEveryEvaluationAndClaimEvenAfterTheDate() {
        var state = State(); var request = QueueRules.ProposeAutomatically(state, Owner, Target, Now);
        state.Policy.AutomaticDeletion = true; state.Policy.SeerrManagedDeletionAcknowledged = true;
        Evaluate(state, request); Assert.Equal("AutomaticRuleVerificationRequired", request.Reason);
        Evaluate(state, request, evidence: Good with { AutomaticRuleVerified = true }); Assert.Equal(ProposalStatus.Waiting, request.Status);
        var due = Now.AddHours(49); Evaluate(state, request, due, Good with { AutomaticRuleVerified = true }); Assert.Equal(ProposalStatus.Ready, request.Status);
        Evaluate(state, request, due); Assert.Equal(ProposalStatus.Blocked, request.Status); Assert.Null(request.EligibleSince);
        Assert.Throws<QueueException>(() => QueueRules.Claim(state, request.Id, request.Revision, Good, due)); Assert.Null(request.Claim);
        Evaluate(state, request, due, Good with { AutomaticRuleVerified = true });
        Assert.Equal(ProposalStatus.Waiting, request.Status); Assert.True(request.NotBefore > due);
    }
    [Fact] public void FreshPlaybackStillBlocksAnAutomaticRuleThatContinuesToMatch() {
        var state = State(); var request = QueueRules.ProposeAutomatically(state, Owner, Target, Now);
        Evaluate(state, request, evidence: Good with { AutomaticRuleVerified = true });
        QueueRules.RecordActivity(state, Viewer.Id, request.Target.ActivityKey, null, false, Now.AddHours(49));
        Evaluate(state, request, Now.AddHours(49), Good with { AutomaticRuleVerified = true });
        Assert.Equal("ConsentRequired", request.Reason); Assert.Equal(ProposalStatus.Blocked, request.Status);
    }
    [Fact] public void DeniedProposalPermissionDoesNotRemoveAViewersConsentProtectionOrGrantApproval()
    {
        var state = State(); state.Policy.RequireAdministratorApproval = true;
        state.Policy.Accounts[Viewer.Id] = ProposalAuthority.Denied;
        var request = QueueRules.ProposeAutomatically(state, Owner, Target, Now);
        QueueRules.RecordActivity(state, Viewer.Id, request.Target.ActivityKey, null, false, Now);
        QueueRules.Consent(state, request.Id, Viewer, request.Revision, QueueRules.Snapshot(state, request.Target, Viewer.Id), Answer.No, true, Now);
        Assert.Throws<QueueException>(() => QueueRules.AdministratorDecision(state, request.Id, Viewer, request.Revision, QueueRules.AdministratorSnapshot(state, request), true, true, Now));
        QueueRules.AdministratorDecision(state, request.Id, Owner with { Administrator = true }, request.Revision, QueueRules.AdministratorSnapshot(state, request), true, true, Now);
        var evidence = Good with { AutomaticRuleVerified = true, AdministratorAuthorized = true };
        Evaluate(state, request, evidence: evidence); Assert.Equal("ConsentRequired", request.Reason);
        QueueRules.Consent(state, request.Id, Viewer, request.Revision, QueueRules.Snapshot(state, request.Target, Viewer.Id), Answer.Yes, true, Now);
        Evaluate(state, request, evidence: evidence); Assert.Equal(ProposalStatus.Waiting, request.Status); Assert.Null(request.Claim);
    }
}
