using System.Text.Json;
using AccountHub.Core;
using AccountHub.Notifications;
using AccountHub.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace AccountHub.Api;

[ApiController,Route("AccountHub"),RequestSizeLimit(4096)]
public sealed class LinkContinuationController(HubHost host,DiscordLinkService links,LinkContinuationStore continuations,IngressLimiter limiter):ControllerBase
{
    public const string CookieName="AccountHub.LinkContinuation";
    private CookieOptions Cookie()=>new(){HttpOnly=true,Secure=Request.IsHttps,SameSite=SameSiteMode.Strict,IsEssential=true,Path=Request.PathBase+"/AccountHub/"};
    private void Origin()
    {
        if(Request.Query.Count!=0)throw new ArgumentException();
        if(Request.Headers.TryGetValue("Origin",out var origin)&&origin!=$"{Request.Scheme}://{Request.Host}")throw new SecurityFailure("forbidden",403);
    }
    [AllowAnonymous,HttpPost("public/v1/discord/link/continue")]
    public Task<IActionResult> Begin(JsonElement body,CancellationToken ct)=>Guard(async()=>{
        Origin();
        if(Request.Headers.ContainsKey("Authorization")||Request.Headers.ContainsKey("X-Emby-Token"))throw new ArgumentException();
        if(!limiter.Allow("link-continuation-ip:"+DiscordLinkService.Hash(HttpContext.Connection.RemoteIpAddress?.ToString()??"unknown"),10))throw new SecurityFailure("rate_limited",429);
        if(body.GetProperty("confirmation").GetString()!="REVIEW AFTER SIGN-IN")throw new ArgumentException();
        var token=body.GetProperty("token").GetString();
        if(token is not {Length:43}||token.Any(c=>!char.IsAsciiLetterOrDigit(c)&&c is not ('-' or '_')))throw new ArgumentException();
        var challenge=await links.LookupAsync(token,ct);if(challenge is null)return Error("link_expired",400);
        var nonce=continuations.Prepare(token,DateTimeOffset.FromUnixTimeMilliseconds(challenge.ExpiresAtUnixMs),Request.Cookies[CookieName]);
        // A session-only opaque transport cookie, not a stored challenge or login
        // credential. No Expires/MaxAge: the server's volatile entry expires first.
        Response.Cookies.Append(CookieName,nonce,Cookie());return Success(new{status="prepared"});
    });
    [Authorize,HttpPost("api/v1/discord/link/continuation")]
    public Task<IActionResult> Resume(CancellationToken ct)=>Guard(async()=>{
        Origin();var actor=await host.CurrentUserAsync(HttpContext,ct);if(actor is null)return Error("unauthorized",401);
        if(!limiter.Allow("link-continuation-user:"+actor.Value,10))throw new SecurityFailure("rate_limited",429);
        var token=continuations.Take(Request.Cookies[CookieName]);Response.Cookies.Delete(CookieName,Cookie());
        if(token is null||await links.LookupAsync(token,ct) is null)return Error("link_expired",404);
        // Authentication resumes a review only. The unchanged confirmation route
        // still verifies the exact reviewed account, native access and consent.
        return Success(new{token});
    });
    private async Task<IActionResult> Guard(Func<Task<IActionResult>> action)
    {
        Response.Headers.CacheControl="no-store, private";Response.Headers["Referrer-Policy"]="no-referrer";Response.Headers["X-Content-Type-Options"]="nosniff";
        try{return await action();}
        catch(SecurityFailure error){return Error(error.Code,error.Status);}
        catch(HubException error){return Error(error.Code,error.Retryable?503:400);}
        catch(Exception error)when(error is JsonException or KeyNotFoundException or ArgumentException or InvalidOperationException){return Error("invalid_request",400);}
    }
    private static IActionResult Success(object data)=>new JsonResult(new{apiVersion="1",ok=true,data},Contracts.Wire.Options);
    private static IActionResult Error(string code,int status)=>new JsonResult(new{apiVersion="1",ok=false,error=new{code,message=AccountHub.Api.UserErrors.Message(code,status),retryable=false}},Contracts.Wire.Options){StatusCode=status};
}
