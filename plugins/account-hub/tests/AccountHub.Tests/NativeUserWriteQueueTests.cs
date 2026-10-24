using AccountHub.Core;
using Xunit;

namespace AccountHub.Tests;

public sealed class NativeUserWriteQueueTests
{
    [Fact]
    public async Task HubWritesNeverOverlapAndFailureReleasesTheQueue()
    {
        var queue = new NativeUserWrites(null!, null!); var active = 0; var completed = 0;
        await Task.WhenAll(Enumerable.Range(0, 100).Select(i => queue.SerializeAsync(async ct =>
        {
            Assert.Equal(1, Interlocked.Increment(ref active));
            await Task.Yield(); Interlocked.Increment(ref completed); Interlocked.Decrement(ref active); return true;
        }, default)));
        Assert.Equal(100, completed);
        await Assert.ThrowsAsync<IOException>(() => queue.SerializeAsync<bool>(_ => throw new IOException(), default));
        Assert.True(await queue.SerializeAsync(_ => Task.FromResult(true), default));
    }
    [Fact]
    public async Task FullQueueRejectsAdditionalWorkWithoutEffectsAndCancellationDoesNotLeakCapacity()
    {
        var queue = new NativeUserWrites(null!, null!);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 128).Select(_ => queue.SerializeAsync(async ct => { await release.Task; return true; }, default)).ToArray();
        try
        {
            var failure = await Assert.ThrowsAsync<HubException>(() => queue.SerializeAsync(_ => Task.FromResult(true), default));
            Assert.Equal("native_write_queue_full", failure.Code);
        }
        finally { release.TrySetResult(); }
        await Task.WhenAll(tasks);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queue.SerializeAsync(_ => Task.FromResult(true), cancelled.Token));
        Assert.True(await queue.SerializeAsync(_ => Task.FromResult(true), default));
    }
}
