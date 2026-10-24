using System.Text.Json;
using AccountHub.Core.Data;
using AccountHub.Api.Contracts;
using Microsoft.EntityFrameworkCore;

namespace AccountHub.Core;

/// <summary>Durable non-secret claim before an external side effect. Interrupted claims never blindly replay.</summary>
public sealed class ExternalCommandGate(HubStore store, TimeProvider clock)
{
    private readonly SemaphoreSlim lease = new(1, 1);
    public async Task<JsonElement> ExecuteAsync(Guid actor, string operation, Guid commandId, long revision, object intent,
        Func<HubState, CancellationToken, Task> authorize,
        Func<CancellationToken, Task<JsonElement>> execute,
        Func<HubState, CancellationToken, Task>? complete = null, CancellationToken ct = default,
        Func<JsonElement>? auditMetadata = null)
    {
        if (actor == Guid.Empty || commandId == Guid.Empty) throw new HubException("invalid_request", "Actor and command ID required");
        var hash = store.SecretFingerprint(operation, CanonicalJson.Serialize(new { revision, intent }));
        await lease.WaitAsync(ct);
        try
        {
            var old = await store.ReadAsync(async (db, token) => await db.Commands.AsNoTracking().SingleOrDefaultAsync(x => x.ActorUserId == actor && x.Operation == operation && x.CommandId == commandId, token), ct);
            if (old is not null)
            {
                if (old.PayloadHash != hash) throw new HubException("command_conflict", "Command input changed");
                return JsonSerializer.Deserialize<JsonElement>(old.ResultJson);
            }
            var state = await store.ReadAsync((db, token) => db.State.AsNoTracking().SingleAsync(token), ct);
            if (state.Revision != revision) throw new HubException("revision_changed", "Reload current revision");
            try { await authorize(state, ct); }
            catch
            {
                await store.AppendAuditAsync(new AuditEvent { EventId = Guid.NewGuid(), Source = "account-hub", EventType = operation + ".denied", Outcome = "denied", ActorUserId = actor, CorrelationId = commandId, OccurredAt = clock.GetUtcNow() }, CancellationToken.None);
                throw;
            }
            var pending = JsonSerializer.SerializeToElement(new { status = "unknown", remediationPending = true, detail = "An interrupted external action must be inspected, not retried." });
            await store.WriteAsync(async (db, token) =>
            {
                var current = await db.State.SingleAsync(token);
                if (current.Revision != revision) throw new HubException("revision_changed", "Reload current revision");
                db.Commands.Add(new CommandResult { ActorUserId = actor, Operation = operation, CommandId = commandId, PayloadHash = hash, ResultJson = pending.GetRawText(), CompletedAtUnixMs = clock.GetUtcNow().ToUnixTimeMilliseconds() });
                await AuditLedger.AppendAsync(db, new AuditEvent { EventId = Guid.NewGuid(), Source = "account-hub", EventType = operation + ".intent", ActorUserId = actor, CorrelationId = commandId, OccurredAt = clock.GetUtcNow(), Metadata = auditMetadata?.Invoke() ?? JsonSerializer.SerializeToElement(new { }) }, clock, token);
                current.Revision++;
                return true;
            }, ct);
            JsonElement result;
            try { result = await execute(ct); }
            catch
            {
                // The non-replayable claim remains uncertain, including cancellation after send.
                await store.AppendAuditAsync(new AuditEvent { EventId = Guid.NewGuid(), Source = "account-hub", EventType = operation + ".uncertain", Outcome = "failure", ActorUserId = actor, CorrelationId = commandId, OccurredAt = clock.GetUtcNow() }, CancellationToken.None);
                throw;
            }
            // Persist completion even when the initiating client went away after an external commit.
            await store.WriteAsync(async (db, token) =>
            {
                var current = await db.State.SingleAsync(token);
                if (complete is not null) await complete(current, token);
                var receipt = await db.Commands.FindAsync([actor, operation, commandId], token);
                receipt!.ResultJson = result.GetRawText();
                var denied = result.ValueKind == JsonValueKind.Object && result.TryGetProperty("failureCode", out _);
                var uncertain = result.ValueKind == JsonValueKind.Object && result.TryGetProperty("remediationPending", out var remediation) && remediation.ValueKind == JsonValueKind.True;
                await AuditLedger.AppendAsync(db, new AuditEvent { EventId = Guid.NewGuid(), Source = "account-hub", EventType = operation + (denied ? ".denied" : ".completed"), Outcome = denied ? "denied" : uncertain ? "failure" : "success", ActorUserId = actor, CorrelationId = commandId, OccurredAt = clock.GetUtcNow(), Metadata = auditMetadata?.Invoke() ?? JsonSerializer.SerializeToElement(new { }) }, clock, token);
                return true;
            }, CancellationToken.None);
            return result;
        }
        finally { lease.Release(); }
    }
}
