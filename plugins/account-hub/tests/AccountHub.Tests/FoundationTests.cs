using System.Text.Json;
using AccountHub.Api.Contracts;
using AccountHub.Core;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AccountHub.Tests;

public sealed class FoundationTests
{
    internal static AuditEvent Event(Guid? eventId = null) => new()
    {
        EventId = eventId ?? Guid.NewGuid(), Source = "deletion-queue", EventType = "deletion.proposal.created",
        OccurredAt = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero),
        Metadata = JsonSerializer.SerializeToElement(new { proposalId = "proposal-1" })
    };

    [Fact]
    public async Task MigrationPersistsDefaultsAndReopens()
    {
        await using var f = new StoreFixture();
        await f.InitializeAsync();
        Assert.True(f.Store.Ready);
        var state = await f.Store.ReadAsync((db, ct) => db.State.AsNoTracking().SingleAsync(ct));
        Assert.Equal(90, state.AuditRetentionDays);
        Assert.Equal(90, state.NotificationRetentionDays);
        Assert.Equal("Destructive", state.StepUpLevelMirror);
        Assert.Equal("native", state.SecurityAlertMode);
        using var reopened = f.Reopen();
        await reopened.InitializeAsync();
        Assert.True(reopened.Ready);
    }
    [Fact]
    public async Task CurrentSchemaWithInterruptedMigrationLockReopensWithoutDeletingTheLock()
    {
        await using var f=new StoreFixture();await f.InitializeAsync();
        await f.ExecuteSqlAsync("INSERT INTO __EFMigrationsLock (Id,Timestamp) VALUES(1,'2026-10-26T00:00:00Z')");
        using var reopened=f.Reopen();using var budget=new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await reopened.InitializeAsync(budget.Token);Assert.True(reopened.Ready);
        await using var connection=new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={f.Paths.Database};Pooling=False");await connection.OpenAsync();
        await using var command=connection.CreateCommand();command.CommandText="SELECT COUNT(*) FROM __EFMigrationsLock WHERE Id=1";
        Assert.Equal(1L,await command.ExecuteScalarAsync());
        Assert.True((await reopened.VerifyAuditAsync()).Valid);
    }

    [Fact]
    public async Task AuditAcceptanceAndDuplicateAreDurableAndConflictsRejected()
    {
        await using var f = new StoreFixture();
        await f.InitializeAsync();
        var value = Event();
        var first = await f.Store.AppendAuditAsync(value);
        using var restarted = f.Reopen();
        await restarted.InitializeAsync();
        var duplicate = await restarted.AppendAuditAsync(value);
        Assert.Equal("duplicate", duplicate.Status);
        Assert.Equal(first.AuditId, duplicate.AuditId);
        var conflict = await Assert.ThrowsAsync<HubException>(() => restarted.AppendAuditAsync(value with { Outcome = "failure" }));
        Assert.Equal("rejected", conflict.Code);
        Assert.True((await restarted.VerifyAuditAsync()).Valid);
        Assert.Equal(1, await restarted.ReadAsync((db, ct) => db.Audit.CountAsync(ct)));
    }

    [Fact]
    public async Task ConcurrentEventsHaveOneMonotonicChain()
    {
        await using var f = new StoreFixture();
        await f.InitializeAsync();
        await Task.WhenAll(Enumerable.Range(0, 40).Select(_ => f.Store.AppendAuditAsync(Event())));
        Assert.True((await f.Store.VerifyAuditAsync()).Valid);
        Assert.Equal(40, await f.Store.ReadAsync((db, ct) => db.Audit.CountAsync(ct)));
    }

    [Theory]
    [InlineData("UPDATE audit_events SET EventJson = '{}'")]
    [InlineData("DELETE FROM audit_events")]
    [InlineData("UPDATE audit_events SET RecordedAtUnixMs = 1")]
    public async Task TamperFailsClosedAcrossRestart(string sql)
    {
        await using var f = new StoreFixture();
        await f.InitializeAsync();
        await f.Store.AppendAuditAsync(Event());
        await f.ExecuteSqlAsync(sql);
        using var restarted = f.Reopen();
        await Assert.ThrowsAnyAsync<Exception>(() => restarted.InitializeAsync());
        Assert.False(restarted.Ready);
        var error = await Assert.ThrowsAsync<HubException>(() => restarted.AppendAuditAsync(Event()));
        Assert.Equal("unavailable", error.Code);
    }

    [Fact]
    public async Task RetentionCheckpointVerifiesAndDedupeSurvivesPruning()
    {
        await using var f = new StoreFixture();
        await f.InitializeAsync();
        var value = Event();
        await f.Store.AppendAuditAsync(value);
        f.Clock.Now = f.Clock.Now.AddDays(91);
        await f.Store.AppendAuditAsync(Event());
        Assert.Equal(1, await f.Store.PruneAuditAsync(Guid.NewGuid()));
        var verification = await f.Store.VerifyAuditAsync();
        Assert.True(verification.Valid);
        Assert.Equal(1, verification.CheckpointSequence);
        Assert.Equal("duplicate", (await f.Store.AppendAuditAsync(value)).Status);
        using var restarted = f.Reopen();
        await restarted.InitializeAsync();
        Assert.True((await restarted.VerifyAuditAsync()).Valid);
    }

    [Fact]
    public async Task CommandReplayPrecedesProofAndRevisionAndRejectsChangedInput()
    {
        await using var f = new StoreFixture();
        await f.InitializeAsync();
        var service = new HubSettingsService(new CommandGate(f.Store, f.Clock));
        var actor = Guid.NewGuid(); var command = Guid.NewGuid();
        var proofs = 0;
        Task Authorize(CancellationToken _) { proofs++; return Task.CompletedTask; }
        var result = await service.UpdateRetentionAsync(actor, command, 0, new(30, 60), Authorize);
        var replay = await service.UpdateRetentionAsync(actor, command, 0, new(30, 60), _ => throw new Exception("Must not consume proof"));
        Assert.Equal(result.GetRawText(), replay.GetRawText());
        Assert.Equal(1, proofs);
        Assert.Equal(1, await f.Store.ReadAsync((db, ct) => db.Audit.CountAsync(ct)));
        var conflict = await Assert.ThrowsAsync<HubException>(() => service.UpdateRetentionAsync(actor, command, 0, new(31, 60), Authorize));
        Assert.Equal("command_conflict", conflict.Code);
        var stale = await Assert.ThrowsAsync<HubException>(() => service.UpdateRetentionAsync(actor, Guid.NewGuid(), 0, new(30, 60), Authorize));
        Assert.Equal("revision_changed", stale.Code);
        Assert.Equal(1, proofs);
    }

    [Fact]
    public async Task DeniedProofDoesNotMutateOrCreateCommandResult()
    {
        await using var f = new StoreFixture();
        await f.InitializeAsync();
        var service = new HubSettingsService(new CommandGate(f.Store, f.Clock));
        await Assert.ThrowsAsync<HubException>(() => service.UpdateRetentionAsync(Guid.NewGuid(), Guid.NewGuid(), 0,
            new(30, 60), _ => throw new HubException("step_up_failed", "Denied")));
        Assert.Equal(0, await f.Store.ReadAsync((db, ct) => db.Commands.CountAsync(ct)));
        Assert.Equal(0, await f.Store.ReadAsync((db, ct) => db.Audit.CountAsync(ct)));
        Assert.Equal(90, await f.Store.ReadAsync((db, ct) => db.State.Select(x => x.AuditRetentionDays).SingleAsync(ct)));
    }

    [Fact]
    public async Task AuditWriteFailureRollsBackSettingsAndCommandAndFailsClosed()
    {
        await using var f = new StoreFixture();
        await f.InitializeAsync();
        await f.ExecuteSqlAsync("CREATE TRIGGER reject_audit BEFORE INSERT ON audit_events BEGIN SELECT RAISE(ABORT, 'test fault'); END;");
        var service = new HubSettingsService(new CommandGate(f.Store, f.Clock));
        await Assert.ThrowsAsync<HubException>(() => service.UpdateRetentionAsync(Guid.NewGuid(), Guid.NewGuid(), 0,
            new(30, 60), _ => Task.CompletedTask));
        Assert.False(f.Store.Ready);
        await f.ExecuteSqlAsync("DROP TRIGGER reject_audit;");
        using var restarted = f.Reopen();
        await restarted.InitializeAsync();
        Assert.Equal(0, await restarted.ReadAsync((db, ct) => db.Commands.CountAsync(ct)));
        Assert.Equal(0, await restarted.ReadAsync((db, ct) => db.Audit.CountAsync(ct)));
        Assert.Equal(90, await restarted.ReadAsync((db, ct) => db.State.Select(x => x.AuditRetentionDays).SingleAsync(ct)));
    }

    [Fact]
    public async Task MissingDatabaseWithInstallationArtifactsFailsWithoutReplacement()
    {
        await using var f = new StoreFixture(); await f.InitializeAsync();
        File.Move(f.Paths.Database, f.Paths.Database + ".preserved");
        await Assert.ThrowsAnyAsync<Exception>(() => f.Store.ReadAsync((db, ct) => db.State.CountAsync(ct)));
        Assert.False(File.Exists(f.Paths.Database));
        using var restarted = f.Reopen();
        await Assert.ThrowsAnyAsync<Exception>(() => restarted.InitializeAsync());
        Assert.False(restarted.Ready); Assert.False(File.Exists(f.Paths.Database));
        Assert.Equal("storage_recovery_required", restarted.FailureCode);
    }
    [Fact]
    public async Task MismatchedInstallationIdentityFailsWithoutReplacingMarker()
    {
        await using var f = new StoreFixture(); await f.InitializeAsync();
        var marker = Path.Combine(f.Paths.DataDirectory, "installation.id");
        var wrong = Guid.NewGuid().ToString("N"); File.WriteAllText(marker, wrong);
        using var restarted = f.Reopen();
        await Assert.ThrowsAnyAsync<Exception>(() => restarted.InitializeAsync());
        Assert.Equal(wrong, File.ReadAllText(marker)); Assert.False(restarted.Ready);
    }
    [Fact]
    public async Task InterruptedMarkerCreationRecoversExistingDatabaseIdentity()
    {
        await using var f = new StoreFixture(); await f.InitializeAsync();
        var marker = Path.Combine(f.Paths.DataDirectory, "installation.id"); var expected = File.ReadAllText(marker); File.Delete(marker);
        using var restarted = f.Reopen(); await restarted.InitializeAsync();
        Assert.True(restarted.Ready); Assert.Equal(expected, File.ReadAllText(marker));
    }
    [Fact]
    public async Task FutureSchemaIsRejectedRatherThanDowngraded()
    {
        await using var f = new StoreFixture();
        await f.InitializeAsync();
        await f.ExecuteSqlAsync("INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES ('20990101000000_Future', '10.0.11');");
        using var restarted = f.Reopen();
        await Assert.ThrowsAnyAsync<Exception>(() => restarted.InitializeAsync());
        Assert.False(restarted.Ready);
    }

    [Fact]
    public async Task SecretEncryptedNoReadbackAndRetryBoundToSecretWithoutPlaintextHash()
    {
        await using var f = new StoreFixture();
        await f.InitializeAsync();
        var service = new IntegrationSecretService(f.Store, new CommandGate(f.Store, f.Clock), f.Clock);
        var actor = Guid.NewGuid(); var command = Guid.NewGuid();
        const string secret = "unique-very-sensitive-discord-token";
        var response = await service.ReplaceAsync(actor, command, 0, "discord.botToken", secret, _ => Task.CompletedTask);
        await service.ReplaceAsync(actor, command, 0, "discord.botToken", secret, _ => throw new Exception("Replay must skip proof"));
        Assert.Equal("{\"configured\":true}", response.GetRawText());
        var ciphertext = await f.Store.ReadAsync((db, ct) => db.Secrets.Where(x => x.Name == "discord.botToken").Select(x => x.Ciphertext).SingleAsync(ct));
        Assert.DoesNotContain(secret, ciphertext);
        var ledger = await f.Store.ReadAsync((db, ct) => db.Commands.Select(x => x.PayloadHash + x.ResultJson).SingleAsync(ct));
        Assert.DoesNotContain(secret, ledger);
        Assert.DoesNotContain(CanonicalJson.Hash(secret), ledger);
        using var restarted = f.Reopen();
        await restarted.InitializeAsync();
        Assert.True(restarted.Ready);
        var conflict = await Assert.ThrowsAsync<HubException>(() => service.ReplaceAsync(actor, command, 0,
            "discord.botToken", "different-secret", _ => Task.CompletedTask));
        Assert.Equal("command_conflict", conflict.Code);
    }

    [Fact]
    public async Task MissingKeysNeverRegenerateOverEncryptedData()
    {
        await using var f = new StoreFixture();
        await f.InitializeAsync();
        foreach (var file in Directory.GetFiles(f.Paths.Keys)) File.Delete(file);
        using var restarted = f.Reopen();
        await Assert.ThrowsAnyAsync<Exception>(() => restarted.InitializeAsync());
        Assert.False(restarted.Ready);
        Assert.Empty(Directory.GetFiles(f.Paths.Keys));
    }

    [Fact]
    public async Task CorruptDatabaseDoesNotGetRecreated()
    {
        await using var f = new StoreFixture();
        Directory.CreateDirectory(f.Paths.DataDirectory);
        byte[] bytes = [1, 2, 3, 4, 5];
        await File.WriteAllBytesAsync(f.Paths.Database, bytes);
        await Assert.ThrowsAnyAsync<Exception>(() => f.InitializeAsync());
        Assert.False(f.Store.Ready);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(f.Paths.Database));
    }

    [Theory]
    [InlineData("{\"password\":\"never-store-me\"}")]
    [InlineData("{\"before\":{\"Password\":\"never-store-me\"}}")]
    [InlineData("{\"reason\":{\"pairingCode\":\"123456\"}}")]
    public async Task SecretMetadataRejectedBeforePersistence(string metadata)
    {
        await using var f = new StoreFixture();
        await f.InitializeAsync();
        await Assert.ThrowsAsync<HubException>(() => f.Store.AppendAuditAsync(Event() with
        { Metadata = JsonSerializer.Deserialize<JsonElement>(metadata) }));
        Assert.Equal(0, await f.Store.ReadAsync((db, ct) => db.Audit.CountAsync(ct)));
    }
}
