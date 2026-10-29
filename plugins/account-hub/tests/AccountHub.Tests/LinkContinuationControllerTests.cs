using System.Reflection;
using System.Text.Json;
using AccountHub.Api;
using AccountHub.Core;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AccountHub.Tests;

public sealed class LinkContinuationControllerTests
{
    public class Proxy:DispatchProxy
    {
        public Func<MethodInfo,object?[],object?> Call=null!;
        protected override object? Invoke(MethodInfo? method,object?[]? args)=>Call(method!,args??[]);
    }
    private static T Fake<T>(Func<MethodInfo,object?[],object?> call)where T:class{var value=DispatchProxy.Create<T,Proxy>();((Proxy)(object)value).Call=call;return value;}
    private static JsonElement Body(object value)=>JsonSerializer.SerializeToElement(value);
    private static JsonElement Value(IActionResult result)=>Body(Assert.IsType<JsonResult>(result).Value!);
    private static DefaultHttpContext Context(bool https=true)
    {
        var context=new DefaultHttpContext();context.Request.Scheme=https?"https":"http";context.Request.Host=new HostString("fixture.test");context.Request.PathBase="/suite";
        context.Request.Headers.Origin=(https?"https":"http")+"://fixture.test";return context;
    }
    [Theory,InlineData(true),InlineData(false)] public async Task AnonymousPreparationUsesAScopedSessionCookieWithoutReturningTheChallenge(bool https)
    {
        await using var fixture=new StoreFixture();await fixture.InitializeAsync();var links=new DiscordLinkService(fixture.Store,fixture.Clock);var store=new LinkContinuationStore(fixture.Clock);
        var token=await links.PrepareAsync("123456789012345678",default);var context=Context(https);
        var controller=new LinkContinuationController(null!,links,store,new(fixture.Clock)){ControllerContext=new(){HttpContext=context}};
        var result=Value(await controller.Begin(Body(new{token,confirmation="REVIEW AFTER SIGN-IN"}),default));Assert.True(result.GetProperty("ok").GetBoolean());Assert.DoesNotContain(token,result.ToString());
        var cookie=context.Response.Headers.SetCookie.ToString();Assert.Contains("path=/suite/AccountHub/",cookie);Assert.Contains("httponly",cookie);Assert.Contains("samesite=strict",cookie);
        Assert.Equal(https,cookie.Contains("secure"));Assert.DoesNotContain("expires=",cookie);Assert.DoesNotContain("max-age=",cookie);Assert.DoesNotContain(token,cookie);Assert.Contains("no-store",context.Response.Headers.CacheControl.ToString());
        Assert.NotNull(await links.LookupAsync(token,default));
    }
    [Theory,InlineData("origin"),InlineData("authorization"),InlineData("header-token"),InlineData("query"),InlineData("consent")]
    public async Task InvalidPreparationCannotIssueACookie(string mode)
    {
        await using var fixture=new StoreFixture();await fixture.InitializeAsync();var links=new DiscordLinkService(fixture.Store,fixture.Clock);var token=await links.PrepareAsync("123456789012345678",default);var context=Context();
        if(mode=="origin")context.Request.Headers.Origin="https://attacker.test";
        if(mode=="authorization")context.Request.Headers.Authorization="MediaBrowser Token=\"api-key\"";
        if(mode=="header-token")context.Request.Headers["X-Emby-Token"]="api-key";
        if(mode=="query")context.Request.QueryString=new("?api_key=private");
        var controller=new LinkContinuationController(null!,links,new(fixture.Clock),new(fixture.Clock)){ControllerContext=new(){HttpContext=context}};
        var result=await controller.Begin(Body(new{token,confirmation=mode=="consent"?"wrong":"REVIEW AFTER SIGN-IN"}),default);Assert.False(Value(result).GetProperty("ok").GetBoolean());Assert.Equal(0,context.Response.Headers.SetCookie.Count);Assert.NotNull(await links.LookupAsync(token,default));
    }
    [Theory,InlineData("valid"),InlineData("api-key"),InlineData("anonymous"),InlineData("disabled"),InlineData("origin"),InlineData("mismatch")]
    public async Task NativeAuthorizationPrecedesOneUseTransportConsumption(string mode)
    {
        await using var fixture=new StoreFixture();await fixture.InitializeAsync();var links=new DiscordLinkService(fixture.Store,fixture.Clock);var token=await links.PrepareAsync("123456789012345678",default);var store=new LinkContinuationStore(fixture.Clock);var nonce=store.Prepare(token,fixture.Clock.Now.AddMinutes(5));
        var user=new User("review-user","auth","reset"){Id=Guid.NewGuid()};if(mode=="disabled")user.SetPermission(PermissionKind.IsDisabled,true);
        using var services=new ServiceCollection()
            .AddSingleton(Fake<IUserManager>((method,_)=>method.Name=="GetUserById"?user:throw new NotSupportedException()))
            .AddSingleton(Fake<IAuthorizationContext>((method,_)=>method.Name=="GetAuthorizationInfo"?Task.FromResult(new AuthorizationInfo{IsAuthenticated=mode!="anonymous",IsApiKey=mode=="api-key",User=user,Token="native-session"}):throw new NotSupportedException()))
            .BuildServiceProvider();
        var context=Context();context.Request.Headers.Cookie=LinkContinuationController.CookieName+"="+nonce;context.Request.Headers.Authorization="MediaBrowser Token=\""+(mode=="mismatch"?"wrong-session":"native-session")+"\"";
        if(mode=="origin")context.Request.Headers.Origin="https://attacker.test";
        var host=new HubHost(services,fixture.Store,new(fixture.Store,fixture.Clock));var controller=new LinkContinuationController(host,links,store,new(fixture.Clock)){ControllerContext=new(){HttpContext=context}};
        var result=await controller.Resume(default);
        if(mode=="valid"){
            Assert.Equal(token,Value(result).GetProperty("data").GetProperty("token").GetString());Assert.Null(store.Take(nonce));Assert.Contains("expires=Thu, 01 Jan 1970",context.Response.Headers.SetCookie.ToString());Assert.Contains("path=/suite/AccountHub/",context.Response.Headers.SetCookie.ToString());
            Assert.Equal(404,Assert.IsType<JsonResult>(await controller.Resume(default)).StatusCode);
        }else{Assert.False(Value(result).GetProperty("ok").GetBoolean());Assert.Equal(token,store.Take(nonce));Assert.Equal(0,context.Response.Headers.SetCookie.Count);}
        Assert.NotNull(await links.LookupAsync(token,default));
    }
}
