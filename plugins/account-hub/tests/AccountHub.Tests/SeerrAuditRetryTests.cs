using AccountHub.Core.Seerr;
using AccountHub.Seerr;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AccountHub.Tests;

public sealed class SeerrAuditRetryTests
{
    [Fact]
    public async Task RealAuditAdapterAcceptsCommittedEventAfterRestartWithoutChangingTimestamp()
    {
        await using var f = new StoreFixture(); await f.InitializeAsync();
        var entry = new SeerrJobEvent(Guid.NewGuid(), Guid.NewGuid(), "seerr.provision.completed", "completed", null, DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds());
        await new SeerrEvents(f.Store).PublishAsync(entry, default);
        using var reopened = f.Reopen(); await reopened.InitializeAsync();
        await new SeerrEvents(reopened).PublishAsync(entry, default);
        Assert.Equal(1, await reopened.ReadAsync((db, ct) => db.Audit.CountAsync(ct)));
        Assert.True((await reopened.VerifyAuditAsync()).Valid);
    }
}
