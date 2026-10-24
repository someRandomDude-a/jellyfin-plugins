using AccountHub.Api;
using Xunit;

namespace AccountHub.Tests;

public sealed class DomainRouteTests
{
    [Theory]
    [InlineData("settings", "PUT", true)]
    [InlineData("policy", "PUT", true)]
    [InlineData("policy/preview", "POST", true)]
    [InlineData("policy/reset", "POST", true)]
    [InlineData("settings", "DELETE", false)]
    [InlineData("policy/reset", "PUT", false)]
    [InlineData("status", "POST", false)]
    [InlineData("parties/create", "POST", false)]
    [InlineData("proposals/delete", "POST", false)]
    [InlineData("../settings", "PUT", false)]
    public void OnlyExactNativePolicyOperationsAreExposed(string route, string method, bool expected) =>
        Assert.Equal(expected, DomainsController.AllowedCommand(route, method));

    [Fact] public void PerUserRoutesCannotReachPartyOrMediaActions()
    {
        var user = Guid.NewGuid();
        Assert.True(DomainsController.AllowedCommand($"users/{user:D}/policy", "DELETE"));
        Assert.True(DomainsController.AllowedCommand($"users/{user:D}/policy", "PUT"));
        Assert.False(DomainsController.AllowedCommand($"users/{user:D}/effective-policy", "PUT"));
        Assert.False(DomainsController.AllowedCommand($"users/{user:D}/policy", "POST"));
    }
}
