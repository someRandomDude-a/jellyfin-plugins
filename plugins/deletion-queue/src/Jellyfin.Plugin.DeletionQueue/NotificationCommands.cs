using System.Text.Json;
using AccountHub.Api;
using DeletionQueue.Core;

namespace Jellyfin.Plugin.DeletionQueue;

// Live caller-specific controls. Never put a recipient's vote or snapshot in the shared event.
public sealed class NotificationCommands(QueueRuntime runtime, TargetResolver targets, LiveIdentity identity, TimeProvider clock)
{
    public Task<string> DispatchAsync(Guid user, string eventJson, string? commandJson, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var entry = JsonSerializer.Deserialize<NotificationEvent>(eventJson, Contract.Json)!;
        if (!Guid.TryParse(entry.Metadata?.GetValueOrDefault("proposalId"), out var id)) throw new QueueException("NotFound");
        var caller = identity.Account(user);
        if (!caller.Enabled) throw new QueueException("AccessDenied");
        var state = runtime.Read(); var p = QueueRules.Find(state, id);
        if (!targets.Accessible(user, p)) throw new QueueException("NotFound");
        if (commandJson is not null) {
            var command = JsonSerializer.Deserialize<NotificationCommandRequest>(commandJson, Contract.Json)!;
            runtime.Write(command.CommandId, user.ToString(), "NotificationAction", current => {
                var proposal = QueueRules.Find(current, id);
                var account = identity.Account(user);
                if (!account.Enabled || !targets.Accessible(user, proposal.Target)) throw new QueueException("NotFound");
                if (command.Id == "undo") QueueRules.Undo(current, id, user, command.Revision, clock.GetUtcNow());
                else if (command.Id is "allow" or "keep") QueueRules.Consent(current, id, account, command.Revision, command.Snapshot, command.Id == "allow" ? Answer.Yes : Answer.No, true, clock.GetUtcNow());
                else throw new QueueException("InvalidAction");
                return true;
            });
            state = runtime.Read(); p = QueueRules.Find(state, id);
        }
        var projection = QueueRules.Project(state, p, user, clock.GetUtcNow());
        if (p.Terminal || p.Claim is not null) return Task.FromResult("[]");
        var commands = new List<NotificationCommand> {
            new("allow", "Allow", projection.Revision, projection.OwnSnapshot) { Selected = projection.OwnAnswer == Answer.Yes },
            new("keep", "Keep", projection.Revision, projection.OwnSnapshot) { Selected = projection.OwnAnswer == Answer.No }
        };
        if (entry.Type == "removal.playback_reset") commands.Clear();
        if (projection.CanUndo) commands.Add(new("undo", entry.Type == "removal.playback_reset" ? "Cancel request" : "Undo", projection.Revision, projection.OwnSnapshot));
        return Task.FromResult(Contract.Serialize(commands));
    }
}
