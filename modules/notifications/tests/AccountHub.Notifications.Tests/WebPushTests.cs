using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AccountHub.Api;
using Xunit;

namespace AccountHub.Notifications.Tests;

public sealed class WebPushTests
{
    [Fact]public void DeviceDefaultIsConfigurableAndSurvivesOtherPushEdits(){
        var f=new Fixture();Assert.False(f.Store.PushSettings().DefaultOn);
        f.Store.ConfigurePush(0,true,"https://example.test/",true);Assert.True(f.Store.PushSettings().DefaultOn);
        f.Store.ConfigurePush(1,true);Assert.True(f.Store.PushSettings().DefaultOn);
        f.Store.ConfigurePush(2,true,defaultOn:false);Assert.False(f.Store.PushSettings().DefaultOn);
    }
    [Theory]
    [InlineData("https://public.example/jellyfin?token=secret", "http", "internal:8096", "https://public.example/")]
    [InlineData(null,"https","client.example:8443","https://client.example:8443/")]
    [InlineData(null,"http","localhost:8096","https://localhost:8096/")]
    [InlineData("http://internal:8096", "https", "client.example", "https://client.example/")]
    [InlineData("https://user:secret@wrong.example/path", "https", "client.example", "https://client.example/")]
    public void AutomaticSetupUsesNativePublicOriginThenClientHostWithoutSecrets(string? configured,string scheme,string host,string expected){
        var context=new Microsoft.AspNetCore.Http.DefaultHttpContext();context.Request.Scheme=scheme;context.Request.Host=new(host);
        Assert.Equal(expected,WebPush.AutomaticSubject(configured,context.Request));
    }
    private sealed class Transport:IWebPushTransport
    {
        public System.Net.HttpStatusCode Status=System.Net.HttpStatusCode.Created;
        public List<(string Endpoint,string Authorization,string Urgency)> Sent=[];
        public Task<System.Net.HttpStatusCode> SendAsync(string endpoint,string authorization,CancellationToken ct,string urgency){Sent.Add((endpoint,authorization,urgency));return Task.FromResult(Status);}
    }
    private static byte[] Decode(string s)=>Convert.FromBase64String(s.Replace('-','+').Replace('_','/')+new string('=',(4-s.Length%4)%4));
    private static PushSubscription Subscription(string endpoint="https://fcm.googleapis.com/fcm/send/disposable"){
        using var ec=ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);var p=ec.ExportParameters(false);return new(endpoint,WebPush.Encode([4,..p.Q.X!,..p.Q.Y!]),WebPush.Encode(RandomNumberGenerator.GetBytes(16)));
    }
    [Fact]public void VapidUsesVerifiableP256SignatureAndExactPushServiceAudience(){
        var config=WebPush.Create(0,true) with{Subject="mailto:test@example.test"};var now=DateTimeOffset.UtcNow;var authorization=WebPush.Authorization(config,"https://fcm.googleapis.com/fcm/send/private-endpoint",now);
        var jwt=authorization.Split("t=")[1].Split(',')[0];var parts=jwt.Split('.');var key=Decode(config.PublicKey);
        using var verifier=ECDsa.Create(new ECParameters{Curve=ECCurve.NamedCurves.nistP256,Q=new(){X=key[1..33],Y=key[33..65]}});
        Assert.True(verifier.VerifyData(Encoding.ASCII.GetBytes(parts[0]+"."+parts[1]),Decode(parts[2]),HashAlgorithmName.SHA256,DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        using var claims=JsonDocument.Parse(Decode(parts[1]));Assert.Equal("https://fcm.googleapis.com",claims.RootElement.GetProperty("aud").GetString());Assert.Equal(now.AddHours(1).ToUnixTimeSeconds(),claims.RootElement.GetProperty("exp").GetInt64());
        Assert.DoesNotContain("private-endpoint",Encoding.UTF8.GetString(Decode(parts[1])));
    }
    [Theory]
    [InlineData("http://fcm.googleapis.com/push")]
    [InlineData("https://fcm.googleapis.com.evil.invalid/push")]
    [InlineData("https://127.0.0.1/push")]
    [InlineData("https://fcm.googleapis.com:444/push")]
    [InlineData("https://user@fcm.googleapis.com/push")]
    public void ArbitraryNetworkTargetsAreRejected(string endpoint)=>Assert.Throws<NotificationException>(()=>WebPush.Validate(Subscription(endpoint)));
    [Fact]public async Task PushIsOptInRecipientBoundProtectedAndRevocationDropsJobs(){
        var f=new Fixture();var subscription=Subscription();Assert.False(f.Store.PushSettings().Enabled);Assert.Throws<NotificationException>(()=>f.Store.SavePush(f.Host.Alice,subscription));
        f.Store.ConfigurePush(0,true,"mailto:test@example.test");var id=f.Store.SavePush(f.Host.Alice,subscription);
        Assert.Throws<NotificationException>(()=>f.Store.SavePush(f.Host.Bob,subscription));f.Store.RemovePush(f.Host.Bob,id);Assert.Single(f.Store.PushTargets(f.Host.Alice));
        var entry=f.Event();await f.Publish(entry);Assert.Single(f.Store.PushJobs());
        await f.Publish(entry);Assert.Single(f.Store.PushJobs());
        // Revocation is checked before any external request is attempted.
        f.Host.Deleted.Add(f.Host.Alice);await new WebPush(f.Store,f.Host,f.Clock).ProcessAsync(default);Assert.Empty(f.Store.PushJobs());
        f.Store.ConfigurePush(1,false);Assert.Empty(f.Store.PushTargets(f.Host.Alice));Assert.False(f.Store.PushSettings().Enabled);
        Assert.Throws<NotificationException>(()=>f.Store.ConfigurePush(0,true));
        using var db=new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder{DataSource=f.Options.DatabasePath}.ToString());db.Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT json FROM nh_push_settings WHERE id=1";var protectedJson=(string)cmd.ExecuteScalar()!;Assert.DoesNotContain("privateKey",protectedJson);
    }
    [Fact]public async Task RevokedMediaAndDismissedRowsNeverProducePushRequests(){
        var f=new Fixture();f.Store.ConfigurePush(0,true,"mailto:test@example.test");f.Store.SavePush(f.Host.Alice,Subscription());
        var accepted=await f.Publish(f.Event() with{Type="library.movie.added"});f.Host.LibraryDenied.Add(f.Host.Alice);
        await new WebPush(f.Store,f.Host,f.Clock).ProcessAsync(default);Assert.Empty(f.Store.PushJobs());
        f.Host.LibraryDenied.Clear();accepted=await f.Publish(f.Event());f.Store.Mark(f.Host.Alice,Assert.Single(accepted.NotificationIds),true);
        await new WebPush(f.Store,f.Host,f.Clock).ProcessAsync(default);Assert.Empty(f.Store.PushJobs());
    }
    [Fact]public async Task SuccessfulSignalsAreContentFreeGoneSubscriptionsAreRemovedAndRetriesAreBounded(){
        var f=new Fixture();f.Store.ConfigurePush(0,true,"mailto:test@example.test");f.Store.SavePush(f.Host.Alice,Subscription());var sender=new Transport();var push=new WebPush(f.Store,f.Host,f.Clock,sender);
        await f.Publish(f.Event() with{Title="Private title must not leave Jellyfin"});await push.ProcessAsync(default);Assert.Single(sender.Sent);Assert.Empty(f.Store.PushJobs());Assert.DoesNotContain("Private title",sender.Sent[0].Authorization);
        sender.Status=System.Net.HttpStatusCode.ServiceUnavailable;await f.Publish(f.Event());
        for(var i=0;i<3;i++){await push.ProcessAsync(default);f.Clock.Now=f.Clock.Now.AddMinutes(5);}Assert.Empty(f.Store.PushJobs());Assert.Equal(4,sender.Sent.Count);
        sender.Status=System.Net.HttpStatusCode.Gone;await f.Publish(f.Event());await push.ProcessAsync(default);Assert.Empty(f.Store.PushTargets(f.Host.Alice));Assert.Empty(f.Store.PushJobs());
    }
    [Fact]public async Task SilentAdministratorCategoriesSkipPushWhileHighUsesHighUrgency(){
        var f=new Fixture();f.Store.ConfigurePush(0,true,"mailto:test@example.test");f.Store.SavePush(f.Host.Bob,Subscription());var sender=new Transport();var push=new WebPush(f.Store,f.Host,f.Clock,sender);
        f.Store.Preferences(f.Host.Bob,[new("deletion-queue","*","in-app",true){Importance="silent"}]);await f.Publish(f.Event(true));await push.ProcessAsync(default);Assert.Empty(sender.Sent);
        f.Store.Preferences(f.Host.Bob,[new("deletion-queue","*","in-app",true){Importance="high"}]);await f.Publish(f.Event(true));await push.ProcessAsync(default);Assert.Equal("high",Assert.Single(sender.Sent).Urgency);
    }
    [Theory]
    [InlineData("silent")]
    [InlineData("low")]
    public async Task OrdinaryUsersCanSilencePushAndOptForImportantDelivery(string quiet) {
        var f = new Fixture(); f.Store.ConfigurePush(0, true, "https://example.test/"); f.Store.SavePush(f.Host.Alice, Subscription());
        var sender = new Transport(); var push = new WebPush(f.Store, f.Host, f.Clock, sender);
        f.Store.Preferences(f.Host.Alice, [new("deletion-queue", "*", "in-app", true) { Importance = quiet }]);
        await f.Publish(f.Event()); await push.ProcessAsync(default); Assert.Empty(sender.Sent);
        f.Store.Preferences(f.Host.Alice, [new("deletion-queue", "*", "in-app", true) { Importance = "high" }]);
        await f.Publish(f.Event()); await push.ProcessAsync(default); Assert.Equal("high", Assert.Single(sender.Sent).Urgency);
    }
    [Fact] public async Task CriticalPushStillRequiresAccessAndDeviceOptInButIgnoresPersonalMute() {
        var f = new Fixture(); f.Store.ConfigurePush(0, true, "https://example.test/"); f.Store.SavePush(f.Host.Alice, Subscription());
        var sender = new Transport(); var push = new WebPush(f.Store, f.Host, f.Clock, sender);
        f.Store.Preferences(f.Host.Alice, [new("*", "*", "in-app", false) { Importance = "silent" }]);
        await f.Publish(f.Event() with { Severity = "critical" }); await push.ProcessAsync(default); Assert.Equal("high", Assert.Single(sender.Sent).Urgency);
        f.Host.LibraryDenied.Add(f.Host.Alice); await f.Publish(f.Event() with { Type = "library.movie.added", Severity = "critical" });
        await push.ProcessAsync(default); Assert.Single(sender.Sent);
    }
}
