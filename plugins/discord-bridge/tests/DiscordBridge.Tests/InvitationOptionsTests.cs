using System.Reflection;
using Discord.Interactions;
using Jellyfin.Plugin.DiscordBridge;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DiscordBridge.Tests;

public sealed class InvitationOptionsTests
{
    public class SessionProxy:DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method,object?[]? args)=>method?.Name=="get_Sessions"?Array.Empty<SessionInfo>():throw new NotSupportedException(method?.Name);
    }
    [Fact]public async Task MissingRealNativeDeviceHasActionableGuidanceWithoutCallingAPartyProvider()
    {
        var directory=Path.Combine(Path.GetTempPath(),"opencode","bridge-no-device-"+Guid.NewGuid().ToString("N"));
        try{
            var store=new BridgeStore(directory);var before=store.Read();
            using var provider=new ServiceCollection().AddSingleton(store).AddSingleton(DispatchProxy.Create<ISessionManager,SessionProxy>()).BuildServiceProvider();
            var error=await Assert.ThrowsAsync<AccountCommandException>(()=>new WatchPartyCommands(provider).InviteAsync(Guid.NewGuid(),default));
            Assert.Equal("active_device_required",error.Code);Assert.Contains("No invitation was created",error.Message);Assert.Equal(before.Revision,store.Read().Revision);
        }finally{if(Directory.Exists(directory))Directory.Delete(directory,true);}
    }
    [Fact] public void SignupAndWatchPartyAreDistinctTopLevelCommandsWithOnlyTheRequestedOptions()
    {
        var methods=typeof(SlashCommands).GetMethods().Where(m=>m.GetCustomAttribute<SlashCommandAttribute>() is not null).ToArray();
        var party=Assert.Single(methods,m=>m.GetCustomAttribute<SlashCommandAttribute>()!.Name=="watchparty");
        Assert.Equal("user",Assert.Single(party.GetParameters()).Name);
        var signup=Assert.Single(methods,m=>m.GetCustomAttribute<SlashCommandAttribute>()!.Name=="invite");
        Assert.Equal(new[]{"uses","minutes"},signup.GetParameters().Select(p=>p.Name));
        Assert.All(signup.GetParameters(),p=>Assert.True(p.IsOptional));
    }
    [Fact] public void IndependentBridgeDefaultsAreBoundedAndNativeAuthorityIsNotGranted()
    {
        var config=new Configuration();Assert.Equal(1,config.InviteDefaultUses);Assert.False(config.WatchPartyGuests);BridgeStore.Validate(config);
        config.InviteDefaultUses=3;config.InviteMaxUses=2;Assert.Throws<ArgumentException>(()=>BridgeStore.Validate(config));
        config.InviteDefaultUses=2;BridgeStore.Validate(config);
        config.InviteMaxExpiryMinutes=30;Assert.Throws<ArgumentException>(()=>BridgeStore.Validate(config));
        config.InviteDefaultExpiryMinutes=30;BridgeStore.Validate(config);
        config.WatchPartyUses=101;Assert.Throws<ArgumentException>(()=>BridgeStore.Validate(config));
        Assert.False(BridgePermissions.Allowed(config,"invite.create"));Assert.False(BridgePermissions.Allowed(config,"party.guests"));
        Assert.True(BridgePermissions.Allowed(config,"party.share.channel"));config.Permissions.Defaults["party.share.channel"]=false;Assert.False(BridgePermissions.Allowed(config,"party.share.channel"));
        Assert.False(new BridgeLibraryNotices(null!).ObserveNative);
        Assert.False(new BridgeLibraryNotices(null!).CaptureLocally);
    }
}
