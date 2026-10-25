using Jellyfin.PluginSupport;
using MediaBrowser.Common.Api;
using MediaBrowser.Common.Plugins;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.DeletionQueue.Api;

[ApiController, Authorize(Policy = Policies.RequiresElevation), Route("DeletionQueue/admin/dependencies")]
public sealed class DependenciesController(IPluginManager plugins, LiveIdentity identity) : ControllerBase
{
    [HttpGet]
    public IActionResult Get()
    {
        _ = identity.Caller(User, administrator: true);
        Response.Headers.CacheControl = "no-store";
        return HubAdapterController.Success(new { items = new[] {
            PluginDependency.Check(plugins, "notifications", "Notifications (required for new removal requests)", new("c18d78ec-4b70-47cf-a0d2-936d987c37ba")),
            PluginDependency.Check(plugins, "account-hub", "Account Hub", new("bc9c3d3e-6da3-4ee5-91cd-c429d58ad901")),
            PluginDependency.Check(plugins, "discord", "Discord Bridge", new("4b047046-a550-4415-b204-4206f1f18201"))
        } });
    }
}
