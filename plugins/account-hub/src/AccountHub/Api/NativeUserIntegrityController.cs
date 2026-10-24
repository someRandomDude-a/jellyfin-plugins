using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AccountHub.Api;

[ApiController, Authorize(Policy = Policies.RequiresElevation), Route("AccountHub/api/v1/native-users/integrity")]
public sealed class NativeUserIntegrityController(HubHost host, NativeUserIntegrity integrity) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Status(CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        try
        {
            await host.RequireAdministratorAsync(HttpContext, ct);
            return new JsonResult(new { apiVersion = "1", ok = true, data = integrity.Snapshot });
        }
        catch (AccountHub.Security.SecurityFailure ex)
        {
            return new JsonResult(new { apiVersion = "1", ok = false, error = new { code = ex.Code } }) { StatusCode = ex.Status };
        }
    }
}
