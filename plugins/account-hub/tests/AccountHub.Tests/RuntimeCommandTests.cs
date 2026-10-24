using System.Text.Json;
using AccountHub.Core;
using AccountHub.Api;
using MediaBrowser.Model.Users;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AccountHub.Tests;

public sealed class RuntimeCommandTests
{
    [Fact]
    public async Task InterruptedEffectIsNotReplayedAfterRestart()
    {
        await using var f = new StoreFixture(); await f.InitializeAsync();
        var actor = Guid.NewGuid(); var id = Guid.NewGuid(); var calls = 0;
        var gate = new ExternalCommandGate(f.Store, f.Clock);
        await Assert.ThrowsAsync<IOException>(() => gate.ExecuteAsync(actor, "user.test", id, 0, new { enabled = false }, (_, _) => Task.CompletedTask,
            _ => { calls++; throw new IOException("external operation uncertain"); }));
        using var reopened = f.Reopen(); await reopened.InitializeAsync();
        var retry = await new ExternalCommandGate(reopened, f.Clock).ExecuteAsync(actor, "user.test", id, 0, new { enabled = false }, (_, _) => throw new Exception("Must not consume another proof"),
            _ => { calls++; return Task.FromResult(JsonSerializer.SerializeToElement(new { status = "applied" })); });
        Assert.Equal(1, calls); Assert.True(retry.GetProperty("remediationPending").GetBoolean());
        Assert.True((await reopened.VerifyAuditAsync()).Valid);
    }
    [Fact]
    public async Task DeniedEffectIsAuditedAsDeniedNotSuccessful()
    {
        await using var f = new StoreFixture(); await f.InitializeAsync();
        await new ExternalCommandGate(f.Store, f.Clock).ExecuteAsync(Guid.NewGuid(), "security.test", Guid.NewGuid(), 0, new { }, (_, _) => Task.CompletedTask,
            _ => Task.FromResult(JsonSerializer.SerializeToElement(new { failureCode = "confirmation_required", failureStatus = 400 })),
            auditMetadata: () => JsonSerializer.SerializeToElement(new { reason = "confirmation", fallbackReason = "security_missing" }));
        var rows = await f.Store.ReadAsync((db, ct) => db.Audit.OrderBy(a => a.Sequence).Select(a => a.EventJson).ToArrayAsync(ct));
        Assert.Contains("\"outcome\":\"denied\"", rows[^1]); Assert.Contains("security_missing", rows[^1]);
    }
    [Fact]
    public async Task AuthorizationDenialDoesNotRunOrClaimAnEffect()
    {
        await using var f = new StoreFixture(); await f.InitializeAsync(); var calls = 0;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new ExternalCommandGate(f.Store, f.Clock).ExecuteAsync(Guid.NewGuid(), "user.test", Guid.NewGuid(), 0, new { },
            (_, _) => throw new UnauthorizedAccessException(), _ => { calls++; return Task.FromResult(JsonSerializer.SerializeToElement(new { })); }));
        Assert.Equal(0, calls);
        Assert.Equal(0, await f.Store.ReadAsync((db, ct) => db.Commands.CountAsync(ct)));
        Assert.Contains("\"outcome\":\"denied\"", await f.Store.ReadAsync((db, ct) => db.Audit.Select(a => a.EventJson).SingleAsync(ct)));
    }
    [Fact]
    public void ManagedPolicyPreservesAllUnmanagedFields()
    {
        var library = Guid.NewGuid();
        var current = new UserPolicy { IsAdministrator = false, IsDisabled = true, EnableContentDeletion = true, AuthenticationProviderId = "provider", LoginAttemptsBeforeLockout = 12, MaxActiveSessions = 9, EnableAllFolders = false, EnabledFolders = [library] };
        var updated = ManagedPolicy.Apply(current, JsonSerializer.SerializeToElement(new { EnableMediaPlayback = false, MaxActiveSessions = 3 }));
        Assert.False(updated.EnableMediaPlayback); Assert.Equal(3, updated.MaxActiveSessions);
        Assert.True(updated.IsDisabled); Assert.True(updated.EnableContentDeletion); Assert.Equal("provider", updated.AuthenticationProviderId);
        Assert.Equal(12, updated.LoginAttemptsBeforeLockout); Assert.False(updated.EnableAllFolders); Assert.Equal([library], updated.EnabledFolders);
    }
    [Theory]
    [InlineData("{\"IsAdministrator\":true}")]
    [InlineData("{\"AuthenticationProviderId\":\"other\"}")]
    [InlineData("{\"MaxActiveSessions\":-1}")]
    public void UnmanagedAndOutOfRangePoliciesFailClosed(string json) => Assert.Throws<HubException>(() => ManagedPolicy.Apply(new UserPolicy(), JsonSerializer.Deserialize<JsonElement>(json)));
}
