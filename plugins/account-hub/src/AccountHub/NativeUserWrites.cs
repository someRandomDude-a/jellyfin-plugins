using System.Text.Json;
using AccountHub.Api;
using AccountHub.Core;
using Jellyfin.Data;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Events;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AccountHub;

/// <summary>Bounded single-writer boundary for Hub operations, not a replacement native user manager.</summary>
public sealed class NativeUserWrites(IDbContextFactory<JellyfinDbContext> databases, IEventManager events, UserCreationOwnership? ownership = null)
{
    private readonly SemaphoreSlim capacity = new(128, 128);
    private readonly SemaphoreSlim writer = new(1, 1);

    public async Task<T> SerializeAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        if (!await capacity.WaitAsync(0, ct)) throw new HubException("native_write_queue_full", "Native account write queue is full", true);
        try
        {
            await writer.WaitAsync(ct);
            try { return await operation(ct); }
            finally { writer.Release(); }
        }
        finally { capacity.Release(); }
    }

    public Task<T> TransactionAsync<T>(Func<JellyfinDbContext, CancellationToken, Task<T>> operation, CancellationToken ct, bool creation = false) =>
        creation && ownership is not null
            ? ownership.RunAsync(false, () => SerializeAsync(token => ExecuteTransactionAsync(operation, token), ct), ct)
            : SerializeAsync(token => ExecuteTransactionAsync(operation, token), ct);

    private async Task<T> ExecuteTransactionAsync<T>(Func<JellyfinDbContext, CancellationToken, Task<T>> operation, CancellationToken token)
    {
        await using var db = await databases.CreateDbContextAsync(token);
        InviteNativeProvisioner.RequireSupportedDatabase(typeof(JellyfinDbContext).Assembly.GetName().Version, db.Database.ProviderName);
        if ((await db.Database.GetPendingMigrationsAsync(token)).Any()) throw new HubException("native_database_unavailable", "Native migrations must complete before account writes", true);
        await db.Database.OpenConnectionAsync(token);
        // BEGIN IMMEDIATE acquires SQLite's writer reservation BEFORE any user/policy read.
        // Never call IUserManager or publish events while this transaction is held.
        await using var transaction = ((SqliteConnection)db.Database.GetDbConnection()).BeginTransaction(deferred: false);
        await db.Database.UseTransactionAsync(transaction, token);
        var result = await operation(db, token);
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return result;
    }

    public async Task ApplyManagedAsync(Guid id, JsonElement patch, bool requireDisabled, bool enable, CancellationToken ct)
    {
        var desired = ManagedPolicy.Apply(new MediaBrowser.Model.Users.UserPolicy(), patch);
        var user = await TransactionAsync(async (db, token) =>
        {
            var current = await db.Users.Include(u => u.Permissions).Include(u => u.Preferences).Include(u => u.AccessSchedules).Include(u => u.ProfileImage).SingleOrDefaultAsync(u => u.Id == id, token)
                ?? throw new HubException("not_found", "Native user no longer exists");
            if (current.HasPermission(PermissionKind.IsAdministrator)) throw new HubException("administrator_protected", "Administrator excluded");
            if (requireDisabled && !current.HasPermission(PermissionKind.IsDisabled)) throw new HubException("remediation_pending", "Incomplete account was modified outside onboarding");
            foreach (var field in patch.EnumerateObject())
            {
                if (field.Name == "MaxActiveSessions") current.MaxActiveSessions = desired.MaxActiveSessions;
                else if (field.Name == "MaxParentalRating") current.MaxParentalRatingScore = desired.MaxParentalRating;
                else if (field.Name == "EnabledFolders") current.SetPreference(PreferenceKind.EnabledFolders, desired.EnabledFolders);
                else current.SetPermission(Enum.Parse<PermissionKind>(field.Name), field.Value.GetBoolean());
            }
            if (enable)
            {
                if (!requireDisabled || string.IsNullOrEmpty(current.Password)) throw new HubException("remediation_pending", "Chosen password must be established before enabling");
                current.SetPermission(PermissionKind.IsDisabled, false);
            }
            // Tracked updates only: never DbContext.Update(user), which marks unrelated columns modified.
            return current;
        }, ct);
        await events.PublishAsync(new Jellyfin.Data.Events.Users.UserUpdatedEventArgs(user));
    }

    public async Task SetDisabledAsync(Guid id, Guid actor, bool disabled, CancellationToken ct)
    {
        var user = await TransactionAsync(async (db, token) =>
        {
            var current = await db.Users.Include(u => u.Permissions).Include(u => u.Preferences).Include(u => u.AccessSchedules).Include(u => u.ProfileImage).SingleOrDefaultAsync(u => u.Id == id, token)
                ?? throw new HubException("not_found", "Native user no longer exists");
            if (disabled && (id == actor || current.HasPermission(PermissionKind.IsAdministrator) &&
                await db.Users.CountAsync(u => u.Permissions.Any(p => p.Kind == PermissionKind.IsAdministrator && p.Value), token) <= 1))
                throw new HubException("administrator_protected", "Cannot disable yourself or the last administrator");
            current.SetPermission(PermissionKind.IsDisabled, disabled);
            return current;
        }, ct);
        await events.PublishAsync(new Jellyfin.Data.Events.Users.UserUpdatedEventArgs(user));
    }
}
