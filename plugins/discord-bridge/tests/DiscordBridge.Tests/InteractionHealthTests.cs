using Jellyfin.Plugin.DiscordBridge;
using Xunit;

namespace DiscordBridge.Tests;

public sealed class InteractionHealthTests
{
    [Fact]
    public async Task MetricsAreBoundedConcurrentAndNeverAcceptIdentityOrPayloadFields()
    {
        var metrics = new InteractionHealth();
        await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => Task.Run(() => metrics.Record("help", "completed"))));
        Assert.Equal(100, metrics.Snapshot()["help:completed"]);
        Assert.Throws<ArgumentException>(() => metrics.Record("user-123456789", "completed"));
        Assert.Throws<ArgumentException>(() => metrics.Record("help", "private-message-body"));
        Assert.Single(metrics.Snapshot());
    }
}
