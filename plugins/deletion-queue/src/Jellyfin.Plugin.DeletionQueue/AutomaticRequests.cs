using System.Text.Json;
using AccountHub.Api;
using DeletionQueue.Core;

namespace Jellyfin.Plugin.DeletionQueue;

public sealed class AutomaticRequests(QueueRuntime runtime, TargetResolver targets, LiveIdentity identity, HubAvailability notifications, TimeProvider clock)
{
    public async Task<string> SubmitAsync(string json, CancellationToken ct)
    {
        if (json.Length > 4096) throw new QueueException("InvalidRequest");
        var request = JsonSerializer.Deserialize<AutomaticRemovalSubmission>(json, Contract.Json) ?? throw new QueueException("InvalidRequest");
        if (request.CommandId == Guid.Empty || request.ItemId == Guid.Empty || request.RuleId is not { Length: > 0 and <= 100 }) throw new QueueException("InvalidRequest");
        var actor = identity.Account(request.ActorId);
        if (!actor.Enabled || !actor.Administrator) throw new QueueException("AccessDenied");
        if (!request.PreviewOnly && !notifications.Ready) throw new QueueException("NotificationsRequired");
        VerifiedTarget target;
        var metadata = new Dictionary<string, object?>();
        try { target = await targets.ResolveAsync(actor, new(request.ItemId, null, false), ct, metadata); }
        catch (QueueException e) when (e.Code is "TargetEditionUnverified" or "SeerrTargetNotManaged") {
            target = await targets.ResolveAsync(actor, new(request.ItemId, null, true), ct, metadata);
        }
        if (request.PreviewOnly) return Contract.Serialize(metadata!);
        Guid? requesterId=metadata.TryGetValue("seerr.requesterId",out var mapped)&&mapped is string value&&Guid.TryParse(value,out var verified)?verified:null;
        var owner=actor;
        if(requesterId is {} known) {
            owner=identity.Account(known);
            if(!owner.Enabled)throw new QueueException("OwnerNotAuthorized");
            // Re-resolve under the real requester's current native visibility and
            // own-request permissions; the administrator is not impersonated.
            target=await targets.ResolveAsync(owner,new(request.ItemId,null,target.Is4K),ct);
        }
        var id = runtime.Write(request.CommandId, "automatic-rule:" + request.RuleId, "AutomaticRemovalRequested", state => {
            actor = identity.Account(request.ActorId);
            if (!actor.Enabled || !actor.Administrator || !notifications.Ready) throw new QueueException("AccessDenied");
            if (state.Seerr.Revision != target.ConfigurationRevision) throw new QueueException("RevisionChanged");
            if(!targets.Accessible(actor.Id,target))throw new QueueException("AccessDenied");
            owner=identity.Account(requesterId??request.ActorId);
            QueueRules.RequireProposalAuthority(state,owner,target,targets.Accessible(owner.Id,target));
            var prior = state.Proposals.Select(p => p.Id).ToHashSet();
            var proposal = QueueRules.ProposeAutomatically(state, owner, target, clock.GetUtcNow());
            if (!prior.Contains(proposal.Id)) {proposal.AutomaticRuleId = request.RuleId;proposal.AutomaticAuthorizerId=actor.Id;proposal.RequesterId=requesterId;}
            return proposal.Id;
        });
        return Contract.Serialize(new { proposalId = id });
    }
}
