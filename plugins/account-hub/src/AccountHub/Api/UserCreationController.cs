using System.Text.Json;
using AccountHub.Core;
using AccountHub.Security;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AccountHub.Api;

[ApiController, Authorize(Policy = Policies.RequiresElevation), Route("AccountHub/api/v1/user-creation"), TypeFilter(typeof(HubBodyFilter)), RequestSizeLimit(16384)]
public sealed class UserCreationController(HubHost host, UserCreationOwnership ownership, ExternalCommandGate commands) : ControllerBase
{
    [HttpGet]
    public Task<IActionResult> Status(CancellationToken ct) => Run(async () => { await host.RequireAdministratorAsync(HttpContext, ct); return ownership.Snapshot; });
    [HttpPost("native-override")]
    public Task<IActionResult> Override(JsonElement body, CancellationToken ct) => Run(async () =>
    {
        var actor = await host.RequireAdministratorAsync(HttpContext, ct);
        var enabled = body.GetProperty("enabled").GetBoolean();
        return await commands.ExecuteAsync(actor.UserId, "user-creation.native-override", body.GetProperty("commandId").GetGuid(), body.GetProperty("revision").GetInt64(), new { enabled },
            async (_, token) => await host.AuthorizeAsync(HttpContext, CommandRisk.Destructive, token),
            async token => JsonSerializer.SerializeToElement(await ownership.SetNativeOverrideAsync(enabled, token)), ct: ct);
    });
    private async Task<IActionResult> Run(Func<Task<object>> action)
    {
        Response.Headers.CacheControl = "no-store";
        try { return new JsonResult(new { apiVersion = "1", ok = true, data = await action() }); }
        catch (SecurityFailure ex) { return Failure(ex.Code, ex.Status); }
        catch (HubException ex) { return Failure(ex.Code, ex.Code is "revision_changed" or "command_conflict" ? 409 : ex.Retryable ? 503 : 400); }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException) { return Failure("invalid_request", 400); }
    }
    private IActionResult Failure(string code, int status) => new JsonResult(new { apiVersion = "1", ok = false, error = new { code } }) { StatusCode = status };
}
