using System.Text.Json;
using AccountHub.Core;
using AccountHub.Core.Seerr;
using AccountHub.Seerr;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AccountHub.Tests;

public sealed class SeerrLifecycleTests
{
    private static readonly SeerrDesired Desired = new(32, new(10, 7), new(null, null));
    [Fact] public async Task ProvisionUsesNativeIdentityAndUpstreamDefaultsWithoutInventingPolicy()
    {
        using var f = new Fixture(); await f.Discover();
        await f.Service.EnqueueAsync(Guid.NewGuid(), f.Actor, f.Id, null, true, default); await f.Service.TickAsync();
        Assert.Equal(1, f.Remote.Imports); Assert.Equal(0, f.Remote.Applies);
        var state = await f.Store.ReadAsync(); Assert.Equal(f.Id, Assert.Single(state.Links).UserId); Assert.Equal("completed", Assert.Single(state.Jobs).State);
    }
    [Fact] public async Task UncertainImportSurvivesRestartAndManualRetryNeverCreatesAgain()
    {
        using var f = new Fixture(); await f.Discover(); f.Remote.ImportFailure = true;
        var job = await f.Service.EnqueueAsync(Guid.NewGuid(), f.Actor, f.Id, Desired, true, default); await f.Service.TickAsync();
        Assert.True(Assert.Single((await f.Store.ReadAsync()).Jobs).ImportAttempted);
        f.Clock.Advance(); var restarted = f.NewService(); await restarted.TickAsync();
        Assert.Equal("seerr_create_uncertain", Assert.Single((await f.Store.ReadAsync()).Jobs).Error);
        await restarted.RetryAsync(job.Id, default); await restarted.TickAsync(); Assert.Equal(1, f.Remote.Imports);
    }
    [Fact] public async Task LostImportResponseRecoversOnlyWithImmutableIdentityEvidence()
    {
        using var f = new Fixture(); await f.Discover(); f.Remote.ImportFailure = true; f.Remote.CommitBeforeFailure = true;
        await f.Service.EnqueueAsync(Guid.NewGuid(), f.Actor, f.Id, Desired, true, default); await f.Service.TickAsync();
        f.Clock.Advance(); await f.NewService().TickAsync();
        Assert.Equal(1, f.Remote.Imports); Assert.Equal("completed", Assert.Single((await f.Store.ReadAsync()).Jobs).State); Assert.Equal(Desired, f.Remote.Policy);
    }
    [Fact] public async Task PersistedClaimIsNotReplayedBeforeLeaseExpiry()
    {
        using var f = new Fixture(); await f.Discover();
        var job = await f.Service.EnqueueAsync(Guid.NewGuid(), f.Actor, f.Id, null, true, default);
        await f.Store.UpdateAsync(s => { var j = s.Jobs.Single(); j.State = "running"; j.Due = f.Clock.GetUtcNow().ToUnixTimeMilliseconds() + 120000; j.Updated = f.Clock.GetUtcNow().ToUnixTimeMilliseconds(); j.ImportAttempted = true; return true; });
        await f.NewService().TickAsync(); Assert.Equal(0, f.Remote.Imports);
        f.Clock.Advance(); await f.NewService().TickAsync(); Assert.Equal(0, f.Remote.Imports); Assert.Equal("blocked", Assert.Single((await f.Store.ReadAsync()).Jobs).State);
    }
    [Fact] public async Task LinkRequiresImmutableEvidenceNotMatchingName()
    {
        using var f = new Fixture(); f.Remote.Account = new(8, Guid.NewGuid(), 32, "created", "native-name");
        var ex = await Assert.ThrowsAsync<HubException>(() => f.Service.LinkAsync(f.Id, 8, default)); Assert.Equal("seerr_identity_mismatch", ex.Code);
        Assert.Empty((await f.Store.ReadAsync()).Links);
    }
    [Theory] [InlineData(1, 32)] [InlineData(8, 2)]
    public async Task UpstreamOwnerAndAdminNeverLinkedOrMutated(int id, long permissions)
    {
        using var f = new Fixture(); f.Remote.Account = new(id, f.Id, permissions, "created");
        Assert.Equal("administrator_protected", (await Assert.ThrowsAsync<HubException>(() => f.Service.LinkAsync(f.Id, id, default))).Code);
        Assert.Equal(0, f.Remote.Applies + f.Remote.Deletes);
    }
    [Fact] public async Task NativeAdministratorIsRecheckedAtExecution()
    {
        using var f = new Fixture(); await f.Link();
        await f.Service.EnqueueAsync(Guid.NewGuid(), f.Actor, f.Id, Desired, false, default);
        f.Directory.User = f.Directory.User! with { Administrator = true }; await f.Service.TickAsync();
        Assert.Equal(0, f.Remote.Applies); Assert.Equal("administrator_protected", Assert.Single((await f.Store.ReadAsync()).Jobs).Error);
    }
    [Fact] public async Task NativeDisabledCannotProvision()
    {
        using var f = new Fixture(); await f.Discover(); f.Directory.User = f.Directory.User! with { Disabled = true };
        await Assert.ThrowsAsync<HubException>(() => f.Service.EnqueueAsync(Guid.NewGuid(), f.Actor, f.Id, Desired, true, default)); Assert.Equal(0, f.Remote.Imports);
    }
    [Fact] public async Task DeleteIntentSurvivesRestartAndWaitsForNativeAbsence()
    {
        using var f = new Fixture(); await f.Link();
        await f.Service.PrepareDeletionAsync(Guid.NewGuid(), f.Actor, f.Id); await f.NewService().TickAsync(); Assert.Equal(0, f.Remote.Deletes);
        f.Directory.User = null; await f.NewService().TickAsync(); Assert.Equal(1, f.Remote.Deletes); Assert.Empty((await f.Store.ReadAsync()).Links);
    }
    [Fact] public async Task ExternalNativeDeletionIsReconciledUsingSavedLinkOnly()
    {
        using var f = new Fixture(); await f.Link(); f.Directory.User = null; await f.Service.TickAsync();
        Assert.Equal(1, f.Remote.Deletes); Assert.Equal("delete", Assert.Single((await f.Store.ReadAsync()).Jobs).Kind);
    }
    [Fact] public async Task MissingSeerrDeleteIsIdempotentCompletion()
    {
        using var f = new Fixture(); await f.Link(); await f.Service.PrepareDeletionAsync(Guid.NewGuid(), f.Actor, f.Id); f.Directory.User = null; f.Remote.Account = null;
        await f.Service.TickAsync(); Assert.Equal(0, f.Remote.Deletes); Assert.Equal("completed", Assert.Single((await f.Store.ReadAsync()).Jobs).State);
    }
    [Theory] [InlineData("connection")] [InlineData("native-id")] [InlineData("created-at")] [InlineData("admin")]
    public async Task ChangedDeletionTargetStopsForRemediation(string fault)
    {
        using var f = new Fixture(); await f.Link(); await f.Service.PrepareDeletionAsync(Guid.NewGuid(), f.Actor, f.Id); f.Directory.User = null;
        if (fault == "connection") f.Remote.Capabilities = f.Remote.Capabilities with { Fingerprint = "another-instance" };
        else if (fault == "native-id") f.Remote.Account = f.Remote.Account! with { NativeId = Guid.NewGuid() };
        else if (fault == "created-at") f.Remote.Account = f.Remote.Account! with { CreatedAt = "reused-id" };
        else f.Remote.Account = f.Remote.Account! with { Permissions = 2 };
        await f.Service.TickAsync(); Assert.Equal(0, f.Remote.Deletes); Assert.Equal("blocked", Assert.Single((await f.Store.ReadAsync()).Jobs).State); Assert.Single((await f.Store.ReadAsync()).Links);
    }
    [Fact] public async Task MissingLinkedAccountIsNotAutomaticallyRecreated()
    {
        using var f = new Fixture(); await f.Link(); await f.Service.EnqueueAsync(Guid.NewGuid(), f.Actor, f.Id, Desired, false, default); f.Remote.Account = null;
        await f.Service.TickAsync(); Assert.Equal(0, f.Remote.Imports); Assert.Equal("seerr_account_missing", Assert.Single((await f.Store.ReadAsync()).Jobs).Error);
    }
    [Fact] public async Task UnsafeUpstreamDefaultsCannotProvisionAnAdministrator()
    {
        using var f = new Fixture(); f.Remote.Capabilities = f.Remote.Capabilities with { DefaultPermissions = 2 }; await f.Discover();
        await f.Service.EnqueueAsync(Guid.NewGuid(), f.Actor, f.Id, null, true, default); await f.Service.TickAsync(); Assert.Equal(0, f.Remote.Imports);
    }
    [Fact] public async Task OutageHasBoundedDurableRetriesAndNoRawErrorStorage()
    {
        using var f = new Fixture(); await f.Discover(); f.Remote.Offline = true;
        await f.Service.EnqueueAsync(Guid.NewGuid(), f.Actor, f.Id, null, true, default);
        for (var i = 0; i < 12; i++) { await f.NewService().TickAsync(); f.Clock.Advance(); }
        var job = Assert.Single((await f.Store.ReadAsync()).Jobs); Assert.Equal(8, job.Attempts); Assert.Equal("failed", job.State); Assert.Equal("seerr_unavailable", job.Error); Assert.Equal(0, f.Remote.Imports);
    }
    [Fact] public async Task AuditDeliveryFailureDoesNotReplayEffectAndRetainsStableEventId()
    {
        using var f = new Fixture(); await f.Discover(); f.Events.Fail = true;
        await f.Service.EnqueueAsync(Guid.NewGuid(), f.Actor, f.Id, null, true, default);
        await Assert.ThrowsAsync<IOException>(() => f.Service.TickAsync()); var first = Assert.Single(f.Events.Entries);
        f.Events.Fail = false; await f.NewService().TickAsync(); Assert.Equal(first.EventId, f.Events.Entries.Last().EventId); Assert.Equal(1, f.Remote.Imports);
    }
    [Fact] public async Task DuplicateJobCommandIsIdempotentButDifferentActorConflicts()
    {
        using var f = new Fixture(); await f.Discover(); var id = Guid.NewGuid();
        await f.Service.EnqueueAsync(id, f.Actor, f.Id, Desired, true, default); await f.Service.EnqueueAsync(id, f.Actor, f.Id, Desired, true, default);
        Assert.Single((await f.Store.ReadAsync()).Jobs);
        Assert.Equal("command_conflict", (await Assert.ThrowsAsync<HubException>(() => f.Service.EnqueueAsync(id, Guid.NewGuid(), f.Id, Desired, true, default))).Code);
    }
    [Fact] public async Task AuditAcknowledgementCannotEraseConcurrentRetryAndCancellation()
    {
        using var f = new Fixture(); await f.Link();
        var job = await f.Service.EnqueueAsync(Guid.NewGuid(), f.Actor, f.Id, Desired, false, default);
        f.Remote.Account = null;
        f.Events.OnPublish = async () =>
        {
            f.Events.OnPublish = null;
            await f.Service.RetryAsync(job.Id, default);
            await f.Service.CancelAsync(job.Id, default);
        };
        await f.Service.TickAsync();
        Assert.Equal("blocked", Assert.Single(f.Events.Entries).State);
        var pending = Assert.Single((await f.Store.ReadAsync()).AuditOutbox);
        Assert.Equal("cancelled", pending.Event.State);
        Assert.NotEqual(f.Events.Entries[0].EventId, pending.Event.EventId);
        await f.NewService().TickAsync();
        Assert.Equal("cancelled", f.Events.Entries.Last().State);
        Assert.Empty((await f.Store.ReadAsync()).AuditOutbox);
    }
    [Fact] public async Task FailedAuditSnapshotSurvivesRetryBeforeRestart()
    {
        using var f = new Fixture(); await f.Link();
        var job = await f.Service.EnqueueAsync(Guid.NewGuid(), f.Actor, f.Id, Desired, false, default);
        f.Remote.Account = null; f.Events.Fail = true;
        await Assert.ThrowsAsync<IOException>(() => f.Service.TickAsync());
        var original = Assert.Single(f.Events.Entries);
        await f.Service.RetryAsync(job.Id, default);
        await f.Service.CancelAsync(job.Id, default);
        Assert.Equal(2, (await f.Store.ReadAsync()).AuditOutbox.Count);
        f.Events.Fail = false; await f.NewService().TickAsync();
        Assert.Equal(original, f.Events.Entries[1]);
        Assert.Equal("cancelled", f.Events.Entries[2].State);
        Assert.Empty((await f.Store.ReadAsync()).AuditOutbox);
    }
    [Fact] public async Task ActiveJobPreventsUnlinkOrConflictingDesiredState()
    {
        using var f = new Fixture(); await f.Link(); await f.Service.EnqueueAsync(Guid.NewGuid(), f.Actor, f.Id, Desired, false, default);
        await Assert.ThrowsAsync<HubException>(() => f.Service.UnlinkAsync(f.Id, default));
        await Assert.ThrowsAsync<HubException>(() => f.Service.EnqueueAsync(Guid.NewGuid(), f.Actor, f.Id, Desired, false, default));
    }
    [Fact] public async Task NamedSchemaDoesNotChangeUserVersionAndRejectsFutureSchema()
    {
        using var f = new Fixture(); await f.Discover();
        await using var db = new SqliteConnection("Data Source=" + f.Paths.Database); await db.OpenAsync();
        var cmd = db.CreateCommand(); cmd.CommandText = "PRAGMA user_version"; Assert.Equal(0L, await cmd.ExecuteScalarAsync());
        cmd.CommandText = "UPDATE hub_seerr_schema SET version=2"; await cmd.ExecuteNonQueryAsync();
        Assert.Equal("seerr_schema_incompatible", (await Assert.ThrowsAsync<HubException>(() => f.Store.ReadAsync())).Code);
    }
    [Fact] public void QuotasAndPermissionsRejectUnknownDesiredPrivilegeAndInvalidBounds()
    {
        Assert.Throws<HubException>(() => SeerrLifecycle.Validate(Desired with { Permissions = 2 }));
        Assert.Throws<HubException>(() => SeerrLifecycle.Validate(Desired with { Permissions = 1L << 29 }));
        Assert.Throws<HubException>(() => SeerrLifecycle.Validate(Desired with { MovieQuota = new(4, null) }));
        Assert.Throws<HubException>(() => SeerrLifecycle.Validate(Desired with { MovieQuota = new(4, 366) }));
        SeerrLifecycle.Validate(Desired with { MovieQuota = new(0, 7) });
    }
    [Fact] public void QuotaProfilePreservesNativeProfileButNotArbitrarySecrets()
    {
        var profile = JsonSerializer.SerializeToElement(new { username = "native", email = "test", locale = "fr", watchlistSyncTv = true, apiKey = "SECRET", globalMovieQuotaLimit = 200 });
        var output = SeerrUpstream.PreserveQuotaProfile(profile, Desired);
        Assert.Equal("native", output.GetProperty("username").GetString()); Assert.True(output.GetProperty("watchlistSyncTv").GetBoolean());
        Assert.Equal(10, output.GetProperty("movieQuotaLimit").GetInt32()); Assert.Equal(JsonValueKind.Null, output.GetProperty("tvQuotaDays").ValueKind);
        Assert.DoesNotContain("SECRET", output.GetRawText()); Assert.False(output.TryGetProperty("globalMovieQuotaLimit", out _));
    }
    [Fact] public void RequestHistoryProjectionContainsNoRequesterOrCommenterIdentity()
    {
        var output = SeerrUpstream.ProjectRequests(JsonSerializer.SerializeToElement(new { results = new[] { new { id = 1, status = 2, type = "movie", createdAt = "date", requestedBy = new { email = "PRIVATE", username = "PRIVATE" }, modifiedBy = "PRIVATE", comments = "PRIVATE", media = new { title = "PRIVATE", requester = "PRIVATE" } } } }));
        Assert.DoesNotContain("PRIVATE", output.GetRawText()); Assert.DoesNotContain("requestedBy", output.GetRawText()); Assert.Equal(1, output.GetProperty("items").GetArrayLength());
    }
    [Fact] public void AmbiguousEnhancedDestinationsAreNotGuessed() => Assert.Null(SeerrConnectionDiscovery.ReadPublicConfiguration(new { JellyseerrEnabled = true, JellyseerrUrls = "https://one.example,https://two.example", JellyseerrApiKey = "PRIVATE" }));

