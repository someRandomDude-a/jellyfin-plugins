using AccountHub.Core;
using Xunit;

namespace AccountHub.Tests;

public sealed class CommandAuthorizationReentryTests
{
    [Fact]
    public async Task ProofGateCanReadSettingsWithoutDeadlockingTheWriter()
    {
        await using var fixture = new StoreFixture(); await fixture.InitializeAsync();
        var settings = new HubSettingsService(new CommandGate(fixture.Store, fixture.Clock));
        await settings.UpdateRetentionAsync(Guid.NewGuid(), Guid.NewGuid(), 0, new(30, 30),
            async ct => { _ = await fixture.Store.ReadSecretAsync("missing", ct); }).WaitAsync(TimeSpan.FromSeconds(5));
    }
}
