using System.Text.Json;
using AccountHub.Api.Contracts;
using AccountHub.Core;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AccountHub.Api;

[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("AccountHub/api/v1")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AdminController(HubStore store, IAuthorizationContext authorization, HubWorkerHealth workers) : ControllerBase
{
    [HttpGet("health")]
    public Task<IActionResult> Health() => ForAdministratorAsync(() => Task.FromResult<IActionResult>(Envelope(
        ApiResponse.Success(new { ready = store.Ready, database = store.Ready ? "ok" : "unavailable", errorCode = store.FailureCode,
            backgroundWorkReady = workers.Healthy, workers = workers.Snapshot() }),
        store.Ready ? 200 : 503)));

    [HttpGet("status")]
    public Task<IActionResult> Status() => ForAdministratorAsync(() => Task.FromResult<IActionResult>(Envelope(ApiResponse.Success(new
    {
        hostVersion = ProducerDispatcher.HostVersion,
        targetAbi = typeof(MediaBrowser.Controller.Library.IUserManager).Assembly.GetName().Version?.ToString(),
        stage = "integration-preview",
        backgroundWorkReady = workers.Healthy, workers = workers.Snapshot(),
        features = new { audit = store.Ready, managementWrites = store.Ready, notifications = store.Ready && workers.IsRunning("notifications"), bell = true,
            invites = false, discord = true, seerr = false, security = true, domains = true }
    }))));

    // Audit query/export is deliberately not exposed before P2's Everything proof-body gate.
    private async Task<IActionResult> ForAdministratorAsync(Func<Task<IActionResult>> action)
    {
        var info = await authorization.GetAuthorizationInfo(HttpContext);
        if (!info.IsAuthenticated || info.IsApiKey || info.UserId == Guid.Empty)
            return Envelope(ApiResponse.Failure("unauthorized", "A current administrator session is required"), 403);
        return await action();
    }

    private static ContentResult Envelope(ApiResponse response, int status = 200) => new()
    {
        Content = JsonSerializer.Serialize(response, Wire.Options), ContentType = "application/json; charset=utf-8", StatusCode = status
    };
}
