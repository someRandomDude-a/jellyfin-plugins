using AccountHub.Security;
using Jellyfin.PluginSupport;
using MediaBrowser.Common.Api;
using MediaBrowser.Common.Plugins;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AccountHub.Api;

[ApiController, Authorize(Policy = Policies.RequiresElevation), Route("AccountHub/api/v1/dependencies")]
public sealed class DependenciesController(HubHost host, IPluginManager plugins, ISecurityCompatibility security) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        try
        {
            await host.RequireAdministratorAsync(HttpContext, ct);
            var baseline = security.Check();
            var items = new[] {
                PluginDependency.Check(plugins, "deletion-queue", "Deletion Queue", new("3de88ba1-6d5c-49f5-854b-65a72f473f21")),
                PluginDependency.Check(plugins, "automatic", "Automatic Deletion Requests", new("a6429fb2-c67c-459d-b6a7-579c4a05dace")),
                PluginDependency.Check(plugins, "private-syncplay", "Private SyncPlay", new("09514a9e-f7a0-4c5a-91bc-3a46338298ca")),
                PluginDependency.Check(plugins, "discord", "Discord Bridge", new("4b047046-a550-4415-b204-4206f1f18201")),
                new DependencyHealth("security", "Jellyfin Security", baseline.IsOk, baseline.State, baseline.IsOk ? "Native 2FA status and safe actions are available." : "Native Security status/actions require the supported optional Jellyfin Security plugin. Core account management remains available with explicitly confirmed fallback authorization.", "Install and enable the supported Jellyfin Security jf12 package (v2.6.4), restart, then match the step-up mirror. Never disable 2FA to repair an integration.")
            };
            return new JsonResult(new { apiVersion = "1", ok = true, data = new { items } }, Contracts.Wire.Options);
        }
        catch (SecurityFailure ex) { return new JsonResult(new { apiVersion = "1", ok = false, error = new { code = ex.Code } }, Contracts.Wire.Options) { StatusCode = ex.Status }; }
    }
}
