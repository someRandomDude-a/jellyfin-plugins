using System.Reflection;
using System.Text.Json;
using AccountHub.Core;
using AccountHub.Groups;
using AccountHub.Notifications;
using DiscordBridge.Api;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.UserManager.Data;
using Jellyfin.Plugin.UserManager.Security;
using Jellyfin.Plugin.UserManager.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AccountHub.Tests;

public sealed class DiscordIdentityTests
{
    public class Users : DispatchProxy
    {
        public User User = new("Test user", "native-auth", "native-reset") { Id = Guid.NewGuid() };
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
        {
            "GetUserById" => args![0] is Guid id && id == User.Id ? User : null,
            _ => throw new NotSupportedException(method?.Name)
        };
    }
    private sealed class Native : IGroupPolicyNative
    {
        public UserPolicy Read(Guid id) => throw new NotSupportedException();
        public Task WriteManagedAsync(Guid id, JsonElement patch, CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public readonly StoreFixture Hub = new();
        public readonly string Discord = "123456789012345678";
        public readonly List<JsonElement> Messages = [];
        public bool DeliverySucceeds = true;
        public ServiceProvider Services = null!;
        public PluginStore Lifecycle = null!;
        public PluginSettingsService Settings = null!;
        public DiscordLinkService Links = null!;
        public DiscordIdentity Identity = null!;
        public InviteDefaultsService Defaults = null!;
        public GroupPolicyService Groups = null!;
        public Users NativeUsers = null!;
        public async Task Initialize(bool linked = true)
        {
            await Hub.InitializeAsync();
            await Hub.ExecuteSqlAsync("UPDATE hub_state SET PasswordLoginPolicy='allow_local';");
            Lifecycle = new(Hub.Paths.DataDirectory, "account-hub.db"); await Lifecycle.InitializeAsync();
            Settings = new(Lifecycle, new SecretProtector(Hub.Paths.DataDirectory));
            await Settings.ApplyAsync(new() { PublicBaseUrl = "https://public.example/suite" });
            var users = DispatchProxy.Create<IUserManager, Users>(); NativeUsers = (Users)(object)users;
            Services = new ServiceCollection().AddSingleton(users)
                .AddKeyedSingleton<Func<string,CancellationToken,Task<string>>>(DiscordBridgeClient.ServiceKey, (_,_) => (json,_) => {
                    Messages.Add(JsonSerializer.Deserialize<JsonElement>(json));
                    return Task.FromResult(JsonSerializer.Serialize(new { apiVersion = "1", ok = DeliverySucceeds }));
                }).BuildServiceProvider();
            var userService = new JellyfinUserService(Services);
            var audit = new AuditService(Lifecycle, NullLogger<AuditService>.Instance);
            var accounts = new AccountService(Lifecycle,userService,audit,Settings,null!,NullLogger<AccountService>.Instance);
            Links = new(Hub.Store,Hub.Clock);
            Defaults = new(Hub.Store,new CommandGate(Hub.Store,Hub.Clock),Hub.Clock);
            Groups = new(Hub.Paths,new Native(),Hub.Store,Hub.Clock);
            Identity = new(Lifecycle,Links,accounts,Settings,new HubHost(Services,Hub.Store,new ExternalCommandGate(Hub.Store,Hub.Clock)),new DiscordBridgeClient(Services),Defaults,Hub.Store,Groups,new IngressLimiter(Hub.Clock));
            if(linked)await Links.ConfirmAsync(NativeUsers.User.Id,await Links.PrepareAsync(Discord,default),default,"original.user");
        }
        public async Task<JsonElement> Call(string operation,string? discordUsername="friendly.user") => JsonSerializer.Deserialize<JsonElement>(await Identity.DispatchAsync(JsonSerializer.Serialize(new { apiVersion = "1", operation, discordId = Discord,discordUsername }),default));
        public string SentToken => new Uri(Messages.Last().GetProperty("payload").GetProperty("text").GetString()!.Split('\n').Last()).Fragment["#token=".Length..];
        public async ValueTask DisposeAsync() {
            if(Services is not null)await Services.DisposeAsync();
            if(Lifecycle is not null){using var pooled=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Lifecycle.DatabasePath,Mode=SqliteOpenMode.ReadWrite,Cache=SqliteCacheMode.Shared,Pooling=true,DefaultTimeout=10}.ToString());SqliteConnection.ClearPool(pooled);}
            await Hub.DisposeAsync();
        }
    }

    [Fact] public async Task LinkedRecoveryUsesPrivateFiveMinuteBaseUrlLinkAndUnlinkRevokesIt()
    {
        await using var f = new Fixture(); await f.Initialize();
        var result = await f.Call("reset"); Assert.True(result.GetProperty("ok").GetBoolean());
        Assert.Contains("@friendly.user (ID: "+f.Discord+")",result.GetProperty("data").GetProperty("message").GetString());
        var payload = Assert.Single(f.Messages).GetProperty("payload");
        Assert.Equal("send.secret",f.Messages[0].GetProperty("operation").GetString());
        Assert.True(payload.GetProperty("directMessage").GetBoolean()); Assert.Equal(f.Discord,payload.GetProperty("destination").GetString());
        Assert.Contains("https://public.example/suite/AccountHub/reset#token=",payload.GetProperty("text").GetString());
        var token = f.SentToken; var record = await f.Lifecycle.GetResetTokenAsync(DiscordLinkService.Hash(token));
        Assert.NotNull(record); Assert.InRange(record.ExpiresAt-record.CreatedAt,299999,300001);
        Assert.DoesNotContain(token,result.GetRawText());
        Assert.True((await f.Call("unlink")).GetProperty("ok").GetBoolean());
        Assert.Null(await f.Lifecycle.GetResetTokenAsync(DiscordLinkService.Hash(token)));
        Assert.Equal("link_required",(await f.Call("reset")).GetProperty("error").GetProperty("code").GetString());
        Assert.Single(f.Messages);
    }
    [Fact]public async Task LinkAndStatusShowUsernameAndStableIdWithoutUsingTheNameAsAuthority(){
        await using var f=new Fixture();await f.Initialize();
        Assert.Contains("@friendly.user (ID: "+f.Discord+")",(await f.Call("status")).GetProperty("data").GetProperty("message").GetString());
        Assert.Contains("@original.user (ID: "+f.Discord+")",(await f.Call("status",null)).GetProperty("data").GetProperty("message").GetString());
        var link=(await f.Call("link.prepare")).GetProperty("data").GetProperty("message").GetString();Assert.Contains("@friendly.user (ID: "+f.Discord+")",link);
        await f.Lifecycle.DeleteLinkAsync(f.NativeUsers.User.Id.ToString("N"));
        Assert.Equal("link_required",(await f.Call("status","original.user")).GetProperty("error").GetProperty("code").GetString());
    }

    [Fact] public async Task UnconfirmedDmRevokesItsUnusedCredentialAndDoesNotRetryDelivery()
    {
        await using var f = new Fixture(); await f.Initialize(); f.DeliverySucceeds=false;
        var result=await f.Call("reset"); Assert.False(result.GetProperty("ok").GetBoolean());
        Assert.Equal("delivery_unavailable",result.GetProperty("error").GetProperty("code").GetString());
        Assert.Null(await f.Lifecycle.GetResetTokenAsync(DiscordLinkService.Hash(f.SentToken))); Assert.Single(f.Messages);
    }

    [Fact] public async Task RecoveryChecksNativeAccountPolicyUrlAndIssuanceLimits()
    {
        await using var f = new Fixture(); await f.Initialize();
        f.NativeUsers.User.SetPermission(PermissionKind.IsDisabled,true);
        Assert.Equal("link_required",(await f.Call("reset")).GetProperty("error").GetProperty("code").GetString());
        f.NativeUsers.User.SetPermission(PermissionKind.IsDisabled,false);
        await f.Hub.ExecuteSqlAsync("UPDATE hub_state SET PasswordLoginPolicy='local_disabled';");
        Assert.Equal("password_login_disabled",(await f.Call("reset")).GetProperty("error").GetProperty("code").GetString());
        await f.Hub.ExecuteSqlAsync("UPDATE hub_state SET PasswordLoginPolicy='allow_local';");
        await f.Settings.ApplyAsync(new(){PublicBaseUrl="https://user:secret@invalid.example"});
        Assert.Equal("public_url_required",(await f.Call("reset")).GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(f.Messages);
        f.Hub.Clock.Now=f.Hub.Clock.Now.AddMinutes(1);
        await f.Settings.ApplyAsync(new(){PublicBaseUrl="https://public.example/suite"});
        for(var i=0;i<3;i++)Assert.True((await f.Call("reset")).GetProperty("ok").GetBoolean());
        Assert.Equal("rate_limited",(await f.Call("reset")).GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(3,f.Messages.Count);
    }

    [Fact] public async Task DelegatedInviteUsesOnlyConfiguredTemplateAndEnforcesLimit()
    {
        await using var f = new Fixture(); await f.Initialize();
        Assert.Equal("forbidden",(await f.Call("invite.create")).GetProperty("error").GetProperty("code").GetString());
        var group=Guid.NewGuid(); await f.Groups.SaveGroupAsync(group,0,"Standard","",JsonSerializer.SerializeToElement(new{MaxActiveSessions=2}));
        await f.Defaults.SaveAsync(f.NativeUsers.User.Id,Guid.NewGuid(),0,new(group,true,[f.NativeUsers.User.Id],60,1),_=>Task.CompletedTask);
        var result=await f.Call("invite.create");Assert.True(result.GetProperty("ok").GetBoolean());
        Assert.Contains("https://public.example/suite/AccountHub/invite#token=",result.GetProperty("data").GetProperty("message").GetString());
        var invite=await f.Hub.Store.ReadAsync((db,ct)=>db.OnboardingInvites.SingleAsync(ct));
        Assert.Equal(group.ToString("D"),invite.GroupId);Assert.Equal(1,invite.MaxUses);Assert.Equal("active",invite.Status);
        Assert.Equal("rate_limited",(await f.Call("invite.create")).GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(f.Messages);
    }

    [Fact] public async Task UnlinkDuringInviteAuthorizationCannotCommitANewDelegatedInvite()
    {
        await using var f = new Fixture(); await f.Initialize(); var group = Guid.NewGuid();
        await f.Defaults.SaveAsync(f.NativeUsers.User.Id,Guid.NewGuid(),0,new(group,true,[f.NativeUsers.User.Id]),_=>Task.CompletedTask);
        var error=await Assert.ThrowsAsync<HubException>(()=>f.Defaults.IssueDiscordAsync(f.NativeUsers.User.Id,Guid.NewGuid(),1,group,"{}",
            ct=>f.Lifecycle.DeleteLinkAsync(f.NativeUsers.User.Id.ToString("N"),ct),expectedDiscordId:f.Discord));
        Assert.Equal("link_required",error.Code);
        Assert.Equal(0,await f.Hub.Store.ReadAsync((db,ct)=>db.OnboardingInvites.CountAsync(ct)));
    }
}
