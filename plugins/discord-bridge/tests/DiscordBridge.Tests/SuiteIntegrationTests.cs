using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DiscordBridge.Api;
using Jellyfin.Plugin.DiscordBridge;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DiscordBridge.Tests;

public sealed class SuiteIntegrationTests
{
    [Theory][InlineData(true)][InlineData(false)]
    public async Task ProfileNamesAreOptionalDisplayMetadataBoundToTheExactDiscordId(bool matches){
        using var services=new ServiceCollection().AddKeyedSingleton<Func<string,CancellationToken,Task<string>>>(DiscordBridgeClient.ServiceKey,(_,_)=> (_,_)=>Task.FromResult(JsonSerializer.Serialize(new{apiVersion="1",ok=true,data=new{discordId=matches?"123456789":"987654321",username="friendly.user"}}))).BuildServiceProvider();
        Assert.Equal(matches?"friendly.user":null,await new DiscordBridgeClient(services).UsernameAsync("123456789"));
        Assert.Null(DiscordIdentityLabel.Username("unsafe\nuser"));
        Assert.Equal("@friendly.user (ID: 123456789)",DiscordIdentityLabel.Format("friendly.user","123456789"));
    }
    [Fact]public void UserFailuresExposeOnlyOpaqueErrorIdsAndDoNotExposeServerReferencesOrInternalCodes(){
        using var services=new ServiceCollection().BuildServiceProvider();var runtime=new BridgeRuntime(services,NullLogger<BridgeRuntime>.Instance);
        Assert.Matches("^[A-F0-9]{6}$",runtime.InstanceCode);
        var first=runtime.Failure("account_unavailable","Check integration health.");var second=runtime.Failure("account_unavailable","Check integration health.");
        Assert.Matches("Error ID: [a-f0-9-]{36}$",first);Assert.DoesNotContain("account_unavailable",first);Assert.DoesNotContain("Reference:",first);Assert.DoesNotContain(runtime.InstanceCode,first);Assert.NotEqual(first,second);
        Assert.Equal("account_unavailable",AccountCommandException.FromCode("provider-secret-not-allowed").Code);
    }
    [Fact]public async Task AccountRequestCancellationIsPropagatedToTheIdentityProvider(){
        var directory=Path.Combine(Path.GetTempPath(),"opencode","bridge-account-cancel-"+Guid.NewGuid().ToString("N"));
        try{
            var store=new BridgeStore(directory);var config=store.Read();config.Settings.EnableSelfServiceCommands=true;store.Save(config.Settings,config.Revision,null,false);
            var started=new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var services=new ServiceCollection().AddSingleton(store).AddKeyedSingleton<Func<string,CancellationToken,Task<string>>>(DiscordBridgeClient.IdentityKey,(_,_)=>async(_,ct)=>{started.SetResult(ct);await Task.Delay(Timeout.InfiniteTimeSpan,ct);return "unreachable";}).BuildServiceProvider();
            var runtime=new BridgeRuntime(services,NullLogger<BridgeRuntime>.Instance);using var cancel=new CancellationTokenSource();
            var request=runtime.IdentityAsync("reset",123456789,cancel.Token);var providerToken=await started.Task.WaitAsync(TimeSpan.FromSeconds(5));cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>request);Assert.True(providerToken.IsCancellationRequested);
        }finally{if(Directory.Exists(directory))Directory.Delete(directory,true);}
    }
    [Theory]
    [InlineData("forbidden", "not permitted")]
    [InlineData("link_required", "/link")]
    [InlineData("password_login_disabled", "sign-in policy")]
    [InlineData("delivery_unavailable", "revoked")]
    [InlineData("rate_limited", "limit")]
    [InlineData("invite_limits", "fewer uses")]
    [InlineData("active_device_required", "No invitation was created")]
    [InlineData("party_provider_missing", "Private SyncPlay")]
    [InlineData("party_public_url_required", "BaseUrl")]
    [InlineData("native_party_denied", "Nothing was automatically resent")]
    [InlineData("not_found", "template")]
    [InlineData("secret-bearing unknown failure", "storage health")]
    public async Task AccountFailuresExposeOnlyFixedActionableGuidance(string code,string expected){
        var directory=Path.Combine(Path.GetTempPath(),"opencode","bridge-account-errors-"+Guid.NewGuid().ToString("N"));
        try{
            var store=new BridgeStore(directory);var config=store.Read();config.Settings.EnableSelfServiceCommands=true;store.Save(config.Settings,config.Revision,null,false,Guid.NewGuid(),"test",null);
            using var services=new ServiceCollection().AddSingleton(store).AddKeyedSingleton<Func<string,CancellationToken,Task<string>>>(DiscordBridgeClient.IdentityKey,(_,_) => (_,_) => Task.FromResult(JsonSerializer.Serialize(new{apiVersion="1",ok=false,error=new{code,message="Never forward upstream secret"}}))).BuildServiceProvider();
            var runtime=new BridgeRuntime(services,NullLogger<BridgeRuntime>.Instance);
            var error=await Assert.ThrowsAsync<AccountCommandException>(()=>runtime.IdentityAsync("reset",123456789,default));
            Assert.Contains(expected,error.Message);Assert.DoesNotContain("Never forward",error.Message);Assert.DoesNotContain("secret-bearing",error.Message);
        }finally{if(Directory.Exists(directory))Directory.Delete(directory,true);}
    }
    [Fact]public async Task PublicWatchPartyUrlReusesOnlyTheConfiguredProviderAndPreservesBaseUrl(){
        using var services=new ServiceCollection().AddKeyedSingleton<Func<CancellationToken,Task<string?>>>(DiscordBridgeClient.PublicUrlKey,(_,_)=>_=>Task.FromResult<string?>("https://public.example/suite")).BuildServiceProvider();
        Assert.Equal("https://public.example/suite/",(await BridgePublicUrl.ResolveAsync(services,new(),default))!.AbsoluteUri);
        Assert.Equal("https://override.example/base/",(await BridgePublicUrl.ResolveAsync(services,new(){PublicServerUrl="https://override.example/base"},default))!.AbsoluteUri);
        Assert.Null(await BridgePublicUrl.ResolveAsync(services,new(){PublicServerUrl="https://user:secret@invalid.example"},default));
    }
    [Fact]public async Task StableDeliveryReceiptsAcknowledgeSuccessButNeverReplayAnUncertainSend(){
        var directory=Path.Combine(Path.GetTempPath(),"opencode","bridge-delivery-"+Guid.NewGuid().ToString("N"));
        try{
            var store=new BridgeStore(directory);using var services=new ServiceCollection().AddSingleton(store).BuildServiceProvider();
            var runtime=new BridgeRuntime(services,NullLogger<BridgeRuntime>.Instance);var id=Guid.NewGuid();
            var request=JsonSerializer.Serialize(new{apiVersion="1",operation="send",payload=new{destination="123456789",directMessage=true,text="Synthetic suite notification",deliveryId=id}});
            using(var offline=JsonDocument.Parse(await runtime.DispatchAsync(request,default)))Assert.True(offline.RootElement.GetProperty("error").GetProperty("retryable").GetBoolean());
            var hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{operation="send",destination="123456789",direct=true,text="Synthetic suite notification"}))));
            Assert.Equal("new",store.BeginDelivery(id,hash));
            using(var uncertain=JsonDocument.Parse(await runtime.DispatchAsync(request,default))){Assert.Equal("outcome_uncertain",uncertain.RootElement.GetProperty("error").GetProperty("code").GetString());Assert.False(uncertain.RootElement.GetProperty("error").GetProperty("retryable").GetBoolean());}
            store.CompleteDelivery(id,true);
            using(var delivered=JsonDocument.Parse(await runtime.DispatchAsync(request,default)))Assert.True(delivered.RootElement.GetProperty("data").GetProperty("deduplicated").GetBoolean());
            Assert.Equal("delivered",new BridgeStore(directory).BeginDelivery(id,hash));
            Assert.Throws<ArgumentException>(()=>store.BeginDelivery(id,"different payload"));
        }finally{if(Directory.Exists(directory))Directory.Delete(directory,true);}
    }
}
