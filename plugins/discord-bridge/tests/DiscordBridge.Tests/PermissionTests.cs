using System.Text.Json;
using DiscordBridge.Api;
using Jellyfin.Plugin.DiscordBridge;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DiscordBridge.Tests;

public sealed class PermissionTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "DiscordBridge.Permission.Tests", Guid.NewGuid().ToString("N"));
    [Fact] public void SensitiveDelegationIsDeniedByDefaultAndUnknownActionsNeverGrant()
    {
        var config = new Configuration();
        Assert.True(BridgePermissions.Allowed(config, "status"));
        foreach (var action in new[] { "invite.create", "party.guests", "administrator", "party.devices" })
            Assert.False(BridgePermissions.Allowed(config, action, Guid.NewGuid()));
        Assert.True(BridgePermissions.Allowed(config,"party.share.channel"));
    }
    [Fact] public void OverridesArePerNativeUserAndRemovalRestoresInheritance()
    {
        var config = new Configuration(); var user = Guid.NewGuid();
        config.Permissions.Defaults["party.invite"] = false;
        config.Permissions.Users[user] = new() { ["party.invite"] = true, ["reset"] = false };
        Assert.True(BridgePermissions.Allowed(config, "party.invite", user));
        Assert.False(BridgePermissions.Allowed(config, "party.invite", Guid.NewGuid()));
        Assert.False(BridgePermissions.Allowed(config, "reset", user));
        config.Permissions.Users.Remove(user);
        Assert.False(BridgePermissions.Allowed(config, "party.invite", user));
        Assert.True(BridgePermissions.Allowed(config, "reset", user));
    }
    [Fact] public void InvalidUnknownNullAndOversizedPoliciesAreRejected()
    {
        Assert.Throws<ArgumentException>(() => BridgePermissions.Validate(null));
        Assert.Throws<ArgumentException>(() => BridgeStore.Validate(new Configuration { Permissions = new() { Defaults = new() { ["administrator"] = true } } }));
        Assert.Throws<ArgumentException>(() => BridgePermissions.Validate(new() { Users = new() { [Guid.Empty] = new() } }));
        var policy = new BridgePermissionSettings();
        for (var i = 0; i < 101; i++) policy.Users[Guid.NewGuid()] = new();
        Assert.Throws<ArgumentException>(() => BridgePermissions.Validate(policy));
    }
    [Fact] public void PolicyPersistsAndStaleSettingsCannotOverwriteIt()
    {
        var store = new BridgeStore(directory); var config = store.Read().Settings; var user = Guid.NewGuid();
        config.Permissions.Users[user] = new() { ["reset"] = false };
        store.Save(config, 0, null, false);
        Assert.False(BridgePermissions.Allowed(new BridgeStore(directory).Read().Settings, "reset", user));
        Assert.Throws<InvalidOperationException>(() => store.Save(new Configuration(), 0, null, false));
        Assert.False(BridgePermissions.Allowed(store.Read().Settings, "reset", user));
    }
    [Fact] public async Task DeniedCommandUsesVerifiedIdentityAndDoesNotInvokeTheEffect()
    {
        var store = new BridgeStore(directory); var config = store.Read().Settings; var user = Guid.NewGuid();
        config.Permissions.Users[user] = new() { ["reset"] = false }; store.Save(config, 0, null, false);
        var calls = 0; var services = new ServiceCollection().AddSingleton(store);
        services.AddKeyedSingleton<Func<string, CancellationToken, Task<string>>>(DiscordBridgeClient.IdentityKey, (_, _) => (request, _) => {
            calls++; using var json = JsonDocument.Parse(request);
            Assert.Equal("identity", json.RootElement.GetProperty("operation").GetString());
            return Task.FromResult(JsonSerializer.Serialize(new { apiVersion = "1", ok = true, data = new { userId = user } }));
        });
        using var provider = services.BuildServiceProvider(); var runtime = new BridgeRuntime(provider, NullLogger<BridgeRuntime>.Instance);
        var error = await Assert.ThrowsAsync<AccountCommandException>(() => runtime.IdentityAsync("reset", 123456, CancellationToken.None));
        Assert.Equal("permission_denied", error.Code); Assert.Equal(1, calls);
    }
    [Fact] public void OptionalPortablePolicyDoesNotRequireBridgeButBrokenInstalledPolicyDenies()
    {
        using var absent = new ServiceCollection().BuildServiceProvider();
        Assert.True(new DiscordBridgeClient(absent).Permitted(Guid.NewGuid(), "link"));
        var services = new ServiceCollection();
        services.AddKeyedSingleton<Func<Guid, string, bool>>(DiscordBridgeClient.PermissionKey, (_, _) => (_, _) => throw new IOException());
        using var broken = services.BuildServiceProvider();
        Assert.False(new DiscordBridgeClient(broken).Permitted(Guid.NewGuid(), "link"));
    }
    [Fact] public void OfflineTransportDoesNotChangeNativePolicyButFeatureAndUserDenialsStillApply()
    {
        var config = new Configuration { Enabled = false }; var user = Guid.NewGuid();
        Assert.True(BridgePermissions.Permitted(config, "link", user));
        config.Permissions.Users[user] = new() { ["link"] = false };
        Assert.False(BridgePermissions.Permitted(config, "link", user));
        config.Permissions.Users[user]["link"] = true; config.EnableSelfServiceCommands = false;
        Assert.False(BridgePermissions.Permitted(config, "link", user));
        config.EnableWatchPartyCommands = false;
        Assert.False(BridgePermissions.Permitted(config, "party.devices", user));
        config.Permissions.Users[user]["party.devices"] = true;
        Assert.Throws<UnauthorizedAccessException>(() => BridgePermissions.Require(config, "party.devices", user));
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
