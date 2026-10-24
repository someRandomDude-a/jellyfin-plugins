using System.Text.Json;
using System.Security.Cryptography;
using System.Text;

namespace AccountHub.Core.Seerr;

/// <summary>Native identities are authoritative. Import uncertainty is sticky across restarts and manual retries.</summary>
public sealed class SeerrLifecycle(SeerrStore store, ISeerrUpstream upstream, ISeerrDirectory directory, ISeerrEvents events, TimeProvider clock)
{
    private readonly SemaphoreSlim worker = new(1, 1);
    public const long Admin = 2, ManageUsers = 8;
    public const long KnownMask = 1610612734; // pinned Seerr Permission enum; bit 29 is intentionally unknown
    private long Now => clock.GetUtcNow().ToUnixTimeMilliseconds();
    public Task<SeerrState> StatusAsync(CancellationToken ct = default) => store.ReadAsync(ct);
    public async Task<SeerrCapabilities> DiscoverAsync(CancellationToken ct = default)
    {
        var session = await upstream.OpenAsync(ct);
        return await store.UpdateAsync(s => s.Capabilities = session.Capabilities, ct);
    }
    public static void Validate(SeerrDesired desired)
    {
        if (desired.Permissions < 0 || (desired.Permissions & ~KnownMask) != 0 || (desired.Permissions & Admin) != 0)
            throw new HubException("invalid_request", "Only known non-administrator permissions may be managed");
        desired.MovieQuota.Validate(); desired.TvQuota.Validate();
    }
    private SeerrNativeUser RequireNative(Guid id)
    {
        var user = directory.Find(id) ?? throw new HubException("not_found", "Native account no longer exists");
        if (user.Administrator) throw new HubException("administrator_protected", "Native administrator is protected");
        if (user.Disabled) throw new HubException("native_user_disabled", "Disabled native accounts cannot be provisioned or synchronized");
        if (string.IsNullOrWhiteSpace(user.Username) || user.Username.Length > 256) throw new HubException("invalid_request", "A valid native username is required");
        return user;
    }
    public static void Verify(SeerrAccount account, Guid userId, int? expectedId = null, string? createdAt = null)
    {
        if (account.Id <= 1 || (account.Permissions & Admin) != 0) throw new HubException("administrator_protected", "Seerr owner or administrator is protected");
        if (account.NativeId != userId || expectedId.HasValue && account.Id != expectedId || string.IsNullOrEmpty(account.CreatedAt) || createdAt is not null && account.CreatedAt != createdAt)
            throw new HubException("seerr_identity_mismatch", "Immutable native identity evidence no longer matches");
    }
    public async Task<SeerrLink> LinkAsync(Guid id, int seerrId, CancellationToken ct)
    {
        RequireNative(id);
        var session = await upstream.OpenAsync(ct);
        var actual = await session.FindAsync(seerrId, ct) ?? throw new HubException("not_found", "Seerr account missing");
        Verify(actual, id, seerrId);
        // Also require the immutable-ID lookup to agree; ambiguous imports cannot be linked by name.
        var byNative = await session.FindNativeAsync(id, ct);
        if (byNative?.Id != seerrId) throw new HubException("seerr_identity_mismatch", "Native lookup does not agree");
        RequireNative(id);
        return await store.UpdateAsync(s =>
        {
            Busy(s, id);
            if (s.Links.Any(l => l.UserId != id && l.Fingerprint == session.Capabilities.Fingerprint && l.SeerrId == seerrId)) throw new HubException("seerr_link_conflict", "Account already linked");
            var old = s.Links.SingleOrDefault(l => l.UserId == id);
            if (old is not null && (old.SeerrId != seerrId || old.Fingerprint != session.Capabilities.Fingerprint)) throw new HubException("seerr_link_conflict", "Unlink the old connection explicitly first");
            var link = new SeerrLink(id, seerrId, session.Capabilities.Fingerprint, actual.CreatedAt, old?.Desired, (old?.Revision ?? 0) + 1);
            s.Links.RemoveAll(l => l.UserId == id); s.Links.Add(link); s.Capabilities = session.Capabilities; return link;
        }, ct);
    }
    public Task<bool> UnlinkAsync(Guid id, CancellationToken ct) => store.UpdateAsync(s => { Busy(s, id); return s.Links.RemoveAll(l => l.UserId == id) != 0; }, ct);
    private static void Busy(SeerrState state, Guid id)
    {
        if (state.Jobs.Any(j => j.UserId == id && j.Active)) throw new HubException("seerr_job_active", "Wait for or resolve the active job");
    }
    private static void Capacity(SeerrState state)
    {
        if (state.Jobs.Count >= 10000 || state.Jobs.Count(j => j.Active) >= 1000 || state.Links.Count >= 10000) throw new HubException("seerr_queue_full", "Seerr durable storage is at its configured bound", true);
    }
    public Task<SeerrJob> EnqueueAsync(Guid commandId, Guid actor, Guid id, SeerrDesired? desired, bool provision, CancellationToken ct)
    {
        RequireNative(id); if (desired is not null) Validate(desired);
        return store.UpdateAsync(s =>
        {
            var existing = s.Jobs.SingleOrDefault(j => j.Id == commandId);
            if (existing is not null)
            {
                if (existing.ActorId != actor || existing.UserId != id || existing.Kind != (provision ? "provision" : "reconcile") || existing.Desired != desired)
                    throw new HubException("command_conflict", "Job command identity or payload differs");
                return existing;
            }
            Busy(s, id); Capacity(s);
            var link = s.Links.SingleOrDefault(l => l.UserId == id);
            if (!provision && link is null) throw new HubException("seerr_not_linked", "Explicitly verify a native link first");
            var fingerprint = link?.Fingerprint ?? s.Capabilities?.Fingerprint ?? throw new HubException("seerr_discovery_required", "Discover the connection before provisioning");
            var job = new SeerrJob { Id = commandId, ActorId = actor, UserId = id, Kind = provision ? "provision" : "reconcile", Fingerprint = fingerprint, SeerrId = link?.SeerrId, AccountCreatedAt = link?.CreatedAt, Desired = desired ?? link?.Desired, Due = Now, Updated = Now };
            s.Jobs.Add(job); return job;
        }, ct);
    }
    /// <summary>Call and await BEFORE native deletion. Worker only proceeds after native absence, including crash/external deletion recovery.</summary>
    public Task<SeerrJob?> PrepareDeletionAsync(Guid commandId, Guid? actor, Guid id, CancellationToken ct = default) => store.UpdateAsync(s =>
    {
        var replay = s.Jobs.SingleOrDefault(j => j.Id == commandId); if (replay is not null) return replay;
        var native = directory.Find(id); if (native?.Administrator == true) throw new HubException("administrator_protected", "Native administrator is protected");
        var link = s.Links.SingleOrDefault(l => l.UserId == id); if (link is null) return null;
        Busy(s, id); Capacity(s);
        var job = new SeerrJob { Id = commandId, ActorId = actor, UserId = id, Kind = "delete", Fingerprint = link.Fingerprint, SeerrId = link.SeerrId, AccountCreatedAt = link.CreatedAt, State = "awaiting_native_delete", Due = Now, Updated = Now };
        s.Jobs.Add(job); return job;
    }, ct);
    public Task<SeerrJob> RetryAsync(Guid jobId, CancellationToken ct) => store.UpdateAsync(s =>
    {
        var job = s.Jobs.SingleOrDefault(j => j.Id == jobId) ?? throw new HubException("not_found", "Job missing");
        if (job.Active || job.State == "completed") return job;
        Busy(s, job.UserId);
        FreezeLegacyAudit(s, job);
        // Never clear ImportAttempted, target identity or connection snapshot, even with new fresh proof.
        job.State = job.Kind == "delete" ? "awaiting_native_delete" : "pending"; job.Attempts = 0; job.Due = Now; job.Updated = Now; job.Error = null; return job;
    }, ct);
    public Task<SeerrJob> CancelAsync(Guid jobId, CancellationToken ct) => store.UpdateAsync(s =>
    {
        var job = s.Jobs.SingleOrDefault(j => j.Id == jobId) ?? throw new HubException("not_found", "Job missing");
        if (job.State == "running") throw new HubException("seerr_job_active", "Cannot cancel an in-flight effect");
        if (job.State == "completed") throw new HubException("command_conflict", "Completed effects cannot be undone");
        if (job.State == "cancelled") return job;
        FreezeLegacyAudit(s, job);
        job.State = "cancelled"; job.Updated = Now; QueueAudit(s, job); return job;
    }, ct);
    public async Task<object> DriftAsync(Guid id, CancellationToken ct)
    {
        var state = await store.ReadAsync(ct); var link = state.Links.SingleOrDefault(l => l.UserId == id);
        if (link is null) return new { linked = false };
        var session = await upstream.OpenAsync(ct); CheckConnection(link.Fingerprint, session);
        var account = await session.FindAsync(link.SeerrId, ct) ?? throw new HubException("seerr_account_missing", "Linked account is missing");
        Verify(account, id, link.SeerrId, link.CreatedAt);
        var actual = await session.ReadDesiredAsync(link.SeerrId, ct);
        return new { linked = true, link, actual, drift = link.Desired is not null && link.Desired != actual };
    }
    private static void CheckConnection(string fingerprint, ISeerrSession session)
    {
        if (fingerprint != session.Capabilities.Fingerprint) throw new HubException("seerr_connection_changed", "Connection changed; explicit remediation required");
    }
    public async Task<JsonElement> RequestsAsync(Guid id, int skip, int limit, CancellationToken ct)
    {
        if (skip < 0 || limit is < 1 or > 100) throw new HubException("invalid_request", "Invalid pagination");
        var state = await store.ReadAsync(ct); var link = state.Links.SingleOrDefault(l => l.UserId == id) ?? throw new HubException("seerr_not_linked", "No verified link");
        var session = await upstream.OpenAsync(ct); CheckConnection(link.Fingerprint, session);
        Verify(await session.FindAsync(link.SeerrId, ct) ?? throw new HubException("not_found", "Account missing"), id, link.SeerrId, link.CreatedAt);
        return await session.RequestsAsync(link.SeerrId, skip, limit, ct);
    }
    public async Task TickAsync(CancellationToken ct = default)
    {
        if (!await worker.WaitAsync(0, ct)) return;
        try
        {
            // Bounded inventory scans use only saved immutable links, never discover/guess identities on install.
            var state = await store.UpdateAsync(s => { foreach (var j in s.Jobs) FreezeLegacyAudit(s, j); return s; }, ct);
            foreach (var link in state.Links.Where(l => directory.Find(l.UserId) is null && !state.Jobs.Any(j => j.UserId == l.UserId && (j.Active || j.Kind == "delete"))).Take(50))
                await PrepareDeletionAsync(Guid.NewGuid(), null, link.UserId, ct);
            for (var count = 0; count < 10; count++)
            {
                var job = await store.UpdateAsync(s =>
                {
                    var j = s.Jobs.FirstOrDefault(j => j.Active && j.Due <= Now && (j.State != "running" || j.Updated + 120000 <= Now) && (j.Kind != "delete" || directory.Find(j.UserId) is null));
                    if (j is not null) { j.State = "running"; j.Attempts++; j.Updated = Now; j.Due = Now + 120000; }
                    return j;
                }, ct);
                if (job is null) break;
                try { await ExecuteAsync(job, ct); await FinishAsync(job.Id, "completed", null, ct); }
                catch (HubException ex) { await FinishAsync(job.Id, "blocked", ex.Code, CancellationToken.None); }
                catch (Exception) { await FinishAsync(job.Id, job.Attempts >= 8 ? "failed" : "pending", "seerr_unavailable", CancellationToken.None); }
            }
            state = await store.ReadAsync(ct);
            foreach (var entry in state.AuditOutbox.Take(50))
            {
                await events.PublishAsync(entry.Event, ct);
                // Acknowledge only this immutable snapshot, never the current mutable job state.
                await store.UpdateAsync(s => s.AuditOutbox.RemoveAll(e => e.Event.EventId == entry.Event.EventId), ct);
            }
            await store.UpdateAsync(s => { s.Jobs.RemoveAll(j => j.State == "completed" && !j.AuditPending && !s.AuditOutbox.Any(e => e.JobId == j.Id) && j.Updated < Now - TimeSpan.FromDays(30).TotalMilliseconds); return true; }, ct);
        }
        finally { worker.Release(); }
    }
    private Task<bool> FinishAsync(Guid id, string state, string? error, CancellationToken ct) => store.UpdateAsync(s =>
    {
        var j = s.Jobs.Single(j => j.Id == id); FreezeLegacyAudit(s, j); j.State = state; j.Error = error; j.Updated = Now;
        j.Due = Now + Math.Min(3600000, 15000L * (1L << Math.Min(j.Attempts, 8))); if (state is "completed" or "blocked" or "failed") QueueAudit(s, j); return true;
    }, ct);
    private static void QueueAudit(SeerrState state, SeerrJob job, Guid? eventId = null)
    {
        state.AuditOutbox.Add(new(job.Id, new(eventId ?? Guid.NewGuid(), job.ActorId, "seerr." + job.Kind + "." + job.State, job.State, job.Error, job.Updated)));
        job.AuditPending = false;
    }
    private static void FreezeLegacyAudit(SeerrState state, SeerrJob job)
    {
        if (!job.AuditPending) return;
        // Preserve the pre-outbox ID if delivery already committed before an upgrade/crash.
        var id = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"{job.Id:N}:{job.State}:{job.Attempts}:{job.Updated}:{job.Error}")).AsSpan(0, 16));
        if (!state.AuditOutbox.Any(e => e.Event.EventId == id)) QueueAudit(state, job, id);
        job.AuditPending = false;
    }
    private async Task ExecuteAsync(SeerrJob job, CancellationToken ct)
    {
        if (job.Kind != "delete") RequireNative(job.UserId);
        else if (directory.Find(job.UserId) is not null) throw new HubException("native_user_still_exists", "Native account must be absent");
        var session = await upstream.OpenAsync(ct); CheckConnection(job.Fingerprint, session);
        var actual = job.SeerrId.HasValue ? await session.FindAsync(job.SeerrId.Value, ct) : await session.FindNativeAsync(job.UserId, ct);
        if (job.Kind == "delete")
        {
            if (actual is not null)
            {
                Verify(actual, job.UserId, job.SeerrId, job.AccountCreatedAt);
                if (directory.Find(job.UserId) is not null) throw new HubException("native_user_still_exists", "Native account reappeared");
                await session.DeleteAsync(actual.Id, job.UserId, job.AccountCreatedAt!, ct);
                if (await session.FindAsync(actual.Id, ct) is not null) throw new IOException("Delete not confirmed");
            }
            await store.UpdateAsync(s => { s.Links.RemoveAll(l => l.UserId == job.UserId && l.Fingerprint == job.Fingerprint && l.SeerrId == job.SeerrId); return true; }, ct); return;
        }
        if (actual is null)
        {
            if (job.Kind != "provision" || job.SeerrId.HasValue) throw new HubException("seerr_account_missing", "Saved account missing; no automatic replacement");
            if (job.ImportAttempted) throw new HubException("seerr_create_uncertain", "Import was attempted; inspect upstream before explicit verified linking");
            if ((session.Capabilities.DefaultPermissions & Admin) != 0) throw new HubException("seerr_unsafe_defaults", "Seerr defaults would create an administrator; change defaults in Seerr explicitly first");
            RequireNative(job.UserId);
            await store.UpdateAsync(s => { s.Jobs.Single(j => j.Id == job.Id).ImportAttempted = true; return true; }, ct);
            await session.ImportAsync(job.UserId, ct);
            actual = await session.FindNativeAsync(job.UserId, ct) ?? throw new HubException("seerr_create_uncertain", "Import did not prove a unique native account");
        }
        Verify(actual, job.UserId, job.SeerrId, job.AccountCreatedAt); RequireNative(job.UserId);
        // Persist proven identity before policy writes, so every restart uses the original ID and creation timestamp.
        await store.UpdateAsync(s =>
        {
            var j = s.Jobs.Single(j => j.Id == job.Id); j.SeerrId = actual.Id; j.AccountCreatedAt = actual.CreatedAt;
            var old = s.Links.SingleOrDefault(l => l.UserId == job.UserId);
            if (old is not null && (old.SeerrId != actual.Id || old.Fingerprint != job.Fingerprint)) throw new HubException("seerr_link_conflict", "Link changed");
            s.Links.RemoveAll(l => l.UserId == job.UserId); s.Links.Add(new(job.UserId, actual.Id, job.Fingerprint, actual.CreatedAt, job.Desired ?? old?.Desired, (old?.Revision ?? 0) + 1)); return true;
        }, ct);
        if (job.Desired is not null)
        {
            RequireNative(job.UserId); await session.ApplyAsync(actual.Id, job.UserId, actual.CreatedAt, job.Desired, ct);
            if (await session.ReadDesiredAsync(actual.Id, ct) != job.Desired) throw new HubException("seerr_policy_not_applied", "Upstream did not apply desired permissions/quotas; review bypass privileges");
        }
    }
}
