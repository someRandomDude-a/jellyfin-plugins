using DeletionQueue.Core;
using DeletionQueue.Infrastructure;

namespace Jellyfin.Plugin.DeletionQueue;

public sealed class SeerrExecutionGateway(QueueRuntime runtime, TargetResolver targets, LiveIdentity identity,
    ActivityInventory inventory, ActivityFence fence, TimeProvider clock, ISeerrClientFactory seerrClients,
    AccountHub.Api.AutomaticRuleValidationClient? automaticRules = null) : IExecutionGateway
{
    public Task<ExternalTargetChange?> InspectExternalChangeAsync(Proposal proposal,CancellationToken ct)=>targets.InspectExternalChangeAsync(proposal,ct);
    public async Task<ExecutionPreflight> PreflightAsync(Proposal proposal, CancellationToken ct)
    {
        var observedGeneration = fence.Generation;
        var owner = identity.Account(proposal.OwnerId);
        var metadata=proposal.RequesterId is not null?new Dictionary<string,object?>():null;
        var resolved = await targets.ResolveAsync(owner, proposal.Target.RequestId is { } requestId ? new TargetSelection(null, requestId, proposal.Target.Is4K) : new TargetSelection(proposal.Target.JellyfinItemId, null, proposal.Target.Is4K), ct,metadata);
        var requesterCurrent=proposal.RequesterId is not {} expected || metadata?.GetValueOrDefault("seerr.requesterId") is string mapped && Guid.TryParse(mapped,out var currentRequester)&&currentRequester==expected;
        var state = runtime.Read();
        if (state.Seerr.Revision != resolved.ConfigurationRevision) throw new QueueException("RevisionChanged");
        var authority = QueueRules.Authority(state, owner.Id);
        var authorized = owner.Enabled && AuthorizerCurrent(state, proposal) && requesterCurrent && targets.Accessible(owner.Id, resolved) &&
            (authority == ProposalAuthority.AnyAccessibleItem || authority == ProposalAuthority.OwnRequests && resolved.OwnRequestVerified);
        var scan = fence.Quiet && inventory.Scan(resolved) && fence.Quiet && observedGeneration == fence.Generation && runtime.CollectorHealthy;
        var latest = runtime.Read();
        scan = scan && latest.Policy.Revision == state.Policy.Revision;
        scan = scan && latest.Preferences.Count == state.Preferences.Count && state.Preferences.All(pair => latest.Preferences.TryGetValue(pair.Key, out var pref) && pref.Revision == pair.Value.Revision);
        var current = QueueRules.Find(latest, proposal.Id);
        var ruleVerified = await AutomaticRuleMatchesAsync(current, ct);
        var creatorAuthorized=!current.CreatorAutoApprovalDisabled&&current.AdministratorRequestedBy==current.OwnerId&&!current.RequiresAutomaticRuleValidation&&identity.Account(current.OwnerId) is {Enabled:true,Administrator:true}&&targets.Accessible(current.OwnerId,current.Target);
        scan = scan && fence.Quiet && observedGeneration == fence.Generation;
        if (runtime.Read().Seerr.Revision != resolved.ConfigurationRevision) throw new QueueException("RevisionChanged");
        var differences = MediaIdentity.Differences(proposal.Target, resolved);
        return new(new EvaluationEvidence(authorized, differences.Count == 0, scan, runtime.Available, true,
            AdministratorAuthorized(latest, current)||creatorAuthorized) { TargetDifferences = differences, AutomaticRuleVerified = ruleVerified,AdministratorCreatorAuthorized=creatorAuthorized, NativeHistoryVerified = scan && latest.CoverageHealthy && latest.LastHeartbeat >= clock.GetUtcNow().AddMinutes(-2) }, current.Revision){ActivityGeneration=observedGeneration};
    }

    public Task<bool> AnchorClaimAsync(ExecutionClaim claim, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // Write returned only after the authenticated DB transaction and local head seal were persisted.
        return Task.FromResult(claim.ActivityGeneration is {} generation&&generation==fence.Generation&&fence.Quiet&&runtime.CollectorHealthy&&runtime.Read().Proposals.Any(p => p.Claim == claim && p.Status == ProposalStatus.Claimed));
    }

    public async Task<bool> RecheckBeforeDispatchAsync(Proposal proposal, CancellationToken ct)
    {
        if (proposal.Claim?.ActivityGeneration is not {} observedGeneration || !fence.Quiet || observedGeneration != fence.Generation || !runtime.CollectorHealthy) return false;
        var current = await PreflightAsync(proposal, ct);
        var state = runtime.Read(); var p = QueueRules.Find(state, proposal.Id);
        // Evaluate a detached copy as pre-claim, without making a durable claim reusable.
        p.Claim = null;
        QueueRules.Evaluate(state, p, current.Evidence, clock.GetUtcNow());
        return p.Status == ProposalStatus.Ready && state.Policy.AutomaticDeletion && state.Policy.SeerrManagedDeletionAcknowledged &&
            current.ProposalRevision == proposal.Revision && current.ActivityGeneration==observedGeneration && fence.Quiet && observedGeneration == fence.Generation;
    }

    public async Task DeleteOnceAsync(Proposal proposal, CancellationToken ct)
    {
        var observedGeneration=proposal.Claim?.ActivityGeneration??throw new QueueException("DispatchVerificationFailed");
        if (!await AutomaticRuleMatchesAsync(proposal, ct)) throw new QueueException("AutomaticRuleVerificationRequired");
        if (!fence.Quiet || observedGeneration != fence.Generation) throw new QueueException("ActivityChangedBeforeDispatch");
        var state = runtime.Read(); var p = QueueRules.Find(state, proposal.Id);
        var owner = identity.Account(p.OwnerId);
        var authority = QueueRules.Authority(state, owner.Id);
        if (!owner.Enabled || !AuthorizerCurrent(state, p) || !targets.Accessible(owner.Id, p.Target) || authority == ProposalAuthority.Denied ||
            authority == ProposalAuthority.OwnRequests && !p.Target.OwnRequestVerified) throw new QueueException("OwnerNotAuthorized");
        if (p.Claim?.Id != proposal.Claim?.Id || p.Status != ProposalStatus.Claimed || !state.Policy.AutomaticDeletion ||
            !state.Policy.SeerrManagedDeletionAcknowledged || state.Policy.Suspended) throw new QueueException("DispatchVerificationFailed");
        var resolved = await targets.ResolveAsync(owner, p.Target.RequestId is {} selectedRequest ? new(null, selectedRequest, p.Target.Is4K) : new(p.Target.JellyfinItemId, null, p.Target.Is4K), ct);
        if (MediaIdentity.Differences(p.Target, resolved).Count != 0 || resolved.ConfigurationRevision != state.Seerr.Revision) throw new QueueException("UpstreamTargetChanged");
        using var seerr = seerrClients.Create(state.Seerr);
        if (state.Policy.RequireAdministratorApproval && !AdministratorAuthorized(state, p)) throw new QueueException("AdministratorApprovalRequired");
        if (!fence.Quiet || observedGeneration != fence.Generation || !runtime.CollectorHealthy) throw new QueueException("ActivityChangedBeforeDispatch");
        void RequireCurrentDispatch() {
            var live=runtime.Read();var current=QueueRules.Find(live,p.Id);var account=identity.Account(current.OwnerId);
            var allowed=QueueRules.Authority(live,account.Id);
            if(!runtime.CollectorHealthy||!fence.Quiet||fence.Generation!=observedGeneration||current.Status!=ProposalStatus.Claimed||current.Claim?.Id!=p.Claim?.Id||current.Revision!=p.Revision||
                live.Preferences.Count!=state.Preferences.Count||state.Preferences.Any(pair=>!live.Preferences.TryGetValue(pair.Key,out var pref)||pref.Revision!=pair.Value.Revision)||
                live.Policy.Revision!=state.Policy.Revision||live.Seerr.Revision!=state.Seerr.Revision||!live.Policy.AutomaticDeletion||!live.Policy.SeerrManagedDeletionAcknowledged||live.Policy.Suspended||
                !account.Enabled||!AuthorizerCurrent(live,current)||!targets.Accessible(account.Id,current.Target)||allowed==ProposalAuthority.Denied||
                allowed==ProposalAuthority.OwnRequests&&!current.Target.OwnRequestVerified||live.Policy.RequireAdministratorApproval&&!AdministratorAuthorized(live,current))throw new QueueException("DispatchVerificationFailed");
        }
        if (p.Target.RequestOnly && p.Target.RequestId is { } requestId) {
            var request=await seerr.RequestAsync(requestId,ct);
            if(!request.TryGetProperty("id",out var id)||!id.TryGetInt32(out var actualId)||actualId!=requestId||
                !request.TryGetProperty("status",out var requestStatus)||!requestStatus.TryGetInt32(out var currentStatus)||currentStatus!=1||
                !request.TryGetProperty("is4k",out var quality)||quality.ValueKind is not (System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)||quality.GetBoolean()!=p.Target.Is4K||
                !request.TryGetProperty("media",out var media)||!media.TryGetProperty("id",out var mediaId)||!mediaId.TryGetInt32(out var actualMediaId)||actualMediaId!=p.Target.SeerrMediaId)throw new QueueException("UpstreamTargetChanged");
            RequireCurrentDispatch();await seerr.DeclineRequestOnceAsync(requestId, ct);
        } else {
            var record=await seerr.MediaRecordAsync(p.Target.SeerrMediaId,ct);
            if(!TargetResolver.MatchesMediaRecord(p.Target,record)||!record.TryGetProperty(p.Target.Is4K?"status4k":"status",out var availability)||
                !availability.TryGetInt32(out var status)||status is not (4 or 5))throw new QueueException("UpstreamTargetChanged");
            RequireCurrentDispatch();await seerr.DeleteFilesOnceAsync(p.Target.SeerrMediaId, p.Target.Is4K, ct);
        }
    }

    private Task<bool> AutomaticRuleMatchesAsync(Proposal proposal, CancellationToken ct) => !proposal.RequiresAutomaticRuleValidation
        ? Task.FromResult(true) : automaticRules is null || string.IsNullOrEmpty(proposal.AutomaticRuleId)
            ? Task.FromResult(false) : automaticRules.MatchesAsync(new(proposal.AutomaticAuthorizerId??proposal.OwnerId, proposal.Target.JellyfinItemId, proposal.AutomaticRuleId), ct);

    private bool AuthorizerCurrent(QueueState state, Proposal p)=>p.AutomaticAuthorizerId is not {} id ||
        identity.Account(id) is {Enabled:true,Administrator:true} && QueueRules.Authority(state,id) != ProposalAuthority.Denied;

    private bool AdministratorAuthorized(QueueState state, Proposal proposal)
    {
        if (!QueueRules.AdministratorApprovalCurrent(state, proposal) || proposal.AdministratorApproval is not { } approval) return false;
        var account = identity.Account(approval.UserId);
        return account.Enabled && account.Administrator && (targets.Accessible(account.Id, proposal.Target) || proposal.Target.RequestId > 0);
    }

    public async Task<DeletionProof?> InspectOutcomeAsync(Proposal proposal, CancellationToken ct)
    {
        var state = runtime.Read();
        if (proposal.Claim is null) return null;
        using var seerr = seerrClients.Create(state.Seerr);
        await targets.VerifyServerAssociationAsync(seerr, state.Seerr, ct);
        if (proposal.Target.RequestOnly && proposal.Target.RequestId is { } requestId) {
            var request = await seerr.RequestAsync(requestId, ct);
            if (runtime.Read().Seerr.Revision == state.Seerr.Revision && request.TryGetProperty("id", out var requestIdentity) && requestIdentity.GetInt32() == requestId && request.TryGetProperty("status", out var requestStatus) && requestStatus.GetInt32() == 3 &&
                request.TryGetProperty("is4k", out var quality) && quality.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False && quality.GetBoolean() == proposal.Target.Is4K &&
                request.TryGetProperty("media", out var requestMedia) && TargetResolver.MatchesMediaRecord(proposal.Target, requestMedia))
                return new(proposal.Target.Fingerprint, "Seerr reports the pending request declined; request history retained.");
            return null;
        }
        var media = await seerr.MediaRecordAsync(proposal.Target.SeerrMediaId, ct);
        if (runtime.Read().Seerr.Revision != state.Seerr.Revision) return null;
        if (!TargetResolver.MatchesMediaRecord(proposal.Target,media)) return null;
        if (!media.TryGetProperty("status" + (proposal.Target.Is4K ? "4k" : ""), out var status) || !status.TryGetInt32(out var code) || code != 7) return null;
        return new(proposal.Target.Fingerprint, "Trusted Seerr reports DELETED for the claimed media/quality; Arr record and file removal delegated to Seerr. Request history retained.");
    }
}
