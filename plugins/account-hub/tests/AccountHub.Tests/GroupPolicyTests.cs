using System.Text.Json;
using AccountHub.Api;
using AccountHub.Core;
using AccountHub.Groups;
using Jellyfin.Plugin.UserManager.Services;
using MediaBrowser.Model.Users;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AccountHub.Tests;

public sealed class GroupPolicyTests
{
    [Fact]public async Task CurrentPolicySchemaReopensWithAStaleMigrationLockWithoutDeletingIt(){
        await using var f=new StoreFixture();await f.InitializeAsync();var native=new Native();
        var original=new GroupPolicyService(f.Paths,native,f.Store,f.Clock);await original.GroupsAsync(0,50);
        await f.ExecuteSqlAsync("INSERT INTO __EFMigrationsLock (Id,Timestamp) VALUES(1,'2026-10-26T00:00:00Z')");
        using var budget=new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var reopened=new GroupPolicyService(f.Paths,native,f.Store,f.Clock);await reopened.GroupsAsync(0,50,budget.Token);
        await using var db=reopened.Open();Assert.Equal(1,await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS Value FROM __EFMigrationsLock WHERE Id=1").SingleAsync());
        Assert.Equal(0,native.Writes);
    }
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
    private sealed class Native : IGroupPolicyNative
    {
        public Dictionary<Guid, UserPolicy> Users { get; } = [];
        public int Writes;
        public bool FailAfterWrite;
        public Action? BeforeRead;
        public UserPolicy Read(Guid id) { BeforeRead?.Invoke(); return PolicyTools.Clone(Users[id]); }
        public Task WriteManagedAsync(Guid id, JsonElement patch, CancellationToken ct)
        {
            if (Users[id].IsAdministrator) throw new HubException("administrator_protected", "Excluded");
            Users[id] = ManagedPolicy.Apply(Users[id], patch); Writes++;
            if (FailAfterWrite) throw new IOException("Synthetic ambiguous native commit");
            return Task.CompletedTask;
        }
    }
    [Fact]
    public async Task DurablePrimaryGroupOverridesPrecedenceAndDriftWithoutAutomaticEffects()
    {
        await using var f = new StoreFixture(); await f.InitializeAsync(); var native = new Native(); var id = Guid.NewGuid();
        native.Users[id] = new UserPolicy { MaxActiveSessions = 2, EnableRemoteAccess = true, AuthenticationProviderId = "protected-provider", IsHidden = true };
        var service = new GroupPolicyService(f.Paths, native, f.Store, f.Clock); var group = Guid.NewGuid();
        await service.SaveGroupAsync(group, 0, "Family", "", Json(new { MaxActiveSessions = 4, EnableRemoteAccess = false }));
        await service.AssignAsync(id, 0, group, 1, Json(new { MaxActiveSessions = 6 }));
        var reopened = new GroupPolicyService(f.Paths, native, f.Store, f.Clock);
        var view = await reopened.DriftAsync(id);
        Assert.Equal(group, view.GroupId); Assert.Equal(6, view.Desired.GetProperty("MaxActiveSessions").GetInt32());
        Assert.False(view.Desired.GetProperty("EnableRemoteAccess").GetBoolean()); Assert.Equal(0, native.Writes);
        var outcomes = await reopened.ApplyAsync(Guid.NewGuid(), Guid.NewGuid(), [new(id, view.Revision, view.GroupRevision)], "apply");
        Assert.Equal("applied", Assert.Single(outcomes).Status); Assert.Empty((await reopened.DriftAsync(id)).ChangedFields);
        Assert.Equal("protected-provider", native.Users[id].AuthenticationProviderId); Assert.True(native.Users[id].IsHidden);
        native.Users[id].MaxActiveSessions = 9;
        Assert.Contains("MaxActiveSessions", (await reopened.DriftAsync(id)).ChangedFields); Assert.Equal(1, native.Writes);
        await using var db = service.Open(); Assert.Single(await db.Snapshots.ToArrayAsync());
    }
    [Fact]
    public async Task GroupUpdateRequiresRepreviewAndCannotIndirectlyRewritePromotedAdministrator()
    {
        await using var f = new StoreFixture(); await f.InitializeAsync(); var native = new Native(); var id = Guid.NewGuid(); native.Users[id] = new UserPolicy();
        var service = new GroupPolicyService(f.Paths, native, f.Store, f.Clock); var group = Guid.NewGuid();
        await service.SaveGroupAsync(group, 0, "Group", "", Json(new { MaxActiveSessions = 3 }));
        await service.AssignAsync(id, 0, group, 1, Json(new { }));
        await service.SaveGroupAsync(group, 1, "Group", "", Json(new { MaxActiveSessions = 5 }));
        Assert.Equal("revision_changed", Assert.Single(await service.ApplyAsync(Guid.NewGuid(), Guid.NewGuid(), [new(id, 1, 1)], "apply")).Status);
        native.Users[id].IsAdministrator = true;
        var view = await service.DriftAsync(id); Assert.Empty(view.ChangedFields);
        Assert.Equal("administrator_skipped", Assert.Single(await service.ApplyAsync(Guid.NewGuid(), Guid.NewGuid(), [new(id, 1, 2)], "reset")).Status);
        Assert.Equal(0, native.Writes);
    }
    [Fact]
    public async Task AdministratorPromotionIsRecheckedAfterSnapshotBeforeEffect()
    {
        await using var f = new StoreFixture(); await f.InitializeAsync(); var native = new Native(); var id = Guid.NewGuid(); native.Users[id] = new UserPolicy();
        var service = new GroupPolicyService(f.Paths, native, f.Store, f.Clock);
        await service.AssignAsync(id, 0, null, 0, Json(new { MaxActiveSessions = 3 }));
        var reads = 0; native.BeforeRead = () => { if (++reads == 2) native.Users[id].IsAdministrator = true; };
        var result = await service.ApplyAsync(Guid.NewGuid(), Guid.NewGuid(), [new(id, 1)], "apply");
        Assert.Equal("administrator_skipped", Assert.Single(result).Status); Assert.Equal(0, native.Writes);
    }
    [Fact]
    public async Task ConcurrentAssignmentHasOneWinnerAndGroupDeletionIsRestricted()
    {
        await using var f = new StoreFixture(); await f.InitializeAsync(); var native = new Native(); var id = Guid.NewGuid(); native.Users[id] = new UserPolicy();
        var service = new GroupPolicyService(f.Paths, native, f.Store, f.Clock); var group = Guid.NewGuid();
        await service.SaveGroupAsync(group, 0, "Group", "", Json(new { }));
        var first = service.AssignAsync(id, 0, group, 1, Json(new { MaxActiveSessions = 2 }));
        var second = service.AssignAsync(id, 0, group, 1, Json(new { MaxActiveSessions = 4 }));
        await first; Assert.Equal("revision_changed", (await Assert.ThrowsAsync<HubException>(() => second)).Code);
        Assert.Equal("rejected", (await Assert.ThrowsAsync<HubException>(() => service.DeleteGroupAsync(group, 1))).Code);
        Assert.Equal("rejected", (await Assert.ThrowsAsync<HubException>(() => service.SaveGroupAsync(Guid.NewGuid(), 0, "group", "", Json(new { })))).Code);
    }
    [Fact]
    public async Task ResetRestoresEnrollmentBaselineAndRollbackChangesIntentWithoutUnmanagedFields()
    {
        await using var f = new StoreFixture(); await f.InitializeAsync(); var native = new Native(); var id = Guid.NewGuid(); native.Users[id] = new UserPolicy { MaxActiveSessions = 7, IsHidden = true };
        var service = new GroupPolicyService(f.Paths, native, f.Store, f.Clock);
        await service.AssignAsync(id, 0, null, 0, Json(new { MaxActiveSessions = 2 }));
        var apply = Assert.Single(await service.ApplyAsync(Guid.NewGuid(), Guid.NewGuid(), [new(id, 1)], "apply"));
        Assert.Equal(2, native.Users[id].MaxActiveSessions);
        var preview = Assert.Single(await service.PreviewAsync([new(id, 1)], mode: "reset")); Assert.Equal(7, preview.Desired.GetProperty("MaxActiveSessions").GetInt32());
        await service.ApplyAsync(Guid.NewGuid(), Guid.NewGuid(), [new(id, 1)], "reset");
        Assert.Equal(7, native.Users[id].MaxActiveSessions); Assert.Equal(2, (await service.DriftAsync(id)).Revision);
        native.Users[id].MaxActiveSessions = 10;
        await service.ApplyAsync(Guid.NewGuid(), Guid.NewGuid(), [new(id, 2)], "rollback", apply.SnapshotId);
        Assert.Equal(7, native.Users[id].MaxActiveSessions); Assert.True(native.Users[id].IsHidden); Assert.Empty((await service.DriftAsync(id)).ChangedFields);
    }
    [Fact]
    public async Task FaultRetainsManagedSnapshotAndReportsPerUserPartialOutcome()
    {
        await using var f = new StoreFixture(); await f.InitializeAsync(); var native = new Native { FailAfterWrite = true }; var id = Guid.NewGuid(); var other = Guid.NewGuid();
        native.Users[id] = new UserPolicy { MaxActiveSessions = 1 }; native.Users[other] = new UserPolicy { IsAdministrator = true };
        var service = new GroupPolicyService(f.Paths, native, f.Store, f.Clock);
        await service.AssignAsync(id, 0, null, 0, Json(new { MaxActiveSessions = 2 }));
        var result = await service.ApplyAsync(Guid.NewGuid(), Guid.NewGuid(), [new(id, 1), new(other, 0)], "apply");
        Assert.Equal("remediation_pending", result[0].Status); Assert.Equal("administrator_skipped", result[1].Status);
        await using var db = service.Open(); var snapshot = Assert.Single(await db.Snapshots.ToArrayAsync());
        Assert.Equal("remediation_pending", snapshot.Outcome); Assert.Equal(1, JsonSerializer.Deserialize<JsonElement>(snapshot.BeforeJson).GetProperty("MaxActiveSessions").GetInt32());
        Assert.DoesNotContain("AuthenticationProvider", snapshot.BeforeJson); Assert.Equal(1, native.Writes);
    }
    [Theory]
    [InlineData("{\"IsAdministrator\":true}")]
    [InlineData("{\"AuthenticationProviderId\":\"other\"}")]
    [InlineData("{\"EnableRemoteAccess\":null}")]
    [InlineData("{\"EnabledFolders\":[null]}")]
    [InlineData("{\"MaxActiveSessions\":-1}")]
    [InlineData("{\"MaxParentalRating\":-1}")]
    [InlineData("{\"MaxActiveSessions\":1,\"MaxActiveSessions\":2}")]
    public void RejectsProtectedMalformedAndDuplicateFields(string patch) => Assert.ThrowsAny<Exception>(() => ManagedPolicy.Apply(new UserPolicy(), JsonSerializer.Deserialize<JsonElement>(patch)));
    [Fact]
    public async Task ExistingCommandGateRequiresAuthorizationOnceAndNeverReplaysAnAmbiguousEffect()
    {
        await using var f = new StoreFixture(); await f.InitializeAsync(); var native = new Native { FailAfterWrite = true }; var id = Guid.NewGuid(); native.Users[id] = new UserPolicy();
        var service = new GroupPolicyService(f.Paths, native, f.Store, f.Clock);
        await service.AssignAsync(id, 0, null, 0, Json(new { MaxActiveSessions = 2 }));
        var gate = new ExternalCommandGate(f.Store, f.Clock); var actor = Guid.NewGuid(); var command = Guid.NewGuid(); var authorized = 0;
        Task<JsonElement> Apply() => gate.ExecuteAsync(actor, "group-policy.apply", command, 0, new { userId = id },
            (_, _) => { authorized++; return Task.CompletedTask; }, async ct => Json(await service.ApplyAsync(actor, command, [new(id, 1)], "apply", ct: ct)));
        var first = await Apply(); var retry = await Apply();
        Assert.Equal(first.GetRawText(), retry.GetRawText()); Assert.Equal(1, authorized); Assert.Equal(1, native.Writes);
        await Assert.ThrowsAsync<HubException>(() => gate.ExecuteAsync(actor, "group-policy.apply", command, 0, new { userId = Guid.NewGuid() }, (_, _) => Task.CompletedTask, _ => Task.FromResult(Json(new { }))));
    }
    [Fact]
    public async Task BoundsAndSnapshotTargetValidationFailWithoutEffects()
    {
        await using var f = new StoreFixture(); await f.InitializeAsync(); var native = new Native(); var id = Guid.NewGuid(); native.Users[id] = new UserPolicy();
        var service = new GroupPolicyService(f.Paths, native, f.Store, f.Clock);
        await Assert.ThrowsAsync<HubException>(() => service.PreviewAsync([]));
        await Assert.ThrowsAsync<HubException>(() => service.PreviewAsync([new(id, 0), new(id, 0)]));
        await service.AssignAsync(id, 0, null, 0, Json(new { }));
        Assert.Equal("rejected", Assert.Single(await service.ApplyAsync(Guid.NewGuid(), Guid.NewGuid(), [new(id, 1)], "rollback", Guid.NewGuid())).Status);
        Assert.Equal(0, native.Writes);
    }
}
