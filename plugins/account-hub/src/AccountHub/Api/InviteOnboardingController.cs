using System.Net;
using System.Text.Json;
using AccountHub.Core;
using AccountHub.Security;
using AccountHub.Notifications;
using Jellyfin.Plugin.PrivateSyncPlay;
using Jellyfin.Plugin.UserManager.Data;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using DiscordBridge.Api;

namespace AccountHub.Api;

[ApiController, Route("AccountHub"), TypeFilter(typeof(HubBodyFilter)), RequestSizeLimit(32768)]
public sealed class InviteOnboardingController(InviteOnboardingService invites, HubHost host, IngressLimiter limiter, AccountHub.Groups.GroupPolicyService groups, NativeWebStyles styles, DiscordBridgeClient bridge, ExternalCommandGate commands, UserCreationOwnership ownership) : ControllerBase
{
    [Authorize(Policy = Policies.RequiresElevation), HttpGet("api/v1/invites")]
    public Task<IActionResult> List([FromQuery] int skip = 0, [FromQuery] int limit = 50, CancellationToken ct = default) => Run(async () => { await host.RequireAdministratorAsync(HttpContext, ct); return await invites.ListAsync(skip, limit, ct); });
    [Authorize(Policy = Policies.RequiresElevation), HttpGet("api/v1/invites/{id:guid}")]
    public Task<IActionResult> Detail(Guid id, CancellationToken ct) => Run(async () => { await host.RequireAdministratorAsync(HttpContext, ct); return await invites.DetailAsync(id, ct); });
    [Authorize(Policy = Policies.RequiresElevation), HttpPost("api/v1/invites")]
    public Task<IActionResult> Create(JsonElement body, CancellationToken ct) => Run(async () =>
    {
        var caller = await host.RequireAdministratorAsync(HttpContext, ct);
        var group = body.TryGetProperty("groupId", out var g) && g.ValueKind == JsonValueKind.String ? g.GetString() : null;
        var policy = "{}";
        if (group is not null)
        {
            if (!Guid.TryParse(group, out var groupId)) throw new HubException("invalid_request", "Invalid group ID");
            var template = await groups.GroupAsync(groupId, ct);
            policy = template.PolicyJson;
            _ = ManagedPolicy.Apply(new MediaBrowser.Model.Users.UserPolicy(), JsonSerializer.Deserialize<JsonElement>(policy));
        }
        string? discordId = null; var copyLink = true;
        if (body.TryGetProperty("delivery", out var delivery))
        {
            copyLink = !delivery.TryGetProperty("copyLink", out var copy) || copy.ValueKind == JsonValueKind.True;
            if (delivery.TryGetProperty("discordDm", out var dm) && dm.ValueKind == JsonValueKind.True)
            {
                discordId = delivery.GetProperty("discordUserId").GetString();
                var health = await bridge.CallAsync("health", new { }, ct);
                if (health is not { } h || !h.GetProperty("ok").GetBoolean() || !h.GetProperty("data").GetProperty("connected").GetBoolean()) throw new HubException("delivery_unavailable", "Discord Bridge is not connected");
            }
        }
        return await invites.CreateAsync(caller.UserId, body.GetProperty("commandId").GetGuid(), body.GetProperty("revision").GetInt64(), new InviteRequest(
            body.TryGetProperty("expiresInMinutes", out var expiry) ? expiry.GetInt32() : 10080,
            body.TryGetProperty("maxUses", out var uses) ? uses.GetInt32() : 1,
            body.TryGetProperty("note", out var note) ? note.GetString() ?? "" : "", group, policy, discordId, copyLink), token => host.AuthorizeAsync(HttpContext, CommandRisk.Configuration, token), ct);
    });
    [Authorize(Policy = Policies.RequiresElevation), HttpPost("api/v1/invites/{id:guid}/{operation}")]
    public Task<IActionResult> Change(Guid id, string operation, JsonElement body, CancellationToken ct) => Run(async () =>
    {
        var caller = await host.RequireAdministratorAsync(HttpContext, ct);
        var result = await invites.ChangeAsync(caller.UserId, body.GetProperty("commandId").GetGuid(), body.GetProperty("revision").GetInt64(), id, operation,
            token => host.AuthorizeAsync(HttpContext, operation is "revoke" or "expire" ? CommandRisk.Destructive : CommandRisk.Configuration, token), ct);
        var link = result.Token is null ? null : Request.PathBase + "/AccountHub/invite#token=" + result.Token;
        return new { result = result.Result, oneTimeLink = link };
    });
    [Authorize(Policy = Policies.RequiresElevation), HttpPost("api/v1/invite-reservations/{id:guid}/reconcile")]
    public Task<IActionResult> Reconcile(Guid id, JsonElement body, CancellationToken ct) => Run(async () =>
    {
        var caller = await host.RequireAdministratorAsync(HttpContext, ct);
        return await commands.ExecuteAsync(caller.UserId, "invite.reconcile", body.GetProperty("commandId").GetGuid(), body.GetProperty("revision").GetInt64(), new { reservationId = id },
            async (_, token) => await host.AuthorizeAsync(HttpContext, CommandRisk.Configuration, token),
            async token => JsonSerializer.SerializeToElement(await invites.ReconcileAsync(id, token), Contracts.Wire.Options), ct: ct);
    });
    [AllowAnonymous, HttpPost("public/v1/invites/lookup")]
    public Task<IActionResult> Lookup(JsonElement body, CancellationToken ct) => Run(async () => { var raw = PublicToken(body); return await invites.LookupAsync(raw, ct, body.TryGetProperty("claimId", out var claim) ? claim.GetGuid() : null); });
    [AllowAnonymous, HttpPost("public/v1/invites/redeem"), HttpPost("public/v1/invites/resume")]
    public Task<IActionResult> Redeem(JsonElement body, CancellationToken ct) => Run(async () =>
    {
        var raw = PublicToken(body);
        ownership.RequireHub();
        return await ownership.RunAsync(false, async () =>
        {
        if (!await host.LocalPasswordFlowsAllowedAsync(ct)) throw new SecurityFailure("password_login_disabled", 403);
        return Request.Path.Value?.EndsWith("/resume", StringComparison.Ordinal) == true
            ? await invites.ResumeAsync(raw, body.GetProperty("claimId").GetGuid(), body.GetProperty("username").GetString()!, body.GetProperty("password").GetString()!, ct)
            : await invites.RedeemAsync(raw, body.GetProperty("claimId").GetGuid(), body.GetProperty("username").GetString()!, body.GetProperty("password").GetString()!, ct);
        }, ct);
    });
    private string PublicToken(JsonElement body)
    {
        if (Request.Headers.ContainsKey("Authorization") || Request.Headers.ContainsKey("X-Emby-Token") || Request.Query.Count != 0) throw new HubException("invalid_request", "Credentials are not accepted");
        var raw = body.GetProperty("token").GetString()!; InviteOnboardingService.ValidateToken(raw);
        if (!limiter.Allow("invite-ip:" + InviteOnboardingService.Hash(HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown"), 20) || !limiter.Allow("invite-token:" + InviteOnboardingService.Hash(raw), 10)) throw new SecurityFailure("rate_limited", 429);
        return raw;
    }
    [AllowAnonymous, HttpGet("invite-assets/{file}")]
    public IActionResult Asset(string file)
    {
        if (file is not ("invite-public.js" or "invite-admin.js")) return NotFound();
        var stream = GetType().Assembly.GetManifestResourceStream("AccountHub.Web." + file);
        return stream is null ? NotFound() : File(stream, "text/javascript");
    }
    [AllowAnonymous, HttpGet("invite")]
    public IActionResult Page()
    {
        Response.Headers.CacheControl = "no-store"; Response.Headers["Referrer-Policy"] = "no-referrer"; Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
        var path = WebUtility.HtmlEncode(Request.PathBase + "/AccountHub/");
        return Content($"<!doctype html><html lang=\"en\" class=\"theme-{WebUtility.HtmlEncode(styles.Theme)}\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><meta name=\"referrer\" content=\"no-referrer\"><base href=\"{path}\"><title>Accept invitation</title><link rel=\"stylesheet\" href=\"assets/native.css\">{styles.ThemeLink}<link rel=\"stylesheet\" href=\"../Branding/Css\"></head><body><main class=\"content-primary\"><h1>Accept invitation</h1><p id=\"invite-status\" role=\"status\" aria-live=\"polite\">Loading invitation…</p><form id=\"invite-form\" hidden><div class=\"inputContainer\"><label class=\"inputLabel\" for=\"invite-username\">Choose username</label><input is=\"emby-input\" class=\"emby-input\" id=\"invite-username\" autocomplete=\"username\" maxlength=\"64\" required></div><div class=\"inputContainer\"><label class=\"inputLabel\" for=\"invite-password\">Choose password</label><input is=\"emby-input\" class=\"emby-input\" id=\"invite-password\" type=\"password\" autocomplete=\"new-password\" maxlength=\"1024\" required></div><div class=\"inputContainer\"><label class=\"inputLabel\" for=\"invite-confirm\">Confirm password</label><input class=\"emby-input\" id=\"invite-confirm\" type=\"password\" autocomplete=\"new-password\" maxlength=\"1024\" required></div><button class=\"raised button-submit emby-button\" type=\"submit\">Create my account</button></form></main><script src=\"invite-assets/invite-public.js\" defer></script></body></html>", "text/html; charset=utf-8");
    }
    private async Task<IActionResult> Run(Func<Task<object>> action)
    {
        Response.Headers.CacheControl = "no-store"; Response.Headers["Referrer-Policy"] = "no-referrer";
        try { return new JsonResult(new { apiVersion = "1", ok = true, data = await action() }, Contracts.Wire.Options); }
        catch (SecurityFailure ex) { return Failure(ex.Code, ex.Status); }
        catch (HubException ex) { return Failure(ex.Code, ex.Code is "revision_changed" or "command_conflict" or "remediation_pending" ? 409 : ex.Retryable ? 503 : 400); }
        catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException or JsonException or InvalidOperationException) { return Failure("invalid_request", 400); }
    }
    private IActionResult Failure(string code, int status) => new JsonResult(new { apiVersion = "1", ok = false, error = new { code, message = code, retryable = status >= 500 } }, Contracts.Wire.Options) { StatusCode = status };
}
