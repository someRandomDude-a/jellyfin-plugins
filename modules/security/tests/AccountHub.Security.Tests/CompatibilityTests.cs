using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using Moq;
using Xunit;

namespace AccountHub.Security.Tests;

public sealed class CompatibilityTests
{
    [Theory]
    [InlineData("2.6.4", true)]
    [InlineData("2.6.4.0", true)]
    [InlineData("2.6.4.1", true)]
    [InlineData("2.6.4.2", false)]
    [InlineData("2.6.3", false)]
    [InlineData("2.6.5", false)]
    [InlineData("2.6", false)]
    [InlineData("v2.6.4", false)]
    [InlineData("2.6.4-jf12", false)]
    [InlineData(null, false)]
    public void ExactVersionOnly(string? value, bool expected) => Assert.Equal(expected, SupportedSecurity.Matches(value));

    [Fact]
    public void MissingIsSafe()
    {
        var manager = new Mock<IPluginManager>();
        manager.SetupGet(m => m.Plugins).Returns([]);
        var result = new JellyfinSecurityCompatibility(manager.Object, new FixedClock()).Check();
        Assert.Equal("missing", result.State);
        Assert.False(result.SecurityTabVisible);
    }

    [Theory]
    [InlineData("2.6.4.0", "2.6.4.0", PluginStatus.Active, "ok")]
    [InlineData("2.6.5.0", "2.6.4.0", PluginStatus.Active, "ok")]
    [InlineData("2.6.4.0", "2.6.3.0", PluginStatus.Active, "incompatible")]
    [InlineData("2.6.4.0", "2.6.4.0", PluginStatus.Disabled, "disabled")]
    public void RunningVersionWinsOverDownloadedManifest(string installed, string running, PluginStatus status, string expected)
    {
        var plugin = new Mock<IPlugin>();
        plugin.SetupGet(p => p.Version).Returns(Version.Parse(running));
        var local = new LocalPlugin("unused", true, new PluginManifest
        { Id = SupportedSecurity.PluginId, Version = installed, Status = status }) { Instance = plugin.Object };
        var manager = new Mock<IPluginManager>();
        manager.SetupGet(m => m.Plugins).Returns([local]);
        Assert.Equal(expected, new JellyfinSecurityCompatibility(manager.Object, new FixedClock()).Check().State);
    }

    [Fact]
    public void MetadataFailureIsUnavailable()
    {
        var manager = new Mock<IPluginManager>();
        manager.SetupGet(m => m.Plugins).Throws<InvalidOperationException>();
        Assert.Equal("unavailable", new JellyfinSecurityCompatibility(manager.Object, new FixedClock()).Check().State);
    }
}
