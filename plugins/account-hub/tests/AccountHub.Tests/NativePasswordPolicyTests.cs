using AccountHub.Core;
using Xunit;

namespace AccountHub.Tests;

public sealed class NativePasswordPolicyTests
{
    public sealed class NativeConfig { public bool DisablePasswordLogin { get; set; } public string Secret => throw new InvalidOperationException("Must never read unrelated configuration"); }
    public sealed class BrokenConfig { public bool DisablePasswordLogin => throw new InvalidOperationException(); }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReadsOnlyTheVerifiedPublicBoolean(bool value) => Assert.Equal(value, NativePasswordPolicy.Disabled(new NativeConfig { DisablePasswordLogin = value }));
    [Fact]
    public void UnknownOrFailingPoliciesFailClosed()
    {
        Assert.Throws<HubException>(() => NativePasswordPolicy.Disabled(new object()));
        Assert.Throws<HubException>(() => NativePasswordPolicy.Disabled(new BrokenConfig()));
        Assert.Throws<HubException>(() => NativePasswordPolicy.Disabled(new { DisablePasswordLogin = "false" }));
    }
}
