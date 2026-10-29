using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.UserManager.Data;
using Jellyfin.Plugin.UserManager.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jellyfin.Plugin.UserManager.Tests;

public sealed class ResetOperationsTests
{
    private static Task IssueAsync(PluginStore store, string hash, Guid userId) => store.CreateResetTokenAsync(new ResetTokenRecord
    {
        TokenHash = hash,
        JellyfinUserId = userId.ToString("N"),
        CreatedByUserId = "test",
        CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        ExpiresAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds()
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AmbiguousPasswordOutcome_RevokesOnceAndNeverReplaysPassword(bool canceled)
    {
        using var folder = new TempFolder();
        var store = new PluginStore(folder.Path);
        await IssueAsync(store, "ambiguous", Guid.NewGuid());
        var passwordCalls = 0;
        var revokeCalls = 0;
        Task Change()
        {
            passwordCalls++;
            return canceled ? Task.FromCanceled(new CancellationToken(true)) : Task.FromException(new InvalidOperationException("ambiguous native result"));
        }
        Task Revoke(Guid _) { revokeCalls++; return Task.CompletedTask; }
        await Assert.ThrowsAnyAsync<Exception>(() => new ResetOperations(store).CompleteAsync("ambiguous", Change, () => Revoke(Guid.Empty)));
        Assert.Equal("claimed", (await store.GetResetTokenAsync("ambiguous"))!.Stage);

        // Restart loses the live fence but not password uncertainty or completion.
        store = new PluginStore(folder.Path);
        await new ResetOperations(store).RevokePendingAsync(Revoke);
        var record = await store.GetResetTokenAsync("ambiguous");
        Assert.Equal("claimed", record!.Stage);
        Assert.NotNull(record.SessionsRevokedAt);
        store = new PluginStore(folder.Path);
        await new ResetOperations(store).RevokePendingAsync(Revoke);
        await Assert.ThrowsAsync<UserManagerException>(() => new ResetOperations(store).CompleteAsync("ambiguous", Change, () => Revoke(Guid.Empty)));
        Assert.Equal(1, passwordCalls);
        Assert.Equal(1, revokeCalls);
    }

    [Fact]
    public async Task FailedFirstFifty_DoNotStarveLaterRecordsAcrossRestart()
    {
        using var folder = new TempFolder();
        var store = new PluginStore(folder.Path);
        var users = new List<Guid>();
        for (var i = 0; i < 125; i++)
        {
            var user = Guid.NewGuid();
            users.Add(user);
            var hash = i.ToString("D3");
            await IssueAsync(store, hash, user);
            await store.ConsumeResetTokenAsync(hash, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }
        var seen = new List<Guid>();
        Task Revoke(Guid id)
        {
            seen.Add(id);
            return users.IndexOf(id) < 50 ? Task.FromException(new InvalidOperationException("persistent failure")) : Task.CompletedTask;
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ResetOperations(store).RevokePendingAsync(Revoke));
        Assert.Equal(users.Take(50), seen);
        store = new PluginStore(folder.Path);
        await new ResetOperations(store).RevokePendingAsync(Revoke);
        Assert.Equal(100, seen.Count);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ResetOperations(store).RevokePendingAsync(Revoke));
        Assert.Equal(150, seen.Count);
        Assert.All(users, user => Assert.Contains(user, seen));
        for (var i = 50; i < 125; i++)
            Assert.NotNull((await store.GetResetTokenAsync(i.ToString("D3")))!.SessionsRevokedAt);
        // Once failures recover, even these ambiguous claimed records leave the queue
        // permanently. Later logins must not trigger another revocation on the next tick.
        await new ResetOperations(store).RevokePendingAsync(_ => Task.CompletedTask);
        store = new PluginStore(folder.Path);
        await new ResetOperations(store).RevokePendingAsync(_ => throw new InvalidOperationException("completed work was replayed"));
        Assert.Empty(await store.PendingSessionRevocationsAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MaintenanceCannotOvertakeLivePasswordEffect(bool ambiguous)
    {
        using var folder = new TempFolder();
        var store = new PluginStore(folder.Path);
        await IssueAsync(store, "live", Guid.NewGuid());
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var revokeCalls = 0;
        Task Revoke() { Interlocked.Increment(ref revokeCalls); return Task.CompletedTask; }
        var live = new ResetOperations(store).CompleteAsync("live", async () =>
        {
            started.SetResult();
            await release.Task;
            if (ambiguous) throw new InvalidOperationException("ambiguous");
        }, Revoke);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("claimed", (await store.GetResetTokenAsync("live"))!.Stage);
        var maintenance = new ResetOperations(store).RevokePendingAsync(_ => Revoke());
        Assert.False(maintenance.IsCompleted);
        Assert.Equal(0, revokeCalls);
        release.SetResult();
        if (ambiguous) await Assert.ThrowsAsync<InvalidOperationException>(() => live);
        else await live;
        await maintenance.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, revokeCalls);
        Assert.NotNull((await store.GetResetTokenAsync("live"))!.SessionsRevokedAt);
    }

    [Fact]
    public async Task CanceledFenceWaiter_DoesNotReleaseAnotherOperationsFence()
    {
        using var folder = new TempFolder();
        var store = new PluginStore(folder.Path);
        var held = await store.AcquireResetEffectsAsync();
        using var canceled = new CancellationTokenSource();
        var waiter = new ResetOperations(store).RevokePendingAsync(_ => Task.CompletedTask, canceled.Token);
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        var next = new ResetOperations(store).RevokePendingAsync(_ => Task.CompletedTask);
        Assert.False(next.IsCompleted);
        held.Dispose();
        await next.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task FailedSessionAuthorizationDoesNotConsumeTheResetOrRunAnyNativeEffect()
    {
        using var folder = new TempFolder(); var store = new PluginStore(folder.Path);
        await IssueAsync(store, "factor-gated", Guid.NewGuid()); var effects = 0;
        Task Effect() { effects++; return Task.CompletedTask; }
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new ResetOperations(store).CompleteAsync("factor-gated", Effect, Effect,
            authorize: _ => Task.FromException(new UnauthorizedAccessException())));
        Assert.Equal(0, effects); Assert.Null((await store.GetResetTokenAsync("factor-gated"))!.UsedAt);
        var authorizations = 0;
        await new ResetOperations(store).CompleteAsync("factor-gated", Effect, Effect, authorize: _ => { authorizations++; return Task.CompletedTask; });
        Assert.Equal(2, effects); Assert.Equal(1, authorizations);
        await Assert.ThrowsAsync<UserManagerException>(() => new ResetOperations(store).CompleteAsync("factor-gated", Effect, Effect, authorize: _ => { authorizations++; return Task.CompletedTask; }));
        Assert.Equal(1, authorizations); Assert.Equal(2, effects);
    }

    [Fact]
    public async Task KnownPasswordSuccess_RevocationFailureRetriesOnlyRevocation()
    {
        using var folder = new TempFolder();
        var store = new PluginStore(folder.Path);
        await IssueAsync(store, "pending", Guid.NewGuid());
        var passwords = 0;
        await Assert.ThrowsAsync<UserManagerException>(() => new ResetOperations(store).CompleteAsync("pending",
            () => { passwords++; return Task.CompletedTask; },
            () => Task.FromException(new InvalidOperationException("unavailable"))));
        Assert.Equal("remediation_pending", (await store.GetResetTokenAsync("pending"))!.Stage);
        await new ResetOperations(store).RevokePendingAsync(_ => Task.CompletedTask);
        Assert.Equal("sessions_revoked", (await store.GetResetTokenAsync("pending"))!.Stage);
        Assert.Equal(1, passwords);
        Assert.Empty(await store.PendingSessionRevocationsAsync());
    }

    [Fact]
    public async Task SchemaV2_MigratesCompletedAndInterruptedClaimsWithoutReplayingEffects()
    {
        using var folder = new TempFolder();
        var store = new PluginStore(folder.Path);
        await using (var connection = new SqliteConnection($"Data Source={store.DatabasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE reset_tokens (TokenHash TEXT PRIMARY KEY, JellyfinUserId TEXT NOT NULL,
                    CreatedByUserId TEXT NOT NULL, CreatedAt INTEGER NOT NULL, ExpiresAt INTEGER NOT NULL,
                    UsedAt INTEGER NULL, Stage TEXT NOT NULL DEFAULT 'issued');
                INSERT INTO reset_tokens VALUES ('complete', 'bad-id', 'test', 1, 2, 1, 'sessions_revoked');
                INSERT INTO reset_tokens VALUES ('interrupted', 'bad-id', 'test', 1, 2, 1, 'issued');
                PRAGMA user_version=2;
                """;
            await command.ExecuteNonQueryAsync();
        }
        await store.InitializeAsync();
        Assert.NotNull((await store.GetResetTokenAsync("complete"))!.SessionsRevokedAt);
        Assert.Equal("claimed", (await store.GetResetTokenAsync("interrupted"))!.Stage);
        Assert.Equal("interrupted", Assert.Single(await store.PendingSessionRevocationsAsync()).TokenHash);
        // Invalid records fail visibly but still rotate, rather than silently disappearing.
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ResetOperations(store).RevokePendingAsync(_ => Task.CompletedTask));
    }
}
