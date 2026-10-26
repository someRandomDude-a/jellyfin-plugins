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
        try { target = await targets.ResolveAsync(actor, new(request.ItemId, null, false), ct); }
        catch (QueueException e) when (e.Code is "TargetEditionUnverified" or "SeerrTargetNotManaged") {
            target = await targets.ResolveAsync(actor, new(request.ItemId, null, true), ct);
        }
        if (request.PreviewOnly) return Contract.Serialize(new Dictionary<string, object> {
            ["seerr.ownRequest"] = target.OwnRequestVerified, ["seerr.is4k"] = target.Is4K, ["seerr.requestOnly"] = target.RequestOnly
        });
        var id = runtime.Write(request.CommandId, "automatic-rule:" + request.RuleId, "AutomaticRemovalRequested", state => {
            actor = identity.Account(request.ActorId);
            if (!actor.Enabled || !actor.Administrator || !notifications.Ready) throw new QueueException("AccessDenied");
            if (state.Seerr.Revision != target.ConfigurationRevision) throw new QueueException("RevisionChanged");
            QueueRules.RequireProposalAuthority(state, actor, target, targets.Accessible(actor.Id, target));
            var prior = state.Proposals.Select(p => p.Id).ToHashSet();
            var proposal = QueueRules.ProposeAutomatically(state, actor, target, clock.GetUtcNow());
            if (!prior.Contains(proposal.Id)) proposal.AutomaticRuleId = request.RuleId;
            return proposal.Id;
        });
        return Contract.Serialize(new { proposalId = id });
    }
}
