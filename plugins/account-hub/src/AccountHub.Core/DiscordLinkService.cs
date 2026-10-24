using System.Security.Cryptography;
using System.Text;
using AccountHub.Core.Data;
using AccountHub.Api.Contracts;
using Microsoft.EntityFrameworkCore;

namespace AccountHub.Core;

public sealed class DiscordLinkService(HubStore store, TimeProvider clock)
{
    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    public Task<DiscordChallenge?> LookupAsync(string token, CancellationToken ct) => store.ReadAsync(async (db, cancellation) =>
    {
        var challenge = await db.DiscordChallenges.AsNoTracking().SingleOrDefaultAsync(c => c.TokenHash == Hash(token), cancellation);
        return challenge is { ConsumedBy: null } && challenge.ExpiresAtUnixMs > clock.GetUtcNow().ToUnixTimeMilliseconds() ? challenge : null;
    }, ct);
    public async Task<string> PrepareAsync(string discordId, CancellationToken ct)
    {
        if (discordId.Length is < 1 or > 20 || !ulong.TryParse(discordId, out var id) || id == 0) throw new HubException("invalid_request", "Invalid Discord ID");
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        await store.WriteAsync(async (db, cancellation) =>
        {
            var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
            await db.DiscordChallenges.Where(x => x.ExpiresAtUnixMs < now).ExecuteDeleteAsync(cancellation);
            if (await db.DiscordChallenges.CountAsync(cancellation) >= 10000) throw new HubException("unavailable", "Link challenge capacity reached", true);
            await db.DiscordChallenges.Where(x => x.DiscordId == discordId).ExecuteDeleteAsync(cancellation);
            db.DiscordChallenges.Add(new() { TokenHash = Hash(token), DiscordId = discordId, ExpiresAtUnixMs = now + 300000 });
            return true;
        }, ct);
        return token;
    }
    public Task ConfirmAsync(Guid actor, string token, CancellationToken ct) => store.WriteAsync(async (db, cancellation) =>
    {
        var challenge = await db.DiscordChallenges.FindAsync([Hash(token)], cancellation);
        if (challenge is null || challenge.ConsumedBy is not null || challenge.ExpiresAtUnixMs <= clock.GetUtcNow().ToUnixTimeMilliseconds()) throw new HubException("rejected", "Link expired or consumed");
        var userId = actor.ToString("N"); var discord = challenge.DiscordId; var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        // No silent reassignment: unique constraints reject an identity already owned by someone else.
        try { await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO linked_accounts(JellyfinUserId,DiscordUserId,DiscordUsername,LinkedAt) VALUES({userId},{discord},{discord},{now})", cancellation); }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 19) { throw new HubException("rejected", "A Discord or Jellyfin identity is already linked; unlink it explicitly first."); }
        challenge.ConsumedBy = actor;
        await AuditLedger.AppendAsync(db, new AuditEvent { EventId = Guid.NewGuid(), Source = "account-hub", EventType = "discord.linked", ActorUserId = actor, SubjectType = "user", SubjectId = actor.ToString("D"), OccurredAt = clock.GetUtcNow() }, clock, cancellation);
        return true;
    }, ct);
}
