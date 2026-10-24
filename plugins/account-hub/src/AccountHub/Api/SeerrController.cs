using AccountHub.Core;
using AccountHub.Security;
using Jellyfin.Plugin.UserManager.Services;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AccountHub.Core.Seerr;
using AccountHub.Seerr;
using System.Text.Json;

namespace AccountHub.Api;

[ApiController, Authorize(Policy = Policies.RequiresElevation), Route("AccountHub/api/v1/seerr"), TypeFilter(typeof(HubBodyFilter)), RequestSizeLimit(65536)]
public sealed class SeerrController(HubHost host, PluginSettingsService settings, SeerrClient seerr, SeerrLifecycle lifecycle, ExternalCommandGate commands, SeerrConnectionDiscovery discovery) : ControllerBase
{
    [HttpGet("connection")]
    public Task<IActionResult> Connection(CancellationToken ct) => Run(async () =>
    {
        await host.RequireAdministratorAsync(HttpContext, ct);
        var config = await settings.GetAsync(ct);
        return new { baseUrl = config.SeerrUrl, keyConfigured = !string.IsNullOrEmpty(config.SeerrApiKey), allowHttp = config.SeerrAllowHttp, allowPrivateNetwork = config.SeerrAllowPrivateNetwork,
            capabilities = (await lifecycle.StatusAsync(ct)).Capabilities,
            features = new { connectionTest = true, directory = true, reconciliation = true, quotas = true, deletionJobs = true, provisioning = true, verifiedLinks = true },
            warnings = new[] { "Seerr Manage users bypasses quotas; quota changes require that permission to be removed explicitly first.", "Account deletion removes Seerr request history through upstream semantics, never media files.", "Uncertain imports require review and never automatically create again." } };
    });
    [HttpPost("test")]
    public Task<IActionResult> Test(CancellationToken ct) => Run(async () =>
    {
        await host.RequireAdministratorAsync(HttpContext, ct);
        return new { status = "reachable", capabilities = await lifecycle.DiscoverAsync(ct) };
    });
    [HttpGet("users")]
    public Task<IActionResult> Users([FromQuery] int skip = 0, [FromQuery] int limit = 100, CancellationToken ct = default) => Run(async () =>
    {
        await host.RequireAdministratorAsync(HttpContext, ct);
        if (skip < 0 || limit is < 1 or > 100) throw new HubException("invalid_request", "Invalid pagination");
        var connection = await discovery.ResolveAsync(ct);
        var users = await seerr.Snapshot(connection.Settings).GetUsersAsync(limit, ct, skip);
        return new { items = users.Select(user => new { id = user.Id, username = user.Username, jellyfinUserId = user.JellyfinUserId, permissions = user.Permissions, protectedAccount = user.Id <= 1 || (user.Permissions & SeerrPermissions.Admin) != 0 }), skip, limit };
    });
    [HttpGet("jobs")]
    public Task<IActionResult> Jobs([FromQuery] int skip = 0, [FromQuery] int limit = 50, CancellationToken ct = default) => Run(async () =>
    {
        await host.RequireAdministratorAsync(HttpContext, ct);
        if (skip < 0 || limit is < 1 or > 100) throw new HubException("invalid_request", "Invalid pagination");
        var state = await lifecycle.StatusAsync(ct);
        return new { total = state.Jobs.Count, attention = state.Jobs.Count(j => j.State is "blocked" or "failed"), items = state.Jobs.OrderByDescending(j => j.Updated).Skip(skip).Take(limit).ToArray() };
    });
    [HttpGet("/AccountHub/api/v1/users/{id:guid}/seerr")]
    public Task<IActionResult> Detail(Guid id, CancellationToken ct) => Run(async () => { await host.RequireAdministratorAsync(HttpContext, ct); return await lifecycle.DriftAsync(id, ct); });
    [HttpGet("/AccountHub/api/v1/users/{id:guid}/seerr/requests")]
    public Task<IActionResult> Requests(Guid id, [FromQuery] int skip = 0, [FromQuery] int limit = 20, CancellationToken ct = default) => Run(async () =>
    { await host.RequireAdministratorAsync(HttpContext, ct); return await lifecycle.RequestsAsync(id, skip, limit, ct); });
    [HttpPost("commands/{operation}")]
    public Task<IActionResult> Command(string operation, JsonElement body, CancellationToken ct) => Run(async () =>
    {
        var caller = await host.RequireAdministratorAsync(HttpContext, ct);
        if (operation is not ("link" or "unlink" or "provision" or "desired" or "retry" or "cancel")) throw new HubException("invalid_request", "Unknown Seerr command");
        var payload = body.GetProperty("payload"); var commandId = body.GetProperty("commandId").GetGuid();
        return await commands.ExecuteAsync(caller.UserId, "seerr." + operation, commandId, body.GetProperty("revision").GetInt64(), payload,
            async (_, token) => await host.AuthorizeAsync(HttpContext, operation is "unlink" or "cancel" ? CommandRisk.Destructive : CommandRisk.Configuration, token),
            async token =>
            {
                object result;
                if (operation is "retry" or "cancel")
                {
                    var jobId = payload.GetProperty("jobId").GetGuid();
                    result = operation == "retry" ? await lifecycle.RetryAsync(jobId, token) : await lifecycle.CancelAsync(jobId, token);
                }
                else
                {
                    var id = payload.GetProperty("userId").GetGuid();
                    if (operation == "link") result = await lifecycle.LinkAsync(id, payload.GetProperty("seerrId").GetInt32(), token);
                    else if (operation == "unlink") result = new { unlinked = await lifecycle.UnlinkAsync(id, token) };
                    else
                    {
                        var desired = payload.TryGetProperty("desired", out var value) && value.ValueKind != JsonValueKind.Null ? value.Deserialize<SeerrDesired>(Contracts.Wire.Options) : null;
                        if (operation == "desired" && desired is null) throw new HubException("invalid_request", "Desired state is required");
                        result = await lifecycle.EnqueueAsync(commandId, caller.UserId, id, desired, operation == "provision", token);
                    }
                }
                return JsonSerializer.SerializeToElement(result, Contracts.Wire.Options);
            }, ct: ct);
    });
    private async Task<IActionResult> Run<T>(Func<Task<T>> work)
    {
        Response.Headers.CacheControl = "no-store";
        try { return new JsonResult(new { apiVersion = "1", ok = true, data = await work() }, Contracts.Wire.Options); }
        catch (SecurityFailure ex) { return Failure(ex.Code, ex.Status); }
        catch (HubException ex) { return Failure(ex.Code, ex.Code is "revision_changed" or "command_conflict" or "seerr_job_active" or "seerr_link_conflict" ? 409 : ex.Code == "administrator_protected" ? 403 : ex.Retryable ? 503 : 400); }
        catch (SeerrException ex) { return Failure("seerr_unavailable", ex.StatusCode is >= 400 and < 500 ? 409 : 502); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or System.Text.Json.JsonException or TaskCanceledException) { return Failure("seerr_unavailable", 502); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException) { return Failure("invalid_request", 400); }
    }
    private IActionResult Failure(string code, int status) => new JsonResult(new { apiVersion = "1", ok = false, error = new { code, message = code, retryable = status >= 500 } }, Contracts.Wire.Options) { StatusCode = status };
}
