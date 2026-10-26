using System.Net.WebSockets;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace AccountHub.Notifications;

// Single-use, short-lived handshake grants. Native authentication is rechecked
// throughout the socket; only an invalidation signal, never content, is sent.
public sealed class LiveNotifications(TimeProvider clock)
{
    private sealed record Grant(Guid User,string Header,ClaimsPrincipal Principal,string Scheme,HostString Host,string Path,DateTimeOffset Expires);
    private readonly object gate=new();
    private readonly Dictionary<string,Grant> tickets=[];
    private readonly Dictionary<Guid,int> active=[];
    public string Issue(HttpContext c,Guid user){
        var header=c.Request.Headers.Authorization.ToString();Validation.Require(header.Length is >0 and <=4096,"Native session required");
        lock(gate){foreach(var key in tickets.Where(x=>x.Value.Expires<=clock.GetUtcNow()).Select(x=>x.Key).ToArray())tickets.Remove(key);
            Validation.Require(tickets.Count<1000&&tickets.Values.Count(g=>g.User==user)<8,"Too many pending connections");
            var path=(c.Request.PathBase+c.Request.Path).Value!;Validation.Require(path.EndsWith("/notifications/live-ticket",StringComparison.Ordinal),"Invalid handshake scope");
            var token=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));tickets.Add(token,new(user,header,c.User,c.Request.Scheme,c.Request.Host,path[..^"-ticket".Length],clock.GetUtcNow().AddSeconds(30)));return token;}
    }
    private Grant? Redeem(HttpContext c){
        var protocols=c.WebSockets.WebSocketRequestedProtocols;
        if(!protocols.Contains("notifications.v1")||protocols.Count!=2)return null;
        var ticket=protocols.SingleOrDefault(p=>p.StartsWith("ticket.",StringComparison.Ordinal));if(ticket is null)return null;
        lock(gate){if(!tickets.Remove(ticket[7..],out var grant)||grant.Expires<=clock.GetUtcNow())return null;
            if(c.Request.Host!=grant.Host||c.Request.Scheme!=grant.Scheme||(c.Request.PathBase+c.Request.Path).Value!=grant.Path||c.Request.Headers.Origin!=grant.Scheme+"://"+grant.Host)return null;
            if(active.Values.Sum()>=64||active.GetValueOrDefault(grant.User)>=4)return null;active[grant.User]=active.GetValueOrDefault(grant.User)+1;return grant;}
    }
    private static HttpContext Fresh(HttpContext current,Grant grant){var c=new DefaultHttpContext{RequestServices=current.RequestServices,User=grant.Principal};c.Request.Scheme=grant.Scheme;c.Request.Host=grant.Host;c.Request.Headers.Authorization=grant.Header;c.Request.Headers.Origin=grant.Scheme+"://"+grant.Host;return c;}
    public async Task<IResult> ConnectAsync(HttpContext context,INotificationHost host,NotificationStore store){
        if(!context.WebSockets.IsWebSocketRequest)return Results.StatusCode(400);
        var grant=Redeem(context);if(grant is null)return Results.StatusCode(401);
        try{
            if(await host.CurrentUserAsync(Fresh(context,grant),context.RequestAborted)!=grant.User||!await host.UserExistsAsync(grant.User,context.RequestAborted))return Results.StatusCode(401);
            using var socket=await context.WebSockets.AcceptWebSocketAsync("notifications.v1");
            using var life=CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);life.CancelAfter(TimeSpan.FromMinutes(5));
            var receive=socket.ReceiveAsync(new byte[128].AsMemory(),life.Token).AsTask();string? previous=null;
            try{while(socket.State==WebSocketState.Open&&!life.IsCancellationRequested){
                if(await host.CurrentUserAsync(Fresh(context,grant),life.Token)!=grant.User||!await host.UserExistsAsync(grant.User,life.Token))break;
                var digest=store.LiveDigest(grant.User);if(digest!=previous){previous=digest;await socket.SendAsync(Encoding.UTF8.GetBytes("changed").AsMemory(),WebSocketMessageType.Text,true,life.Token);}
                if(await Task.WhenAny(receive,Task.Delay(TimeSpan.FromSeconds(3),life.Token))==receive)break;
            }}catch(OperationCanceledException){}catch(WebSocketException){}
            finally{life.Cancel();try{await receive;}catch(OperationCanceledException){}catch(WebSocketException){}if(socket.State is WebSocketState.Open or WebSocketState.CloseReceived)try{await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure,"Refresh authentication",CancellationToken.None);}catch(WebSocketException){}}
            return Results.Empty;
        }finally{lock(gate){if(active.TryGetValue(grant.User,out var count)){if(count<=1)active.Remove(grant.User);else active[grant.User]=count-1;}}}
    }
}
