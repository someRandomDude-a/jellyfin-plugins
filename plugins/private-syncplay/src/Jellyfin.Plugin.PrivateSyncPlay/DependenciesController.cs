using Jellyfin.PluginSupport;
using MediaBrowser.Common.Api;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.PrivateSyncPlay;

[ApiController, Authorize(Policy = Policies.RequiresElevation), Route("PrivateSyncPlay/admin/dependencies")]
public sealed class DependenciesController(IPluginManager plugins, IAuthorizationContext authorization, HubPolicyService policies) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var caller = await authorization.GetAuthorizationInfo(HttpContext);
        if (!caller.IsAuthenticated || caller.IsApiKey || !policies.IsAdministrator(caller.UserId)) return Forbid();
        Response.Headers.CacheControl = "no-store";
        return new JsonResult(HubWire.Success(new { items = new[] {
            PluginDependency.Check(plugins, "account-hub", "Account Hub", new("bc9c3d3e-6da3-4ee5-91cd-c429d58ad901")),
            PluginDependency.Check(plugins, "discord", "Discord Bridge", new("4b047046-a550-4415-b204-4206f1f18201"))
        } }), HubWire.Json);
    }
}
