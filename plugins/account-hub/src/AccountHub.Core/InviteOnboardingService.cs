using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AccountHub.Api.Contracts;
using AccountHub.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;

namespace AccountHub.Core;

/// <summary>Adapter must create the reserved ID disabled atomically, never enabled/passwordless.</summary>
public interface IInviteNativeProvisioner
{
    Task CreateDisabledAsync(Guid userId, string username, CancellationToken ct);
    Task SetChosenPasswordAsync(Guid userId, string password, CancellationToken ct);
    Task ApplyPolicyAsync(Guid userId, string policyJson, string? groupId, CancellationToken ct);
    Task EnableAsync(Guid userId, CancellationToken ct);
    Task<bool> IsReadyAsync(Guid userId, CancellationToken ct);
    Task<bool> IsIncompleteAsync(Guid userId, string username, CancellationToken ct);
}

public sealed record InviteRequest(int ExpiresInMinutes = 10080, int MaxUses = 1, string Note = "", string? GroupId = null, string PolicyJson = "{}", string? DiscordUserId = null, bool CopyLink = true);
public sealed record InviteIssuance(JsonElement Result, string? Token);

/// <summary>Only non-secret intent is durable. A reservation is never returned to the pool after an uncertain effect.</summary>
public sealed class InviteOnboardingService(HubStore store, CommandGate commands, TimeProvider clock, IInviteNativeProvisioner native)
{
    private readonly SemaphoreSlim provisioning = new(1, 1);
    private readonly SemaphoreSlim finalization = new(1, 1);
    private long Now => clock.GetUtcNow().ToUnixTimeMilliseconds();
    public static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static void ValidateToken(string token)
    {
        if (token.Length != 43 || token.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))) throw Invalid();
    }
    private static HubException Invalid() => new("invalid_request", "Invalid invite input");
    public Task<JsonElement> CreateAsync(Guid actor, Guid commandId, long revision, InviteRequest request, Func<CancellationToken, Task> authorize, CancellationToken ct = default)
    {
        if (request.ExpiresInMinutes is < 15 or > 43200 || request.MaxUses is < 1 or > 100 || request.Note.Length > 500 || request.GroupId?.Length > 64 || request.PolicyJson.Length > 16384) throw Invalid();
        if (request.DiscordUserId is { } discord && (discord.Length is < 17 or > 20 || discord.Any(c => !char.IsAsciiDigit(c))) || request.DiscordUserId is null && !request.CopyLink) throw Invalid();
        return commands.ExecuteAsync(actor, "invite.created", commandId, revision, request, authorize, (db, _) =>
        {
            var invite = new OnboardingInvite { Id = Guid.NewGuid(), CreatedBy = actor, CreatedAt = Now, ExpiresAt = Now + request.ExpiresInMinutes * 60000L, MaxUses = request.MaxUses, Note = request.Note, GroupId = request.GroupId, PolicyJson = request.PolicyJson, DiscordUserId = request.DiscordUserId, CopyLink = request.CopyLink };
            db.OnboardingInvites.Add(invite);
            return Task.FromResult(JsonSerializer.SerializeToElement(new { id = invite.Id, status = invite.Status }));
        }, ct);
    }

    public async Task<InviteIssuance> ChangeAsync(Guid actor, Guid commandId, long revision, Guid id, string action, Func<CancellationToken, Task> authorize, CancellationToken ct = default)
    {
        // Revocation/rotation and the final enable decision have one ordered boundary.
        // Never hold a SQLite transaction open across the native account effect.
        await finalization.WaitAsync(ct);
        try { return await ChangeCoreAsync(actor, commandId, revision, id, action, authorize, ct); }
        finally { finalization.Release(); }
    }
    private async Task<InviteIssuance> ChangeCoreAsync(Guid actor, Guid commandId, long revision, Guid id, string action, Func<CancellationToken, Task> authorize, CancellationToken ct)
    {
        if (action is not ("approve" or "resend" or "revoke" or "expire")) throw Invalid();
        string? raw = null;
        var result = await commands.ExecuteAsync(actor, "invite." + action, commandId, revision, new { id }, authorize, async (db, token) =>
        {
            var invite = await db.OnboardingInvites.SingleOrDefaultAsync(x => x.Id == id, token) ?? throw Invalid();
            // Rotation/revocation also erase any old unsent capsule in the same commit.
            foreach (var old in await db.InviteSecretDeliveries.Where(x => x.InviteId == id && x.Ciphertext != null).ToArrayAsync(token)) { old.Ciphertext = null; old.State = "invalidated"; }
            if (action is "approve" or "resend")
            {
                if (invite.ExpiresAt <= Now || invite.ReservedUses >= invite.MaxUses || (action == "approve" ? invite.Status != "pending_approval" : invite.Status != "active")) throw new HubException("invite_unavailable", "Invite cannot be issued");
                raw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
                invite.TokenHash = Hash(raw); invite.Status = "active"; invite.ApprovedBy = actor;
                if (invite.DiscordUserId is not null) db.InviteSecretDeliveries.Add(new InviteSecretDelivery { Id = Guid.NewGuid(), InviteId = id, Destination = invite.DiscordUserId,
                    Ciphertext = store.Protection.CreateProtector("AccountHub.InviteDelivery.v1").Protect(raw), ExpiresAt = Math.Min(invite.ExpiresAt, Now + 300000) });
                if (!invite.CopyLink) raw = null;
            }
            else { invite.Status = action == "revoke" ? "revoked" : "expired"; invite.TokenHash = null; }
            // Never store the returned credential in command results. Completed retries do not disclose again.
            return JsonSerializer.SerializeToElement(new { id, status = invite.Status, credentialReadback = false });
        }, ct);
        return new(result, raw);
    }

    public Task<object> ListAsync(int skip, int limit, CancellationToken ct = default)
    {
        if (skip < 0 || limit is < 1 or > 100) throw Invalid();
        return store.ReadAsync<object>(async (db, token) => new { items = await db.OnboardingInvites.AsNoTracking().OrderByDescending(x => x.CreatedAt).Skip(skip).Take(limit)
            .Select(x => new { x.Id, x.Status, x.CreatedBy, x.ApprovedBy, x.CreatedAt, x.ExpiresAt, x.MaxUses, x.ReservedUses, x.CompletedUses, x.Note, x.GroupId }).ToArrayAsync(token) }, ct);
    }
    public Task<object> DetailAsync(Guid id, CancellationToken ct = default) => store.ReadAsync<object>(async (db, token) =>
    {
        var x = await db.OnboardingInvites.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, token) ?? throw Invalid();
        return new { x.Id, x.Status, x.ExpiresAt, x.MaxUses, x.ReservedUses, x.CompletedUses, x.Note, x.GroupId,
            deliveries = await db.InviteSecretDeliveries.AsNoTracking().Where(d => d.InviteId == id).Select(d => new { d.Id, d.State, d.ExpiresAt }).ToArrayAsync(token),
            reservations = await db.InviteProvisioning.AsNoTracking().Where(r => r.InviteId == id).Select(r => new { r.Id, r.UserId, r.Username, r.State, r.CreatedAt, r.UpdatedAt }).ToArrayAsync(token) };
    }, ct);

    public Task<object> LookupAsync(string raw, CancellationToken ct = default, Guid? claimId = null)
    {
        ValidateToken(raw); var hash = Hash(raw);
        return store.ReadAsync<object>(async (db, token) =>
        {
            var x = await db.OnboardingInvites.AsNoTracking().SingleOrDefaultAsync(x => x.TokenHash == hash, token);
            var claimHash = claimId is { } claim && claim != Guid.Empty ? Hash(raw + ":" + claim.ToString("N")) : null;
            var resumable = x is not null && x.Status == "active" && x.ExpiresAt > Now && claimHash is not null && await db.InviteProvisioning.AnyAsync(r => r.InviteId == x.Id && r.ClaimHash == claimHash && r.State != "completed", token);
            var valid = x is not null && x.Status == "active" && x.ExpiresAt > Now && (x.ReservedUses < x.MaxUses || resumable);
            return new { valid, resumable, expiresAt = valid ? (long?)x!.ExpiresAt : null };
        }, ct);
    }

    public async Task<object> RedeemAsync(string raw, Guid claimId, string username, string password, CancellationToken ct = default)
    {
        await provisioning.WaitAsync(ct);
        try { return await RedeemCoreAsync(raw, claimId, username, password, ct); }
        finally { provisioning.Release(); }
    }
    private async Task<object> RedeemCoreAsync(string raw, Guid claimId, string username, string password, CancellationToken ct)
    {
        ValidateToken(raw);
        if (claimId == Guid.Empty || username.Length is < 1 or > 64 || username != username.Trim() || password.Length is < 1 or > 1024) throw Invalid();
        var tokenHash = Hash(raw);
        var claimHash = Hash(raw + ":" + claimId.ToString("N"));
        var reservation = await store.WriteAsync(async (db, token) =>
        {
            var previous = await db.InviteProvisioning.SingleOrDefaultAsync(x => x.ClaimHash == claimHash, token);
            if (previous is not null)
            {
                if (previous.Username != username) throw new HubException("command_conflict", "Redemption intent changed");
                return (Record: previous, Fresh: false);
            }
            var invite = await db.OnboardingInvites.SingleOrDefaultAsync(x => x.TokenHash == tokenHash, token);
            if (invite is null || invite.Status != "active" || invite.ExpiresAt <= Now || invite.ReservedUses >= invite.MaxUses) throw new HubException("invite_unavailable", "Invalid or unavailable invite");
            invite.ReservedUses++;
            var record = new InviteProvisioning { Id = Guid.NewGuid(), InviteId = invite.Id, UserId = Guid.NewGuid(), Username = username, ClaimHash = claimHash, CreatedAt = Now, UpdatedAt = Now };
            db.InviteProvisioning.Add(record);
            await AuditAsync(db, record, "invite.slot.reserved", token);
            return (Record: record, Fresh: true);
        }, ct);
        var row = reservation.Record;
        if (!reservation.Fresh) return PublicResult(row.State);
        try
        {
            // Mark intent BEFORE each external call; interruption never restarts a call blindly.
            await StageAsync(row.Id, "creating", ct);
            await native.CreateDisabledAsync(row.UserId, username, ct);
            await StageAsync(row.Id, "password_pending", CancellationToken.None);
            await native.SetChosenPasswordAsync(row.UserId, password, ct);
            await StageAsync(row.Id, "policy_pending", CancellationToken.None);
            var invite = await store.ReadAsync((db, token) => db.OnboardingInvites.AsNoTracking().SingleAsync(x => x.Id == row.InviteId, token), ct);
            await native.ApplyPolicyAsync(row.UserId, invite.PolicyJson, invite.GroupId, ct);
            await StageAsync(row.Id, "ready_to_enable", CancellationToken.None);
            // Revocation prevents new reservations and stops an incomplete account from being enabled.
            return await EnableIfActiveAsync(row, ct);
        }
        catch
        {
            await store.AppendAuditAsync(new AuditEvent { EventId = Guid.NewGuid(), Source = "account-hub", EventType = "invite.provisioning.interrupted", Outcome = "failure", SubjectType = "redemption", SubjectId = row.Id.ToString("D"), OccurredAt = clock.GetUtcNow() }, CancellationToken.None);
            throw new HubException("remediation_pending", "Onboarding interrupted. The slot remains reserved; no account will be recreated.");
        }
    }
    private static object PublicResult(string state) => new { status = state == "completed" ? "completed" : "remediation_pending", message = state == "completed" ? "Account created. Sign in with your chosen password. Set up 2FA in Jellyfin Security if required by your server." : "Onboarding needs administrator review. Do not start another redemption." };
    private Task<bool> ActiveAsync(Guid id, CancellationToken ct) => store.ReadAsync((db, token) => db.OnboardingInvites.AnyAsync(x => x.Id == id && x.Status == "active" && x.ExpiresAt > Now, token), ct);
    private async Task<object> EnableIfActiveAsync(InviteProvisioning row, CancellationToken ct)
    {
        await finalization.WaitAsync(ct);
        try
        {
            if (!await ActiveAsync(row.InviteId, ct)) return PublicResult("remediation_pending");
            await native.EnableAsync(row.UserId, ct);
            await CompleteAsync(row.Id, CancellationToken.None);
            return PublicResult("completed");
        }
        finally { finalization.Release(); }
    }
    private Task<bool> StageAsync(Guid id, string state, CancellationToken ct) => store.WriteAsync(async (db, token) =>
    {
        var row = await db.InviteProvisioning.SingleAsync(x => x.Id == id, token); row.State = state; row.UpdatedAt = Now;
        await AuditAsync(db, row, "invite.provisioning." + state, token); return true;
    }, ct);
    private Task<bool> CompleteAsync(Guid id, CancellationToken ct) => store.WriteAsync(async (db, token) =>
    {
        var row = await db.InviteProvisioning.SingleAsync(x => x.Id == id, token);
        if (row.State == "completed") return true;
        row.State = "completed"; row.UpdatedAt = Now;
        var invite = await db.OnboardingInvites.SingleAsync(x => x.Id == row.InviteId, token);
        invite.CompletedUses++; if (invite.CompletedUses == invite.MaxUses && invite.Status == "active") invite.Status = "redeemed";
        await AuditAsync(db, row, "invite.redeemed", token); return true;
    }, ct);
    // Only the final enable stage is safely reconcilable without a password. Earlier ambiguous stages stay disabled.
    public async Task<object> ReconcileAsync(Guid id, CancellationToken ct = default)
    {
        var row = await store.ReadAsync((db, token) => db.InviteProvisioning.AsNoTracking().SingleAsync(x => x.Id == id, token), ct);
        if (row.State == "ready_to_enable" && await native.IsReadyAsync(row.UserId, ct)) await CompleteAsync(id, ct);
        return PublicResult((await store.ReadAsync((db, token) => db.InviteProvisioning.AsNoTracking().SingleAsync(x => x.Id == id, token), ct)).State);
    }
    /// <summary>Explicit user recovery: never create again and never silently replay a saved password.</summary>
    public async Task<object> ResumeAsync(string raw, Guid claimId, string username, string chosenPassword, CancellationToken ct = default)
    {
        await provisioning.WaitAsync(ct);
        try { return await ResumeCoreAsync(raw, claimId, username, chosenPassword, ct); }
        finally { provisioning.Release(); }
    }
    private async Task<object> ResumeCoreAsync(string raw, Guid claimId, string username, string chosenPassword, CancellationToken ct)
    {
        ValidateToken(raw);
        if (claimId == Guid.Empty || username.Length is < 1 or > 64 || chosenPassword.Length is < 1 or > 1024) throw Invalid();
        var claimHash = Hash(raw + ":" + claimId.ToString("N"));
        var row = await store.ReadAsync((db, token) => db.InviteProvisioning.AsNoTracking().SingleOrDefaultAsync(x => x.ClaimHash == claimHash, token), ct) ?? throw Invalid();
        if (row.Username != username) throw new HubException("command_conflict", "Redemption intent changed");
        if (row.State == "completed") return PublicResult(row.State);
        if (!await ActiveAsync(row.InviteId, ct) || !await native.IsIncompleteAsync(row.UserId, username, ct)) return PublicResult("remediation_pending");
        // Claim recovery atomically. A second caller cannot run password/policy effects concurrently.
        var claimed = await store.WriteAsync(async (db, token) =>
        {
            var current = await db.InviteProvisioning.SingleAsync(x => x.Id == row.Id, token);
            // This service is a host singleton: provisioning fences all live resumes.
            // A persisted recovering state may be left by a terminated host. An explicit
            // resume supplies a fresh password and rechecks the same disabled native ID;
            // it never creates an account or replays stored password material.
            if (current.State == "reserved") return false;
            current.State = "recovering"; current.UpdatedAt = Now;
            await AuditAsync(db, current, "invite.provisioning.recovery_requested", token); return true;
        }, ct);
        if (!claimed) return PublicResult("remediation_pending");
        try
        {
            await native.SetChosenPasswordAsync(row.UserId, chosenPassword, ct);
            var invite = await store.ReadAsync((db, token) => db.OnboardingInvites.AsNoTracking().SingleAsync(x => x.Id == row.InviteId, token), ct);
            await native.ApplyPolicyAsync(row.UserId, invite.PolicyJson, invite.GroupId, ct);
            await StageAsync(row.Id, "ready_to_enable", CancellationToken.None);
            return await EnableIfActiveAsync(row, ct);
        }
        catch { await StageAsync(row.Id, "recovery_interrupted", CancellationToken.None); throw new HubException("remediation_pending", "Recovery interrupted; no account was recreated"); }
    }

    public async Task DeliverPendingAsync(Func<string, string, CancellationToken, Task<bool>> send, CancellationToken ct = default)
    {
        // Expiry also erases capsules left in sending after a process crash. Never retry uncertain sends.
        var delivery = await store.WriteAsync(async (db, token) =>
        {
            foreach (var expired in await db.InviteSecretDeliveries.Where(x => x.ExpiresAt <= Now && x.Ciphertext != null).ToArrayAsync(token)) { expired.Ciphertext = null; expired.State = "expired"; }
            var next = await db.InviteSecretDeliveries.Where(x => x.State == "queued" && x.ExpiresAt > Now).OrderBy(x => x.ExpiresAt).FirstOrDefaultAsync(token);
            if (next is not null) next.State = "sending";
            return next;
        }, ct);
        if (delivery is null) return;
        var success = false;
        try
        {
            if (delivery.Ciphertext is not null && await ActiveAsync(delivery.InviteId, ct))
            {
                var raw = store.Protection.CreateProtector("AccountHub.InviteDelivery.v1").Unprotect(delivery.Ciphertext);
                success = await send(delivery.Destination, raw, ct);
            }
        }
        finally
        {
            await store.WriteAsync(async (db, token) =>
            {
                var row = await db.InviteSecretDeliveries.SingleAsync(x => x.Id == delivery.Id, token);
                row.Ciphertext = null; row.State = success ? "delivered" : "uncertain";
                await AuditLedger.AppendAsync(db, new AuditEvent { EventId = Guid.NewGuid(), Source = "account-hub", EventType = "invite.delivery." + row.State, Outcome = success ? "success" : "failure", SubjectType = "invite", SubjectId = delivery.InviteId.ToString("D"), OccurredAt = clock.GetUtcNow() }, clock, token);
                return true;
            }, CancellationToken.None);
        }
    }
    private Task<AuditAcceptance> AuditAsync(AccountHubDbContext db, InviteProvisioning row, string type, CancellationToken ct) => AuditLedger.AppendAsync(db,
        new AuditEvent { EventId = Guid.NewGuid(), Source = "account-hub", EventType = type, SubjectType = "redemption", SubjectId = row.Id.ToString("D"), OccurredAt = clock.GetUtcNow() }, clock, ct);
}
