using System.Net;
using System.Security.Cryptography;
using System.Text;
using AccountHub.Api;

namespace AccountHub.Notifications;

public sealed record PushSubscription(string Endpoint,string P256dh,string Auth);
public sealed record PushConfiguration(long Revision,bool Enabled,string PublicKey,string PrivateKey)
{
    public string Subject{get;init;}="";
}
public sealed record PushTarget(string Id,Guid User,PushSubscription Subscription);
public sealed record PushJob(string Id,Guid User,Guid NotificationId,Guid EventId,int Attempts);
public interface IWebPushTransport
{
    Task<HttpStatusCode> SendAsync(string endpoint,string authorization,CancellationToken ct);
}
public sealed class WebPushTransport:IWebPushTransport
{
    public async Task<HttpStatusCode> SendAsync(string endpoint,string authorization,CancellationToken ct){
        Validation.Require(WebPush.AllowedEndpoint(endpoint),"Unsupported push service");
        using var handler=new SocketsHttpHandler{AllowAutoRedirect=false,UseCookies=false,UseProxy=false,ConnectTimeout=TimeSpan.FromSeconds(5)};
        handler.ConnectCallback=async(context,token)=>{
            var addresses=await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host,token);
            var address=addresses.FirstOrDefault(a=>WebhookSender.AllowedAddress(a,false))??throw new HttpRequestException("Push address unavailable");
            var socket=new System.Net.Sockets.Socket(address.AddressFamily,System.Net.Sockets.SocketType.Stream,System.Net.Sockets.ProtocolType.Tcp);
            try{await socket.ConnectAsync(new IPEndPoint(address,context.DnsEndPoint.Port),token);return new System.Net.Sockets.NetworkStream(socket,true);}catch{socket.Dispose();throw;}
        };
        using var client=new HttpClient(handler){Timeout=TimeSpan.FromSeconds(10)};
        using var request=new HttpRequestMessage(HttpMethod.Post,endpoint){Content=new ByteArrayContent([])};
        request.Headers.TryAddWithoutValidation("Authorization",authorization);request.Headers.Add("TTL","60");request.Headers.Add("Urgency","normal");
        using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);return response.StatusCode;
    }
}

// RFC 8030/8292 payload-free Web Push: no media, identity, credentials or action
// capabilities leave Jellyfin. A push always displays the same generic message.
public sealed class WebPush(NotificationStore store,INotificationHost host,TimeProvider clock,IWebPushTransport? transport=null)
{
    public static string Encode(byte[] bytes)=>Convert.ToBase64String(bytes).TrimEnd('=').Replace('+','-').Replace('/','_');
    private static byte[] Decode(string text)=>Convert.FromBase64String(text.Replace('-','+').Replace('_','/')+new string('=',(4-text.Length%4)%4));
    public static bool AllowedEndpoint(string endpoint)=>Uri.TryCreate(endpoint,UriKind.Absolute,out var uri)&&uri.Scheme=="https"&&uri.Port==443&&uri.UserInfo.Length==0&&uri.Fragment.Length==0&&endpoint.Length<=2048&&
        (uri.Host=="fcm.googleapis.com"||uri.Host=="web.push.apple.com"||uri.Host=="updates.push.services.mozilla.com"||uri.Host.EndsWith(".push.services.mozilla.com",StringComparison.Ordinal));
    public static void Validate(PushSubscription subscription){
        Validation.Require(subscription is not null&&subscription.Endpoint is not null&&AllowedEndpoint(subscription.Endpoint)&&subscription.P256dh is {Length:87}&&subscription.Auth is {Length:22},"Unsupported push service or keys");
        try {var key=Decode(subscription.P256dh);var auth=Decode(subscription.Auth);Validation.Require(key.Length==65&&key[0]==4&&auth.Length==16,"Invalid push keys");using var ec=ECDiffieHellman.Create(new ECParameters{Curve=ECCurve.NamedCurves.nistP256,Q=new(){X=key[1..33],Y=key[33..65]}});}
        catch(Exception e)when(e is FormatException or CryptographicException or ArgumentException){throw new NotificationException("invalid_request","Invalid push keys");}
    }
    public static PushConfiguration Create(long revision,bool enabled){using var key=ECDsa.Create(ECCurve.NamedCurves.nistP256);var p=key.ExportParameters(false);return new(revision,enabled,Encode([4,..p.Q.X!,..p.Q.Y!]),Encode(key.ExportECPrivateKey()));}
    public static string Authorization(PushConfiguration config,string endpoint,DateTimeOffset now){
        var uri=new Uri(endpoint);var header=Encode(Encoding.UTF8.GetBytes("{\"typ\":\"JWT\",\"alg\":\"ES256\"}"));
        var payload=Encode(Encoding.UTF8.GetBytes(Contract.Serialize(new{aud=uri.GetLeftPart(UriPartial.Authority),exp=now.AddHours(1).ToUnixTimeSeconds(),sub=config.Subject})));
        var input=header+"."+payload;using var signer=ECDsa.Create();signer.ImportECPrivateKey(Decode(config.PrivateKey),out _);
        return "vapid t="+input+"."+Encode(signer.SignData(Encoding.ASCII.GetBytes(input),HashAlgorithmName.SHA256,DSASignatureFormat.IeeeP1363FixedFieldConcatenation))+", k="+config.PublicKey;
    }
    public async Task ProcessAsync(CancellationToken ct){
        var config=store.PushSettings();if(!config.Enabled)return;
        using var budget=CancellationTokenSource.CreateLinkedTokenSource(ct);budget.CancelAfter(TimeSpan.FromSeconds(15));
        foreach(var job in store.PushJobs()){
            ct.ThrowIfCancellationRequested();
            if(budget.IsCancellationRequested)break;
            var entry=store.InboxEvent(job.User,job.NotificationId,await host.IsAdministratorAsync(job.User,ct));
            if(entry is null||entry.EventId!=job.EventId||!await host.UserExistsAsync(job.User,ct)||!await host.CanAccessNotificationAsync(job.User,entry,ct)||!NotificationService.Enabled(store.Preferences(job.User),entry,"in-app",true)||!NotificationService.CategoryEnabled(store.Preferences(job.User),entry,"in-app")){store.CompletePush(job,true);continue;}
            var failed=false;
            foreach(var target in store.PushTargets(job.User)){
                if(budget.IsCancellationRequested){failed=true;break;}
                if(!store.PushSettings().Enabled)break;
                if(!await host.UserExistsAsync(job.User,ct)||entry.AdminOnly&&!await host.IsAdministratorAsync(job.User,ct)||!await host.CanAccessNotificationAsync(job.User,entry,ct))break;
                if(!store.PushTargets(job.User).Any(t=>t.Id==target.Id))continue;
                try{
                    if(!AllowedEndpoint(target.Subscription.Endpoint)){store.RemovePush(job.User,target.Id);continue;}
                    var status=await (transport??new WebPushTransport()).SendAsync(target.Subscription.Endpoint,Authorization(config,target.Subscription.Endpoint,clock.GetUtcNow()),budget.Token);
                    if(status is HttpStatusCode.Gone or HttpStatusCode.NotFound)store.RemovePush(job.User,target.Id);
                    else failed|=(int)status>=500||(int)status==429;
                }catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}catch{failed=true;}
            }
            store.CompletePush(job,!failed);
        }
    }
}
