using System.Text.Json;
using AccountHub.Api;
using AccountHub.Api.Contracts;
using AccountHub.Core;
using MediaBrowser.Model.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AccountHub.Groups;

public interface IGroupPolicyNative
{
    UserPolicy Read(Guid userId);
    Task WriteManagedAsync(Guid userId, JsonElement patch, CancellationToken ct);
}
public sealed record PolicyTarget(Guid UserId, long Revision, long GroupRevision = 0);
public sealed record PolicyOutcome(Guid UserId, string Status, Guid? SnapshotId = null);
public sealed record PolicyView(Guid UserId, long Revision, Guid? GroupId, long GroupRevision, bool Administrator,
    JsonElement Current, JsonElement Desired, string[] ChangedFields, JsonElement Overrides, string Status = "ready");

/// <summary>Only explicit commands change native state. Reading or editing a group never reconciles users.</summary>
public sealed class GroupPolicyService(HubPaths paths, IGroupPolicyNative native, HubStore audit, TimeProvider clock)
{
    private readonly SemaphoreSlim lease = new(1, 1);
    private bool ready;
    public GroupPolicyDbContext Open() => new(new DbContextOptionsBuilder<GroupPolicyDbContext>()
        .UseSqlite(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = paths.Database, Pooling = false, DefaultTimeout = 10, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWrite }.ToString(), o => o.MigrationsHistoryTable("__HubGroupPolicyHistory"))
        .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)).Options);
    private async Task InitializeAsync(CancellationToken ct)
    {
        if (ready) return;
        Directory.CreateDirectory(paths.DataDirectory);
        await using var db = Open();
        var known = db.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);
        if ((await db.Database.GetAppliedMigrationsAsync(ct)).Any(x => !known.Contains(x))) throw new HubException("unavailable", "Policy schema requires a newer Hub", true);
        await db.Database.MigrateAsync(ct);
        ready = true;
    }
    private async Task<T> Locked<T>(Func<GroupPolicyDbContext, Task<T>> action, CancellationToken ct)
    {
        await lease.WaitAsync(ct);
        try { await InitializeAsync(ct); await using var db = Open(); return await action(db); }
        finally { lease.Release(); }
    }
    private static JsonElement Json(string text) => JsonSerializer.Deserialize<JsonElement>(text);
    private static JsonElement Project(UserPolicy value) => JsonSerializer.SerializeToElement(ManagedPolicy.Project(value));
    private static void Validate(JsonElement fields) => _ = ManagedPolicy.Apply(new UserPolicy(), fields);
    private static void Revision(long actual, long expected)
    { if (actual != expected) throw new HubException("revision_changed", "Refresh policy intent before applying"); }
    private static void Targets(PolicyTarget[] targets)
    {
        if (targets.Length is < 1 or > 100 || targets.Any(x => x.UserId == Guid.Empty) || targets.Select(x => x.UserId).Distinct().Count() != targets.Length)
            throw new HubException("invalid_request", "Select 1–100 distinct users");
    }
    public Task<object> GroupsAsync(int skip, int limit, CancellationToken ct = default) => Locked<object>(async db =>
    {
        if (skip < 0 || limit is < 1 or > 100) throw new HubException("invalid_request", "Invalid pagination");
        return new { total = await db.Groups.CountAsync(ct), items = await db.Groups.AsNoTracking().OrderBy(x => x.NameKey).Skip(skip).Take(limit).ToArrayAsync(ct) };
    }, ct);
    public Task<PolicyGroup> GroupAsync(Guid id, CancellationToken ct = default) => Locked(async db =>
        await db.Groups.AsNoTracking().SingleOrDefaultAsync(group => group.Id == id, ct) ?? throw new HubException("not_found", "Group no longer exists"), ct);
    public Task<object> MembersAsync(Guid? groupId, int skip, int limit, CancellationToken ct = default) => Locked<object>(async db =>
    {
        if (skip < 0 || limit is < 1 or > 100) throw new HubException("invalid_request", "Invalid pagination");
        var query = db.Assignments.AsNoTracking().Where(x => x.GroupId == groupId);
        return new { total = await query.CountAsync(ct), items = await query.OrderBy(x => x.UserId).Skip(skip).Take(limit).ToArrayAsync(ct) };
    }, ct);
    public Task<PolicyGroup> SaveGroupAsync(Guid id, long revision, string name, string description, JsonElement fields, CancellationToken ct = default) => Locked(async db =>
    {
        Validate(fields); name = name.Trim(); description = description.Trim();
        if (id == Guid.Empty || name.Length is < 1 or > 100 || description.Length > 1000 || name.Any(char.IsControl)) throw new HubException("invalid_request", "Invalid group");
        var group = await db.Groups.FindAsync([id], ct);
        Revision(group?.Revision ?? 0, revision);
        var key = name.ToUpperInvariant();
        if (await db.Groups.AnyAsync(x => x.NameKey == key && x.Id != id, ct)) throw new HubException("rejected", "Group name already exists");
        if (group is null) { group = new PolicyGroup { Id = id }; db.Groups.Add(group); }
        else { if (group.IsSystem) throw new HubException("rejected", "System group is protected"); group.Revision++; }
        group.Name = name; group.NameKey = key; group.Description = description; group.PolicyJson = fields.GetRawText();
        await db.SaveChangesAsync(ct); return group;
    }, ct);
    public Task<bool> DeleteGroupAsync(Guid id, long revision, CancellationToken ct = default) => Locked(async db =>
    {
        var group = await db.Groups.FindAsync([id], ct) ?? throw new HubException("invalid_request", "Unknown group");
        Revision(group.Revision, revision);
        if (group.IsSystem || await db.Assignments.AnyAsync(x => x.GroupId == id, ct)) throw new HubException("rejected", "System or assigned group cannot be deleted");
        db.Groups.Remove(group); await db.SaveChangesAsync(ct); return true;
    }, ct);
    private async Task<PolicyView> View(GroupPolicyDbContext db, Guid id, CancellationToken ct)
    {
        var current = native.Read(id);
        var assignment = await db.Assignments.FindAsync([id], ct);
        var group = assignment?.GroupId is Guid gid ? await db.Groups.FindAsync([gid], ct) : null;
        // Baseline is captured at explicit enrollment so sparse group fields can later be reset safely.
        var desired = assignment is null ? current : ManagedPolicy.Apply(current, Json(assignment.BaseJson));
        if (group is not null) desired = ManagedPolicy.Apply(desired, Json(group.PolicyJson));
        if (assignment is not null) desired = ManagedPolicy.Apply(desired, Json(assignment.OverridesJson));
        if (current.IsAdministrator) desired = current;
        var before = Project(current); var after = Project(desired);
        return new(id, assignment?.Revision ?? 0, assignment?.GroupId, group?.Revision ?? 0, current.IsAdministrator,
            before, after, Changed(before, after), Json(assignment?.OverridesJson ?? "{}"));
    }
    private static string[] Changed(JsonElement before, JsonElement after) => ManagedPolicy.Fields.Where(name =>
        before.GetProperty(name).GetRawText() != after.GetProperty(name).GetRawText()).ToArray();
    public Task<PolicyView> DriftAsync(Guid id, CancellationToken ct = default) => Locked(db => View(db, id, ct), ct);
    public Task<PolicyView[]> PreviewAsync(PolicyTarget[] targets, CancellationToken ct = default, string mode = "apply") => Locked(async db =>
    {
        Targets(targets); if (mode is not ("apply" or "reset")) throw new HubException("invalid_request", "Invalid preview mode");
        var results = new List<PolicyView>();
        foreach (var target in targets)
        {
            try
            {
                var view = await View(db, target.UserId, ct);
                if (mode == "reset" && !view.Administrator)
                {
                    var assignment = await db.Assignments.FindAsync([target.UserId], ct);
                    if (assignment is not null) { var desired = Json(assignment.BaseJson); view = view with { Desired = desired, ChangedFields = Changed(view.Current, desired) }; }
                }
                results.Add(view);
            }
            catch (HubException ex) when (ex.Code == "not_found")
            { results.Add(new(target.UserId, 0, null, 0, false, Json("{}"), Json("{}"), [], Json("{}"), "not_found")); }
        }
        return results.ToArray();
    }, ct);
    public Task<PolicyView> AssignAsync(Guid id, long revision, Guid? groupId, long groupRevision, JsonElement overrides, CancellationToken ct = default) => Locked(async db =>
    {
        Validate(overrides); var current = native.Read(id);
        if (current.IsAdministrator) throw new HubException("administrator_protected", "Administrators cannot be enrolled in group reconciliation");
        var assignment = await db.Assignments.FindAsync([id], ct); Revision(assignment?.Revision ?? 0, revision);
        if (groupId is Guid gid) { var group = await db.Groups.FindAsync([gid], ct) ?? throw new HubException("invalid_request", "Unknown group"); Revision(group.Revision, groupRevision); }
        else Revision(0, groupRevision);
        if (assignment is null) { assignment = new PolicyAssignment { UserId = id, BaseJson = Project(current).GetRawText() }; db.Assignments.Add(assignment); }
        else assignment.Revision++;
        assignment.GroupId = groupId; assignment.OverridesJson = overrides.GetRawText();
        await db.SaveChangesAsync(ct); return await View(db, id, ct);
    }, ct);
    public Task<object> SnapshotsAsync(Guid id, int skip, int limit, CancellationToken ct = default) => Locked<object>(async db =>
    {
        if (skip < 0 || limit is < 1 or > 100) throw new HubException("invalid_request", "Invalid pagination");
        var query = db.Snapshots.AsNoTracking().Where(x => x.UserId == id);
        return new { total = await query.CountAsync(ct), items = await query.OrderByDescending(x => x.CreatedAt).Skip(skip).Take(limit).ToArrayAsync(ct) };
    }, ct);
    public Task<PolicyOutcome[]> ApplyAsync(Guid actor, Guid commandId, PolicyTarget[] targets, string mode, Guid? snapshotId = null, CancellationToken ct = default) => Locked(async db =>
    {
        Targets(targets);
        if (mode is not ("apply" or "reset" or "rollback") || mode == "rollback" && targets.Length != 1) throw new HubException("invalid_request", "Invalid policy action");
        var outcomes = new List<PolicyOutcome>();
        foreach (var target in targets)
        {
            PolicySnapshot? snapshot = null;
            try
            {
                var view = await View(db, target.UserId, ct);
                if (view.Administrator) { outcomes.Add(new(target.UserId, "administrator_skipped")); continue; }
                Revision(view.Revision, target.Revision); Revision(view.GroupRevision, target.GroupRevision);
                var assignment = await db.Assignments.FindAsync([target.UserId], ct);
                if (assignment is null) { outcomes.Add(new(target.UserId, "unmanaged")); continue; }
                var desired = view.Desired;
                if (mode == "reset") desired = Json(assignment.BaseJson);
                if (mode == "rollback")
                {
                    var old = await db.Snapshots.FindAsync([snapshotId ?? Guid.Empty], ct);
                    if (old is null || old.UserId != target.UserId) throw new HubException("rejected", "Snapshot target mismatch");
                    desired = Json(old.BeforeJson); Validate(desired);
                }
                // Persist snapshots before the effect, including no-op resets which change intent.
                snapshot = new PolicySnapshot { Id = Guid.NewGuid(), UserId = target.UserId, CommandId = commandId, BeforeJson = view.Current.GetRawText(), AfterJson = desired.GetRawText(), CreatedAt = clock.GetUtcNow().ToUnixTimeMilliseconds() };
                db.Snapshots.Add(snapshot); await db.SaveChangesAsync(ct);
                await Audit(actor, commandId, snapshot, "prepared", ct);
                // Re-read immediately before effects; adapter also rechecks and overlays only managed fields.
                if (native.Read(target.UserId).IsAdministrator) { snapshot.Outcome = "administrator_skipped"; await db.SaveChangesAsync(ct); outcomes.Add(new(target.UserId, snapshot.Outcome, snapshot.Id)); continue; }
                await native.WriteManagedAsync(target.UserId, desired, ct);
                if (mode is "reset" or "rollback")
                {
                    assignment.GroupId = null;
                    assignment.OverridesJson = mode == "reset" ? "{}" : desired.GetRawText();
                    assignment.Revision++;
                }
                snapshot.Outcome = "applied";
                await db.SaveChangesAsync(CancellationToken.None);
                await Audit(actor, commandId, snapshot, "applied", CancellationToken.None);
                outcomes.Add(new(target.UserId, "applied", snapshot.Id));
            }
            catch (HubException ex) when (snapshot is null) { outcomes.Add(new(target.UserId, ex.Code)); }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // A native update can have committed before it throws. Never retry automatically.
                if (snapshot is not null) { snapshot.Outcome = "remediation_pending"; await db.SaveChangesAsync(CancellationToken.None); await Audit(actor, commandId, snapshot, "uncertain", CancellationToken.None); }
                outcomes.Add(new(target.UserId, snapshot is null ? "unavailable" : "remediation_pending", snapshot?.Id));
            }
        }
        return outcomes.ToArray();
    }, ct);
    private Task<AuditAcceptance> Audit(Guid actor, Guid command, PolicySnapshot snapshot, string outcome, CancellationToken ct) => audit.AppendAuditAsync(new AccountHub.Api.Contracts.AuditEvent
    {
        EventId = Guid.NewGuid(), Source = "account-hub", EventType = "user.policy." + outcome,
        ActorUserId = actor, SubjectType = "user", SubjectId = snapshot.UserId.ToString("D"), CorrelationId = command,
        OccurredAt = clock.GetUtcNow(), Outcome = outcome == "uncertain" ? "failure" : "success",
        Metadata = JsonSerializer.SerializeToElement(new { snapshotId = snapshot.Id, before = Json(snapshot.BeforeJson), after = Json(snapshot.AfterJson), changedFields = Changed(Json(snapshot.BeforeJson), Json(snapshot.AfterJson)) })
    }, ct);
}
