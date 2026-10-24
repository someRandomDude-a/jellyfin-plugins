using System.Text.Json;
using AccountHub.Core;
using Xunit;

namespace AccountHub.Tests;

public sealed class UserCreationOwnershipTests
{
    [Fact]
    public async Task NativeIsBlockedByDefaultAndOverridePausesHubForExactlyFiveMinutes()
    {
        var clock = new TestClock(); var policy = new UserCreationOwnership(clock);
        Assert.Equal("use_account_hub", (await Assert.ThrowsAsync<HubException>(() => policy.RunAsync(true, () => Task.FromResult(true), default))).Code);
        Assert.True(await policy.RunAsync(false, () => Task.FromResult(true), default));
        await policy.SetNativeOverrideAsync(true, default);
        Assert.Throws<HubException>(policy.RequireHub);
        Assert.True(await policy.RunAsync(true, () => Task.FromResult(true), default));
        await Assert.ThrowsAsync<HubException>(() => policy.RunAsync(false, () => Task.FromResult(true), default));
        clock.Now = clock.Now.AddMinutes(5);
        policy.RequireHub(); Assert.True(await policy.RunAsync(false, () => Task.FromResult(true), default));
        Assert.Equal("hub", JsonSerializer.SerializeToElement(policy.Snapshot).GetProperty("owner").GetString());
    }
    [Fact]
    public async Task SwitchingOwnerDrainsHubWorkflowAndNestedCreationDoesNotDeadlock()
    {
        var policy = new UserCreationOwnership(new TestClock());
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workflow = policy.RunAsync(false, async () =>
        {
            Assert.True(await policy.RunAsync(false, () => Task.FromResult(true), default));
            reached.SetResult(); await release.Task; return true;
        }, default);
        await reached.Task;
        var change = policy.SetNativeOverrideAsync(true, default);
        Assert.False(change.IsCompleted); release.SetResult(); await workflow; await change;
        Assert.Throws<HubException>(policy.RequireHub);
        await policy.SetNativeOverrideAsync(false, default);
        policy.RequireHub();
    }
    [Fact]
    public async Task ExpiryDoesNotAllowHubToOverlapAnAdmittedNativeRequest()
    {
        var clock = new TestClock(); var policy = new UserCreationOwnership(clock);
        await policy.SetNativeOverrideAsync(true, default);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var native = policy.RunAsync(true, async () => { reached.SetResult(); await release.Task; return true; }, default);
        await reached.Task; clock.Now = clock.Now.AddMinutes(5);
        var hub = policy.RunAsync(false, () => Task.FromResult(true), default);
        Assert.False(hub.IsCompleted); release.SetResult(); await native; Assert.True(await hub);
        Assert.Equal("hub", JsonSerializer.SerializeToElement(new UserCreationOwnership(clock).Snapshot).GetProperty("owner").GetString());
    }
}
