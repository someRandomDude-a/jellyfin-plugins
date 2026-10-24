using System.Text;
using System.Text.Json;
using AccountHub.Core;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AccountHub.Tests;

public sealed class InviteOnboardingTests
{
    private sealed class Native : IInviteNativeProvisioner
    {
        public int Creates;
        public string? Fail;
        public Func<Task>? BeforePolicy;
        public readonly HashSet<Guid> Disabled = [];
        public readonly HashSet<Guid> Passwords = [];
        public readonly HashSet<Guid> Enabled = [];
        public Task CreateDisabledAsync(Guid id, string name, CancellationToken ct)
        {
            Interlocked.Increment(ref Creates); Disabled.Add(id);
            if (Fail == "create") throw new IOException("lost response after native commit");
            return Task.CompletedTask;
        }
        public Task SetChosenPasswordAsync(Guid id, string password, CancellationToken ct)
        {
            Assert.Contains(id, Disabled); Assert.NotEmpty(password); Passwords.Add(id);
            if (Fail == "password") throw new IOException("lost response after password commit");
            return Task.CompletedTask;
        }
        public async Task ApplyPolicyAsync(Guid id, string policy, string? group, CancellationToken ct)
        {
            Assert.Contains(id, Disabled); Assert.Contains(id, Passwords);
            if (BeforePolicy is not null) await BeforePolicy();
            if (Fail == "policy") throw new IOException("policy failed");
        }
        public Task EnableAsync(Guid id, CancellationToken ct)
        {
            Assert.Contains(id, Passwords); Enabled.Add(id);
            if (Fail == "enable") throw new IOException("lost response after enable commit");
            return Task.CompletedTask;
        }
        public Task<bool> IsReadyAsync(Guid id, CancellationToken ct) => Task.FromResult(Enabled.Contains(id));
        public Task<bool> IsIncompleteAsync(Guid id, string username, CancellationToken ct) => Task.FromResult(Disabled.Contains(id) && !Enabled.Contains(id));
    }
    private static InviteOnboardingService Service(HubStore store, Native native) => new(store, new CommandGate(store, TimeProvider.System), TimeProvider.System, native);
    private static Task Allow(CancellationToken _) => Task.CompletedTask;
    private static async Task<(Guid Id, string Raw)> Issue(HubStore store, InviteOnboardingService service, int uses = 1)
    {
        var actor = Guid.NewGuid();
        var revision = await store.ReadAsync((db, ct) => db.State.Select(x => x.Revision).SingleAsync(ct));
        var created = await service.CreateAsync(actor, Guid.NewGuid(), revision, new InviteRequest(MaxUses: uses), Allow);
        var id = created.GetProperty("id").GetGuid();
        var approved = await service.ChangeAsync(actor, Guid.NewGuid(), revision + 1, id, "approve", Allow);
        return (id, approved.Token!);
    }
    [Fact]
    public async Task MigrationAndConcurrentSingleSlotAreDurable()
    {
        await using var f = new StoreFixture(); await f.InitializeAsync();
        var native = new Native(); var service = Service(f.Store, native); var invite = await Issue(f.Store, service);
        var tasks = Enumerable.Range(0, 12).Select(async i =>
        {
            try { await service.RedeemAsync(invite.Raw, Guid.NewGuid(), "user" + i, "chosen-only"); return true; }
            catch (HubException) { return false; }
        });
        Assert.Single(await Task.WhenAll(tasks), x => x); Assert.Equal(1, native.Creates);
        using var reopened = f.Reopen(); await reopened.InitializeAsync();
        var row = await reopened.ReadAsync((db, ct) => db.OnboardingInvites.SingleAsync(ct));
        Assert.Equal(1, row.ReservedUses); Assert.Equal(1, row.CompletedUses); Assert.Equal("redeemed", row.Status);
        Assert.True((await reopened.VerifyAuditAsync()).Valid);
    }
    [Fact]
    public async Task MultiUseDoesNotOverbookAndHashesHaveNoReadback()
    {
        await using var f = new StoreFixture(); await f.InitializeAsync();
        var native = new Native(); var service = Service(f.Store, native); var invite = await Issue(f.Store, service, 3);
        for (var i = 0; i < 3; i++) await service.RedeemAsync(invite.Raw, Guid.NewGuid(), "user" + i, "do-not-store-me");
        await Assert.ThrowsAsync<HubException>(() => service.RedeemAsync(invite.Raw, Guid.NewGuid(), "fourth", "do-not-store-me"));
        Assert.Equal(3, native.Creates);
        var projections = JsonSerializer.Serialize(await service.ListAsync(0, 100)) + JsonSerializer.Serialize(await service.DetailAsync(invite.Id));
        Assert.DoesNotContain(invite.Raw, projections); Assert.DoesNotContain("TokenHash", projections);
        var commandAndAudit = await f.Store.ReadAsync(async (db, ct) => string.Join("", await db.Commands.Select(x => x.ResultJson).ToArrayAsync(ct)) + string.Join("", await db.Audit.Select(x => x.EventJson).ToArrayAsync(ct)));
        Assert.DoesNotContain(invite.Raw, commandAndAudit); Assert.DoesNotContain("do-not-store-me", commandAndAudit);
    }
    [Theory]
    [InlineData("create")]
    [InlineData("password")]
    [InlineData("policy")]
    [InlineData("enable")]
    public async Task FaultAndRestartNeverRecreatesOrReleasesReservation(string fail)
    {
        await using var f = new StoreFixture(); await f.InitializeAsync();
        var native = new Native { Fail = fail }; var service = Service(f.Store, native); var invite = await Issue(f.Store, service); var claim = Guid.NewGuid();
        var error = await Assert.ThrowsAsync<HubException>(() => service.RedeemAsync(invite.Raw, claim, "alice", "private-password")); Assert.Equal("remediation_pending", error.Code);
        using var reopened = f.Reopen(); await reopened.InitializeAsync();
        var restarted = Service(reopened, native);
        await restarted.RedeemAsync(invite.Raw, claim, "alice", "private-password");
        await Assert.ThrowsAsync<HubException>(() => restarted.RedeemAsync(invite.Raw, Guid.NewGuid(), "bob", "private-password"));
        Assert.Equal(1, native.Creates);
        var row = await reopened.ReadAsync((db, ct) => db.InviteProvisioning.SingleAsync(ct));
        await restarted.ReconcileAsync(row.Id);
        var final = await reopened.ReadAsync((db, ct) => db.InviteProvisioning.SingleAsync(ct));
        Assert.Equal(fail == "enable" ? "completed" : row.State, final.State);
        Assert.DoesNotContain("private-password", JsonSerializer.Serialize(final));
    }
    [Fact]
    public async Task RotationRevocationAndCommandRetriesNeverDiscloseStoredSecrets()
    {
        await using var f = new StoreFixture(); await f.InitializeAsync();
        var native = new Native(); var service = Service(f.Store, native); var invite = await Issue(f.Store, service);
        var actor = Guid.NewGuid(); var command = Guid.NewGuid();
        var rotated = await service.ChangeAsync(actor, command, 2, invite.Id, "resend", Allow);
        Assert.NotNull(rotated.Token);
        var retry = await service.ChangeAsync(actor, command, 2, invite.Id, "resend", _ => throw new Exception("must not consume proof twice"));
        Assert.Null(retry.Token);
        await Assert.ThrowsAsync<HubException>(() => service.RedeemAsync(invite.Raw, Guid.NewGuid(), "alice", "chosen"));
        await service.ChangeAsync(actor, Guid.NewGuid(), 3, invite.Id, "revoke", Allow);
        await Assert.ThrowsAsync<HubException>(() => service.RedeemAsync(rotated.Token!, Guid.NewGuid(), "alice", "chosen"));
        Assert.Equal(0, native.Creates);
    }
    [Fact]
    public async Task ReservedSlotLookupResumesOnlyMatchingClaimWithoutIdentityReadback()
    {
        await using var f = new StoreFixture(); await f.InitializeAsync();
        var native = new Native { Fail = "create" }; var service = Service(f.Store, native);
        var invite = await Issue(f.Store, service); var claim = Guid.NewGuid();
        await Assert.ThrowsAsync<HubException>(() => service.RedeemAsync(invite.Raw, claim, "private-alice", "private-password"));
        using var reopened = f.Reopen(); await reopened.InitializeAsync();
        var restarted = Service(reopened, native);
        foreach (var other in new Guid?[] { null, Guid.Empty, Guid.NewGuid() })
        {
            var lookup = JsonSerializer.SerializeToElement(await restarted.LookupAsync(invite.Raw, claimId: other));
            Assert.False(lookup.GetProperty("valid").GetBoolean()); Assert.False(lookup.GetProperty("resumable").GetBoolean());
        }
        var matching = JsonSerializer.SerializeToElement(await restarted.LookupAsync(invite.Raw, claimId: claim));
        Assert.True(matching.GetProperty("valid").GetBoolean()); Assert.True(matching.GetProperty("resumable").GetBoolean());
        Assert.Equal(new[] { "expiresAt", "resumable", "valid" }, matching.EnumerateObject().Select(x => x.Name).Order().ToArray());
        native.Fail = null;
        await restarted.ResumeAsync(invite.Raw, claim, "private-alice", "private-password");
        var completed = JsonSerializer.SerializeToElement(await restarted.LookupAsync(invite.Raw, claimId: claim));
        Assert.False(completed.GetProperty("valid").GetBoolean()); Assert.False(completed.GetProperty("resumable").GetBoolean());
        Assert.Equal(1, native.Creates);
    }
    [Fact]
    public async Task ExplicitResumeRecoversAHostTerminatedDuringRecoveryWithoutRecreatingAccount()
    {
        await using var f = new StoreFixture(); await f.InitializeAsync();
        var native = new Native { Fail = "password" }; var service = Service(f.Store, native);
        var invite = await Issue(f.Store, service); var claim = Guid.NewGuid();
        await Assert.ThrowsAsync<HubException>(() => service.RedeemAsync(invite.Raw, claim, "alice", "chosen-first"));
        await f.ExecuteSqlAsync("UPDATE invite_provisioning SET State = 'recovering'");
        using var reopened = f.Reopen(); await reopened.InitializeAsync(); native.Fail = null;
        var result = JsonSerializer.SerializeToElement(await Service(reopened, native).ResumeAsync(invite.Raw, claim, "alice", "freshly-chosen"));
        Assert.Equal("completed", result.GetProperty("status").GetString()); Assert.Equal(1, native.Creates);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommittedRevocationPreventsFinalEnableDuringRedemptionAndResume(bool resume)
    {
        await using var f = new StoreFixture(); await f.InitializeAsync();
        var native = new Native(); var service = Service(f.Store, native); var invite = await Issue(f.Store, service); var claim = Guid.NewGuid();
        if (resume)
        {
            native.Fail = "password";
            await Assert.ThrowsAsync<HubException>(() => service.RedeemAsync(invite.Raw, claim, "alice", "chosen")); native.Fail = null;
        }
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        native.BeforePolicy = async () => { reached.SetResult(); await release.Task; };
        var effect = resume ? service.ResumeAsync(invite.Raw, claim, "alice", "chosen") : service.RedeemAsync(invite.Raw, claim, "alice", "chosen");
        try
        {
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await service.ChangeAsync(Guid.NewGuid(), Guid.NewGuid(), 2, invite.Id, "revoke", Allow);
        }
        finally { release.TrySetResult(); }
        var result = JsonSerializer.SerializeToElement(await effect);
        Assert.Equal("remediation_pending", result.GetProperty("status").GetString()); Assert.Empty(native.Enabled);
        var row = await f.Store.ReadAsync((db, ct) => db.OnboardingInvites.SingleAsync(ct));
        Assert.Equal("revoked", row.Status); Assert.Equal(0, row.CompletedUses); Assert.Equal(1, native.Creates);
    }
    [Fact]
    public async Task ApprovalRequiredAndValidationAreEnforced()
    {
        await using var f = new StoreFixture(); await f.InitializeAsync();
        var service = Service(f.Store, new Native());
        await Assert.ThrowsAsync<HubException>(() => service.CreateAsync(Guid.NewGuid(), Guid.NewGuid(), 0, new InviteRequest(MaxUses: 101), Allow));
        await Assert.ThrowsAsync<HubException>(() => service.LookupAsync("query-secret"));
        var created = await service.CreateAsync(Guid.NewGuid(), Guid.NewGuid(), 0, new InviteRequest(), Allow);
        await Assert.ThrowsAsync<HubException>(() => service.ChangeAsync(Guid.NewGuid(), Guid.NewGuid(), 1, created.GetProperty("id").GetGuid(), "resend", Allow));
    }
}
