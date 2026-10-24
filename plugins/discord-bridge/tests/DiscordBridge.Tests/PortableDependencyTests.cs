using System.Reflection;
using AccountHub.Security;
using Jellyfin.Plugin.DiscordBridge;
using Microsoft.AspNetCore.Mvc;
using Xunit;
using Microsoft.Extensions.DependencyInjection;
using System.Runtime.Loader;

namespace DiscordBridge.Tests;

public sealed class PortableDependencyTests
{
    [Fact]
    public void StandaloneBridgeReferencesOnlyPortableSuiteApis()
    {
        var references = typeof(Plugin).Assembly.GetReferencedAssemblies().Select(name => name.Name).ToArray();
        Assert.DoesNotContain("Jellyfin.Plugin.AccountHub", references);
        Assert.DoesNotContain("AccountHub.Security", references);
        Assert.DoesNotContain("AccountHub.Core", references);
        Assert.DoesNotContain("Jellyfin.Plugin.PrivateSyncPlay", references);
        Assert.Contains("Jellyfin.Security.Api", references);
    }
    [Fact]
    public void SharedSecurityClientCannotDiscoverHubControllersOrWorkers()
    {
        var api = typeof(SecurityClient).Assembly;
        Assert.Equal("Jellyfin.Security.Api", api.GetName().Name);
        Assert.DoesNotContain(api.GetExportedTypes(), type => typeof(ControllerBase).IsAssignableFrom(type));
        Assert.DoesNotContain(api.GetExportedTypes(), type => typeof(Microsoft.Extensions.Hosting.IHostedService).IsAssignableFrom(type));
        Assert.DoesNotContain(api.GetReferencedAssemblies(), name => name.Name is "AccountHub.Core" or "AccountHub.Security" or "Jellyfin.Plugin.AccountHub");
    }
    [Fact]
    public void SeparatePluginCopiesCanRegisterSecurityClientsInOneContainer()
    {
        var services = new ServiceCollection(); services.AddJellyfinSecurityClient();
        var context = new AssemblyLoadContext("portable-sdk-test-" + Guid.NewGuid(), isCollectible: true);
        try
        {
            var copy = context.LoadFromAssemblyPath(typeof(SecurityClient).Assembly.Location);
            var registration = copy.GetType("AccountHub.Security.SecurityClientRegistration")!.GetMethod("AddJellyfinSecurityClient")!;
            registration.Invoke(null, [services]);
        }
        finally { context.Unload(); }
    }
}
