using AccountHub.Api;
using Microsoft.AspNetCore.Mvc;
using Xunit;
using Jellyfin.Plugin.PrivateSyncPlay;
using MediaBrowser.Common.Configuration;
using Microsoft.AspNetCore.Http;
using System.Reflection;

namespace AccountHub.Tests;

public sealed class InviteRouteTests
{
    [Fact]
    public void InviteCommandsDoNotUseMvcReservedActionRouteValue()
    {
        var method = typeof(InviteOnboardingController).GetMethod(nameof(InviteOnboardingController.Change))!;
        var route = Assert.Single(method.GetCustomAttributes(typeof(HttpPostAttribute), false).Cast<HttpPostAttribute>());
        Assert.Equal("api/v1/invites/{id:guid}/{operation}", route.Template);
        Assert.Contains(method.GetParameters(), parameter => parameter.Name == "operation");
    }
    [Fact] public void InvitationUsesNativeStandaloneLoginPlacementAndKeepsStatusOutsideTheHiddenForm()
    {
        var paths=DispatchProxy.Create<IApplicationPaths,EmptyPaths>();
        var controller=new InviteOnboardingController(null!,null!,null!,null!,new NativeWebStyles(paths),null!,null!,null!){ControllerContext=new ControllerContext{HttpContext=new DefaultHttpContext()}};
        controller.Request.PathBase="/suite";var page=Assert.IsType<ContentResult>(controller.Page());var html=page.Content!;
        Assert.Contains("id=\"loginPage\" class=\"page standalonePage backdropPage\"",html);
        Assert.Contains("style=\"position:relative\"",html); // Keep fields above the native fixed background layer.
        Assert.Contains("lang=\"en\" dir=\"ltr\"",html); // Native directional padding selectors require an explicit direction.
        Assert.Contains("padded-bottom-page margin-auto-y",html);Assert.Contains("class=\"manualLoginForm margin-auto-x\" hidden",html);
        Assert.True(html.IndexOf("id=\"invite-status\"",StringComparison.Ordinal)<html.IndexOf("<form",StringComparison.Ordinal));
        Assert.Contains("<base href=\"/suite/AccountHub/\">",html);Assert.Contains("../Branding/Css",html);Assert.DoesNotContain("TwoFactorAuth/Setup",html);
    }
    public class EmptyPaths:DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method,object?[]? args)=>method?.ReturnType==typeof(string)?"":throw new NotSupportedException();
    }
}
