using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AccountHub.Security;

[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("AccountHub/api/v1/security")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class SecurityController(ISecurityHubHost host, ISecurityCompatibility compatibility,
    SecurityClient client, CommandAuthorization authorization) : ControllerBase
{
    [HttpGet("status")]
    public Task<IActionResult> Status(CancellationToken ct) => Run(async () =>
    {
        await host.RequireAdministratorAsync(HttpContext, ct);
        var (settings, revision) = await host.ReadSettingsAsync(ct);
        var health = compatibility.Check();
        return new { compatibility = health.State, health.InstalledVersion, health.SupportedVersion,
            health.SecurityTabVisible, health.SecurityWritesEnabled, health.StepUpAvailable,
            stepUpLevel = settings.StepUpLevelMirror, settings.StepUpLevelSource, settings.AlertMode,
            settings.PasswordLoginPolicy, revision, mirrorWarning = SecuritySettings.MirrorWarning,
            cleanupStatus = "unverified" };
    });

    [HttpGet("users")]
    public Task<IActionResult> Users([FromQuery] int skip = 0, [FromQuery] int limit = 50,
        [FromQuery] bool refresh = false, CancellationToken ct = default) => Run(async () =>
    {
        Page(skip, limit);
        var caller = await host.RequireAdministratorAsync(HttpContext, ct);
        var users = await client.UsersAsync(caller, ct, refresh);
        return new { items = users.Skip(skip).Take(limit).ToArray(), total = users.Length };
    });

    [HttpGet("users/{id:guid}/summary")]
    public Task<IActionResult> Summary(Guid id, CancellationToken ct) => Run(async () =>
    {
        var caller = await host.RequireAdministratorAsync(HttpContext, ct);
        return await client.SummaryAsync(caller, id, ct);
    });

    [HttpGet("devices")]
    public Task<IActionResult> Devices([FromQuery] int skip = 0, [FromQuery] int limit = 50,
        [FromQuery] bool refresh = false, CancellationToken ct = default) => Run(async () =>
    {
        Page(skip, limit);
        var caller = await host.RequireAdministratorAsync(HttpContext, ct);
        var devices = await client.DevicesAsync(caller, ct, refresh);
        return new { items = devices.Skip(skip).Take(limit).ToArray(), total = devices.Length };
    });

    // POST wrapper avoids unverified DELETE-body behavior through proxies. No proof in URL.
    [HttpPost("devices/{userId:guid}/{recordId}/revoke")]
    public Task<IActionResult> Revoke(Guid userId, string recordId, ActionRequest request, CancellationToken ct) =>
        Action("security.device.revoke", userId, recordId, request, ct);

    [HttpPost("users/{id:guid}/force-logout")]
    public Task<IActionResult> ForceLogout(Guid id, ActionRequest request, CancellationToken ct) =>
        Action("security.force_logout", id, null, request, ct);

    [HttpPost("webhook-test")]
    public Task<IActionResult> WebhookTest(ActionRequest request, CancellationToken ct) =>
        Action("security.webhook_test", null, null, request, ct);

    [HttpPut("settings")]
    public Task<IActionResult> Settings(SettingsRequest request, CancellationToken ct) => Run(async () =>
    {
        try
        {
            Validate(request.CommandId, request.Revision);
            request.Settings.Validate();
            var caller = await host.RequireAdministratorAsync(HttpContext, ct);
            return await host.ExecuteAsync(caller, new(request.CommandId, request.Revision,
                "security.settings", null, null, request.Settings), async (old, token) =>
            {
                // Lowering from Destructive to Off is destructive even though other settings are config.
                var risk = request.Settings.StepUpLevelMirror < old.StepUpLevelMirror
                    ? CommandRisk.Destructive : CommandRisk.Configuration;
                var evidence = await authorization.AuthorizeAsync(caller, old, risk, false,
                    request.Confirmation, "CHANGE SECURITY SETTINGS", request.StepUp, token);
                return (new CommandOutcome("applied"), evidence);
            }, ct);
        }
        finally { request.StepUp?.Dispose(); }
    });

    private Task<IActionResult> Action(string operation, Guid? userId, string? recordId, ActionRequest request, CancellationToken ct) => Run(async () =>
    {
        try
        {
            Validate(request.CommandId, request.Revision);
            if (userId == Guid.Empty || recordId is not null && !SecurityClient.ValidRecordId(recordId))
                throw new SecurityFailure("invalid_request", 400);
            var caller = await host.RequireAdministratorAsync(HttpContext, ct);
            if (!compatibility.Check().IsOk) throw new SecurityFailure("security_incompatible", 409);
            return await host.ExecuteAsync(caller, new(request.CommandId, request.Revision, operation, userId, recordId, null), async (old, token) =>
            {
                if (recordId is not null)
                {
                    var devices = await client.DevicesAsync(caller, token, refresh: true);
                    if (!devices.Any(d => d.UserId == userId && d.Id == recordId))
                        throw new SecurityFailure("not_found", 404);
                }
                var test = operation == "security.webhook_test";
                var expected = test ? "TEST ALL SECURITY CHANNELS" : $"{(recordId is null ? "FORCE LOGOUT" : "REVOKE TRUST")} {userId:D}";
                var evidence = await authorization.AuthorizeAsync(caller, old,
                    test ? CommandRisk.Configuration : CommandRisk.Destructive, true,
                    request.Confirmation, expected, request.StepUp, token);
                try
                {
                    if (test) await client.WebhookTestAsync(caller, token);
                    else if (recordId is not null) await client.RevokeAsync(caller, userId!.Value, recordId, token);
                    else
                    {
                        var count = await client.ForceLogoutAsync(caller, userId!.Value, token);
                        return (new CommandOutcome("upstream_reported", count, Detail: "Trust and registered IDs cleared; sessions termination reported by Security, not independently verified."), evidence);
                    }
                    return (new CommandOutcome(test ? "test_requested" : "upstream_reported"), evidence);
                }
                catch (SecurityFailure ex) when (ex.Status >= 500)
                {
                    return (new CommandOutcome("unknown", RemediationPending: true,
                        Detail: "Upstream may have applied the action. Inspect current state; do not blindly retry."), evidence);
                }
            }, ct);
        }
        finally { request.StepUp?.Dispose(); }
    });

    private async Task<IActionResult> Run<T>(Func<Task<T>> action)
    {
        Response.Headers.CacheControl = "no-store";
        try { return new JsonResult(new { apiVersion = "1", ok = true, data = await action() }, SecurityJson.Options); }
        catch (SecurityFailure ex)
        {
            if (ex.RetryAfter is int seconds) Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return new JsonResult(new { apiVersion = "1", ok = false,
                error = new { code = ex.Code, message = ex.Code, retryable = ex.Retryable } }, SecurityJson.Options) { StatusCode = ex.Status };
        }
    }

    private static void Validate(Guid commandId, long revision)
    {
        if (commandId == Guid.Empty || revision < 0) throw new SecurityFailure("invalid_request", 400);
    }
    private static void Page(int skip, int limit)
    {
        if (skip < 0 || limit is < 1 or > 200) throw new SecurityFailure("invalid_request", 400);
    }
}
