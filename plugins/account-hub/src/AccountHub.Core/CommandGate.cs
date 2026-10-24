using System.Text.Json;
using AccountHub.Api.Contracts;
using AccountHub.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace AccountHub.Core;

public sealed class CommandGate(HubStore store, TimeProvider clock)
{
    private readonly SemaphoreSlim lease = new(1, 1);
    /// <summary>
    /// Only same-database mutations. External actions require their own durable saga, not this callback.
    /// Payload must be a typed non-secret intent: never include password, token or step-up proof.
    /// Authorization callback is request-local; completed retries do not consume fresh proof.
    /// </summary>
    internal async Task<JsonElement> ExecuteAsync<T>(Guid actor, string operation, Guid commandId, long revision,
        T intent, Func<CancellationToken, Task> authorize,
        Func<AccountHubDbContext, CancellationToken, Task<JsonElement>> mutate, CancellationToken ct = default)
    {
        if (actor == Guid.Empty || commandId == Guid.Empty || operation.Length > 128)
            throw new HubException("invalid_request", "Actor, operation and command ID are required");
        var payloadHash = CanonicalJson.Hash(CanonicalJson.Serialize(new { revision, intent }));
        await lease.WaitAsync(ct);
        try
        {
            // Never call host authorization while holding the database writer: the proof gate
            // reads current Hub settings and calls Security. Recheck revision inside the commit.
            var existing = await store.ReadAsync((db, token) => db.Commands.AsNoTracking().SingleOrDefaultAsync(c => c.ActorUserId == actor && c.Operation == operation && c.CommandId == commandId, token), ct);
            if (existing is not null)
            {
                if (existing.PayloadHash != payloadHash)
                    throw new HubException("command_conflict", "Command ID was used with different input");
                return JsonSerializer.Deserialize<JsonElement>(existing.ResultJson, Wire.Options);
            }
            var before = await store.ReadAsync((db, token) => db.State.AsNoTracking().SingleAsync(token), ct);
            if (before.Revision != revision) throw new HubException("revision_changed", "Reload the current revision before retrying");
            await authorize(ct);
            return await store.WriteAsync(async (db, token) =>
            {
            var state = await db.State.SingleAsync(token);
            if (state.Revision != revision) throw new HubException("revision_changed", "Reload the current revision before retrying");
            var result = await mutate(db, token);
            state.Revision++;
            await AuditLedger.AppendAsync(db, new AuditEvent
            {
                EventId = Guid.NewGuid(), Source = "account-hub", EventType = operation,
                ActorUserId = actor, CorrelationId = commandId, OccurredAt = clock.GetUtcNow(),
                Metadata = JsonSerializer.SerializeToElement(new { revision = state.Revision })
            }, clock, token);
            db.Commands.Add(new CommandResult
            {
                ActorUserId = actor, Operation = operation, CommandId = commandId,
                PayloadHash = payloadHash, ResultJson = result.GetRawText(),
                CompletedAtUnixMs = clock.GetUtcNow().ToUnixTimeMilliseconds()
            });
            return result;
            }, ct);
        }
        finally { lease.Release(); }
    }
}
