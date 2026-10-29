using System.Text.Json;
using AccountHub.Core;
using AccountHub.Groups;
using AccountHub.Security;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AccountHub.Api;

[ApiController, Authorize(Policy = Policies.RequiresElevation), Route("AccountHub/api/v1/group-policy"), TypeFilter(typeof(HubBodyFilter)), RequestSizeLimit(65536)]
public sealed class GroupPolicyController(GroupPolicyService policies, GroupDriftObserver observer, HubHost host, ExternalCommandGate commands) : ControllerBase
{
    [HttpGet("groups")]
    public Task<IActionResult> Groups(int skip = 0, int limit = 50, CancellationToken ct = default) => Read(() => policies.GroupsAsync(skip, limit, ct), ct);
    [HttpGet("members")]
    public Task<IActionResult> Members(Guid? groupId = null, int skip = 0, int limit = 50, CancellationToken ct = default) => Read(() => policies.MembersAsync(groupId, skip, limit, ct), ct);
    [HttpGet("users/{id:guid}/drift")]
    public Task<IActionResult> Drift(Guid id, CancellationToken ct) => Read(() => policies.DriftAsync(id, ct), ct);
    [HttpGet("drift-status")]
    public Task<IActionResult> DriftStatus(CancellationToken ct) => Read(() => Task.FromResult(new { observer.State, observer.CheckedUsers, observer.DriftedUsers }), ct);
    [HttpGet("users/{id:guid}/snapshots")]
    public Task<IActionResult> Snapshots(Guid id, int skip = 0, int limit = 50, CancellationToken ct = default) => Read(() => policies.SnapshotsAsync(id, skip, limit, ct), ct);
    [HttpPost("preview")]
    public Task<IActionResult> Preview(JsonElement body, CancellationToken ct) => Read(() => policies.PreviewAsync(body.GetProperty("targets").Deserialize<PolicyTarget[]>(Contracts.Wire.Options)!, ct, body.TryGetProperty("mode", out var mode) ? mode.GetString()! : "apply"), ct);
    [HttpPost("commands/{operation}")]
    public Task<IActionResult> Command(string operation, JsonElement body, CancellationToken ct) => Run(async () =>
    {
        var caller = await host.RequireAdministratorAsync(HttpContext, ct);
        if (operation is not ("group.save" or "group.copy-user" or "group.delete" or "assign" or "apply" or "reset" or "rollback")) throw new HubException("invalid_request", "Unknown group command");
        var payload = body.GetProperty("payload"); var commandId = body.GetProperty("commandId").GetGuid();
        Guid? copySource=null;if(operation=="group.copy-user"){
            if(!Guid.TryParse(payload.GetProperty("sourceUserId").GetString(),out var source)||source==Guid.Empty)throw new HubException("invalid_request","Invalid source user");copySource=source;
        }
        return await commands.ExecuteAsync(caller.UserId, "group-policy." + operation, commandId, body.GetProperty("revision").GetInt64(), payload,
            async (_, token) => await host.AuthorizeAsync(HttpContext, operation is "group.save" or "assign" ? CommandRisk.Configuration : CommandRisk.Destructive, token),
            async token =>
            {
                object result;
                if(operation=="group.copy-user"){
                    var native=HttpContext.RequestServices.GetRequiredService<Jellyfin.Plugin.UserManager.Services.JellyfinUserService>();var source=copySource!.Value;
                    if(native.GetUser(source) is null)throw new HubException("invalid_request","Unknown source user");
                    var policy=JsonSerializer.SerializeToElement(ManagedPolicy.Project(native.GetPolicy(source)));
                    result=await policies.SaveGroupAsync(payload.GetProperty("id").GetGuid(),0,payload.GetProperty("name").GetString()!,"Copied access settings",policy,token);
                }
                else if (operation == "group.save") result = await policies.SaveGroupAsync(payload.GetProperty("id").GetGuid(), payload.GetProperty("groupRevision").GetInt64(), payload.GetProperty("name").GetString()!, payload.GetProperty("description").GetString() ?? "", payload.GetProperty("managedFields"), token);
                else if (operation == "group.delete") result = new { deleted = await policies.DeleteGroupAsync(payload.GetProperty("id").GetGuid(), payload.GetProperty("groupRevision").GetInt64(), token) };
                else if (operation == "assign") result = await policies.AssignAsync(payload.GetProperty("userId").GetGuid(), payload.GetProperty("assignmentRevision").GetInt64(), payload.GetProperty("groupId").ValueKind == JsonValueKind.Null ? null : payload.GetProperty("groupId").GetGuid(), payload.GetProperty("groupRevision").GetInt64(), payload.GetProperty("overrides"), token);
                else
                {
                    var outcomes = await policies.ApplyAsync(caller.UserId, commandId, payload.GetProperty("targets").Deserialize<PolicyTarget[]>(Contracts.Wire.Options)!, operation,
                        payload.TryGetProperty("snapshotId", out var snapshot) ? snapshot.GetGuid() : null, token);
                    result = new { outcomes, skippedAdmins = outcomes.Where(x => x.Status == "administrator_skipped").Select(x => x.UserId), remediationPending = outcomes.Any(x => x.Status == "remediation_pending") };
                }
                return JsonSerializer.SerializeToElement(result, Contracts.Wire.Options);
            }, ct: ct);
    });
    private Task<IActionResult> Read<T>(Func<Task<T>> action, CancellationToken ct) => Run(async () => { await host.RequireAdministratorAsync(HttpContext, ct); return await action(); });
    private async Task<IActionResult> Run<T>(Func<Task<T>> action)
    {
        Response.Headers.CacheControl = "no-store";
        try { return new JsonResult(new { apiVersion = "1", ok = true, data = await action() }, Contracts.Wire.Options); }
        catch (SecurityFailure ex) { return Failure(ex.Code, ex.Status); }
        catch (HubException ex) { return Failure(ex.Code, ex.Code == "administrator_protected" ? 403 : ex.Code is "revision_changed" or "command_conflict" ? 409 : ex.Retryable ? 503 : 400); }
        catch (DbUpdateException) { return Failure("revision_changed", 409); }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or ArgumentException or InvalidOperationException) { return Failure("invalid_request", 400); }
    }
    private IActionResult Failure(string code, int status) => new JsonResult(new { apiVersion = "1", ok = false, error = new { code, message = code, retryable = status >= 500 } }, Contracts.Wire.Options) { StatusCode = status };
}

[ApiController, AllowAnonymous, Route("AccountHub/assets/group-policy.mjs")]
public sealed class GroupPolicyAssetsController : ControllerBase
{
    [HttpGet]
    public IActionResult Script() => File(typeof(GroupPolicyAssetsController).Assembly.GetManifestResourceStream("AccountHub.Web.group-policy.mjs")!, "text/javascript");
}
