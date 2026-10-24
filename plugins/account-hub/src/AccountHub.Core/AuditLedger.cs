using System.Globalization;
using System.Text.Json;
using AccountHub.Api.Contracts;
using AccountHub.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace AccountHub.Core;

public sealed record AuditAcceptance(string Status, Guid AuditId);
public sealed record ChainVerification(bool Valid, long? FirstMismatch, long CheckpointSequence, long HeadSequence);

public static class AuditLedger
{
    public const string Genesis = "0000000000000000000000000000000000000000000000000000000000000000";

    // Caller holds the store's transaction/write lock. Audit and mutation commit together.
    internal static async Task<AuditAcceptance> AppendAsync(AccountHubDbContext db, AuditEvent value,
        TimeProvider clock, CancellationToken ct, string? intakeHash = null)
    {
        EventValidation.Audit(value);
        var json = CanonicalJson.Serialize(value);
        var hash = intakeHash ?? CanonicalJson.Hash(json);
        var receipt = await db.Receipts.FindAsync([value.Source, value.EventId], ct);
        if (receipt is not null)
        {
            if (receipt.PayloadHash != hash) throw new HubException("rejected", "Event ID conflicts with its accepted payload");
            return new("duplicate", receipt.AuditId);
        }

        var state = await db.State.SingleAsync(ct);
        var row = new AuditRow
        {
            Id = Guid.NewGuid(), Sequence = state.HeadSequence + 1, EventJson = json,
            PreviousHash = state.HeadHash, RecordedAtUnixMs = clock.GetUtcNow().ToUnixTimeMilliseconds()
        };
        row.EntryHash = EntryHash(row);
        db.Audit.Add(row);
        state.HeadSequence = row.Sequence;
        state.HeadHash = row.EntryHash;
        db.Receipts.Add(new EventReceipt
        {
            Source = value.Source, EventId = value.EventId, PayloadHash = hash, AuditId = row.Id,
            AcceptedAtUnixMs = row.RecordedAtUnixMs
        });
        return new("accepted", row.Id);
    }

    internal static async Task<ChainVerification> VerifyAsync(AccountHubDbContext db, CancellationToken ct)
    {
        var state = await db.State.AsNoTracking().SingleAsync(ct);
        var sequence = state.CheckpointSequence;
        var hash = state.CheckpointHash;
        if (sequence < 0 || sequence > state.HeadSequence || (sequence == 0 && hash != Genesis))
            return new(false, sequence, state.CheckpointSequence, state.HeadSequence);
        await foreach (var row in db.Audit.AsNoTracking().OrderBy(x => x.Sequence).AsAsyncEnumerable().WithCancellation(ct))
        {
            if (row.Sequence != sequence + 1 || row.PreviousHash != hash || row.EntryHash != EntryHash(row))
                return new(false, row.Sequence, state.CheckpointSequence, state.HeadSequence);
            sequence = row.Sequence;
            hash = row.EntryHash;
        }
        return new(sequence == state.HeadSequence && hash == state.HeadHash,
            sequence == state.HeadSequence && hash == state.HeadHash ? null : sequence + 1,
            state.CheckpointSequence, state.HeadSequence);
    }

    internal static async Task<int> PruneAsync(AccountHubDbContext db, TimeProvider clock, Guid actor, CancellationToken ct)
    {
        var verification = await VerifyAsync(db, ct);
        if (!verification.Valid) throw new HubException("unavailable", "Audit integrity check failed");
        var state = await db.State.SingleAsync(ct);
        var cutoff = clock.GetUtcNow().AddDays(-state.AuditRetentionDays).ToUnixTimeMilliseconds();
        // Remove only a contiguous prefix, using host receipt time (not producer-controlled time).
        var firstRetained = await db.Audit.Where(x => x.RecordedAtUnixMs >= cutoff)
            .OrderBy(x => x.Sequence).Select(x => (long?)x.Sequence).FirstOrDefaultAsync(ct);
        var boundary = firstRetained.HasValue ? firstRetained.Value - 1 : state.HeadSequence;
        var anchor = await db.Audit.SingleOrDefaultAsync(x => x.Sequence == boundary, ct);
        if (anchor is null) return 0;
        var removed = await db.Audit.Where(x => x.Sequence <= boundary).ExecuteDeleteAsync(ct);
        state.CheckpointSequence = anchor.Sequence;
        state.CheckpointHash = anchor.EntryHash;
        await AppendAsync(db, new AuditEvent
        {
            EventId = Guid.NewGuid(), Source = "account-hub", EventType = "audit.pruned", ActorUserId = actor,
            OccurredAt = clock.GetUtcNow(), Metadata = JsonSerializer.SerializeToElement(new
            { removedCount = removed, checkpointSequence = boundary })
        }, clock, ct);
        return removed;
    }

    private static string EntryHash(AuditRow row) => CanonicalJson.Hash(string.Join('\n',
        row.Sequence.ToString(CultureInfo.InvariantCulture), row.Id.ToString("D"),
        row.RecordedAtUnixMs.ToString(CultureInfo.InvariantCulture), row.PreviousHash, row.EventJson));
}