    private sealed class Fixture : IDisposable
    {
        public Guid Id { get; } = Guid.NewGuid(); public Guid Actor { get; } = Guid.NewGuid();
        public HubPaths Paths { get; } = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp", "opencode", "seerr-tests-" + Guid.NewGuid().ToString("N")));
        public readonly Clock Clock = new(); public readonly DirectoryFake Directory = new(); public readonly RemoteFake Remote = new(); public readonly EventsFake Events = new();
        public SeerrStore Store { get; } public SeerrLifecycle Service { get; }
        public Fixture() { Store = new(Paths); Directory.User = new(Id, "native-name", false, false); Remote.NativeId = Id; Service = NewService(); }
        public SeerrLifecycle NewService() => new(Store, Remote, Directory, Events, Clock);
        public Task Discover() => Service.DiscoverAsync();
        public async Task Link() { Remote.Account = new(8, Id, 32, "created"); await Service.LinkAsync(Id, 8, default); }
        public void Dispose() { Store.Dispose(); SqliteConnection.ClearAllPools(); if (System.IO.Directory.Exists(Paths.DataDirectory)) System.IO.Directory.Delete(Paths.DataDirectory, true); }
    }
    private sealed class Clock : TimeProvider { private DateTimeOffset now = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => now; public void Advance() => now += TimeSpan.FromHours(3); }
    private sealed class DirectoryFake : ISeerrDirectory { public SeerrNativeUser? User; public SeerrNativeUser? Find(Guid id) => User?.Id == id ? User : null; }
    private sealed class EventsFake : ISeerrEvents { public bool Fail; public Func<Task>? OnPublish; public readonly List<SeerrJobEvent> Entries = []; public async Task PublishAsync(SeerrJobEvent entry, CancellationToken ct) { Entries.Add(entry); if (OnPublish is not null) await OnPublish(); if (Fail) throw new IOException("secret upstream content"); } }
    private sealed class RemoteFake : ISeerrUpstream, ISeerrSession
    {
        public SeerrCapabilities Capabilities { get; set; } = new("instance", "3.5.1", 32, new(null, null), new(null, null), "override");
        public SeerrAccount? Account; public Guid NativeId; public bool Offline, ImportFailure, CommitBeforeFailure; public int Imports, Applies, Deletes; public SeerrDesired Policy = Desired;
        public Task<ISeerrSession> OpenAsync(CancellationToken ct) { if (Offline) throw new IOException("API KEY SECRET"); return Task.FromResult<ISeerrSession>(this); }
        public Task<SeerrAccount?> FindNativeAsync(Guid id, CancellationToken ct) => Task.FromResult(Account?.NativeId == id ? Account : null);
        public Task<SeerrAccount?> FindAsync(int id, CancellationToken ct) => Task.FromResult(Account?.Id == id ? Account : null);
        public Task ImportAsync(Guid id, CancellationToken ct) { Imports++; if (!ImportFailure || CommitBeforeFailure) Account = new(8, id, 32, "created"); if (ImportFailure) throw new IOException("uncertain import"); return Task.CompletedTask; }
        public Task<SeerrDesired> ReadDesiredAsync(int id, CancellationToken ct) => Task.FromResult(Policy);
        public Task ApplyAsync(int id, Guid nativeId, string createdAt, SeerrDesired desired, CancellationToken ct) { Applies++; Policy = desired; return Task.CompletedTask; }
        public Task DeleteAsync(int id, Guid nativeId, string createdAt, CancellationToken ct) { Deletes++; Account = null; return Task.CompletedTask; }
        public Task<JsonElement> RequestsAsync(int id, int skip, int limit, CancellationToken ct) => Task.FromResult(JsonSerializer.SerializeToElement(new { items = Array.Empty<object>() }));
    }
}
