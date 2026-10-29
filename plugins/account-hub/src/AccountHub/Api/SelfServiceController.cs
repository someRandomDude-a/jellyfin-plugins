using System.Text.Json;
using AccountHub.Core;
using AccountHub.Notifications;
using AccountHub.Security;
using Jellyfin.Plugin.UserManager.Data;
using Jellyfin.Plugin.UserManager.Services;
using DiscordBridge.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AccountHub.Api;

[ApiController, Route("AccountHub"), RequestSizeLimit(16384)]
public sealed class SelfServiceController(HubHost host, DiscordLinkService links, AccountService accounts, PluginStore lifecycle, JellyfinUserService users, IngressLimiter limiter,DiscordBridgeClient bridge) : ControllerBase
{
    [Authorize, HttpGet("api/v1/self")]
    public async Task<IActionResult> Self(CancellationToken ct)
    {
        var id = await host.CurrentUserAsync(HttpContext, ct);
        if(id is null)return Error("unauthorized",401);
        var link=await lifecycle.GetLinkByJellyfinUserIdAsync(id.Value.ToString("N"),ct);
        return OkJson(new { userId=id, username=users.GetUser(id.Value)?.Username, discordLinked=link is not null, discordId=link?.DiscordUserId,discordUsername=link is null?null:DiscordIdentityLabel.Username(link.DiscordUsername) });
    }
    [Authorize, HttpPost("api/v1/discord/link/lookup")]
    public Task<IActionResult> LookupLink(JsonElement body, CancellationToken ct) => Guard(async () =>
    {
        var actor = await host.CurrentUserAsync(HttpContext, ct);
        if (actor is null) return Error("unauthorized", 401);
        if (!limiter.Allow("link-lookup:" + actor.Value, 20)) return Error("rate_limited", 429);
        var token = Token(body); var challenge = await links.LookupAsync(token, ct);
        if(challenge is null)return Error("link_expired",400);
        var username=await bridge.UsernameAsync(challenge.DiscordId,ct);
        return OkJson(new { userId=actor.Value, discordId=challenge.DiscordId,discordUsername=username,jellyfinUsername=users.GetUser(actor.Value)?.Username, expiresAt=DateTimeOffset.FromUnixTimeMilliseconds(challenge.ExpiresAtUnixMs) });
    });
    [Authorize, HttpPost("api/v1/discord/link/confirm")]
    public Task<IActionResult> Confirm(JsonElement body, CancellationToken ct) => Guard(async () =>
    {
        var actor = await host.CurrentUserAsync(HttpContext, ct);
        if (actor is null) return Error("unauthorized", 401);
        if (body.GetProperty("confirmation").GetString() != "LINK MY DISCORD ACCOUNT") return Error("confirmation_required", 400);
        if(!body.TryGetProperty("userId",out var reviewed)||reviewed.ValueKind!=JsonValueKind.String||!Guid.TryParse(reviewed.GetString(),out var reviewedId)||reviewedId!=actor.Value)return Error("account_changed",409);
        if (!bridge.Permitted(actor.Value, "link")) return Error("forbidden", 403);
        var token = Token(body);
        if (!limiter.Allow("link-confirm:" + actor.Value, 10)) return Error("rate_limited", 429);
        var challenge=await links.LookupAsync(token,ct);if(challenge is null)return Error("link_expired",400);
        var username=await bridge.UsernameAsync(challenge.DiscordId,ct);
        await links.ConfirmAsync(actor.Value, token, ct,username);
        return OkJson(new { status = "linked" });
    });
    [AllowAnonymous, HttpPost("public/v1/resets/lookup")]
    public Task<IActionResult> LookupReset(JsonElement body, CancellationToken ct) => Guard(async () =>
    {
        CheckAnonymous(); var token = Token(body); Limit(token);
        var record = await lifecycle.GetResetTokenAsync(DiscordLinkService.Hash(token), ct);
        var valid = record is not null && record.UsedAt is null && record.ExpiresAt > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() && Guid.TryParse(record.JellyfinUserId, out var id) && await host.UserExistsAsync(id, ct);
        return OkJson(new { valid, verifiedSessionRequired = true, verificationAvailable = host.SecurityCompatible, expiresAt = valid ? DateTimeOffset.FromUnixTimeMilliseconds(record!.ExpiresAt) : (DateTimeOffset?)null });
    });
    [Authorize, HttpPost("public/v1/resets/session")]
    public Task<IActionResult> ResetSession(JsonElement body, CancellationToken ct) => Guard(async () =>
    {
        if (Request.Query.Count != 0) throw new ArgumentException();
        var token = Token(body); Limit(token);
        var record = await lifecycle.GetResetTokenAsync(DiscordLinkService.Hash(token), ct);
        if (record is null || record.UsedAt is not null || record.ExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() || !Guid.TryParse(record.JellyfinUserId, out var id)) return Error("invalid_or_expired", 400);
        await host.RequireResetSessionAsync(HttpContext, id, ct);
        return OkJson(new { ready = true });
    });
    [Authorize, HttpPost("public/v1/resets/complete")]
    public Task<IActionResult> CompleteReset(JsonElement body, CancellationToken ct) => Guard(async () =>
    {
        if (Request.Query.Count != 0) throw new ArgumentException();
        var token = Token(body); Limit(token);
        if (!await host.LocalPasswordFlowsAllowedAsync(ct)) return Error("password_login_disabled", 403);
        var password = body.GetProperty("password").GetString()!;
        if (password.Length > 1024) return Error("invalid_request", 400);
        await accounts.ResetWithTokenAsync(token, password, null, (id, cancellation) => host.RequireResetSessionAsync(HttpContext, id, cancellation), ct);
        return OkJson(new { status = "completed", message = "Password updated. Sign in again." });
    });
    private static string Token(JsonElement body)
    {
        var token = body.GetProperty("token").GetString()!;
        if (token.Length != 43 || token.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))) throw new ArgumentException();
        return token;
    }
    private void CheckAnonymous()
    {
        if (Request.Headers.ContainsKey("Authorization") || Request.Headers.ContainsKey("X-Emby-Token") || Request.Query.Count != 0) throw new ArgumentException();
    }
    private void Limit(string token)
    {
        var address = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (!limiter.Allow("recovery-ip:" + DiscordLinkService.Hash(address), 20) || !limiter.Allow("recovery-token:" + DiscordLinkService.Hash(token), 10)) throw new UserManagerException(429, "rate_limited");
    }
    private async Task<IActionResult> Guard(Func<Task<IActionResult>> action)
    {
        Response.Headers.CacheControl = "no-store";
        try { return await action(); }
        catch (SecurityFailure ex) { return Error(ex.Code, ex.Status); }
        catch (UserManagerException ex) { return Error(ex.StatusCode == 409 ? "remediation_pending" : "invalid_or_expired", ex.StatusCode); }
        catch (HubException ex) { return Error(ex.Code, ex.Retryable ? 503 : 400); }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or ArgumentException) { return Error("invalid_request", 400); }
    }
    private IActionResult OkJson(object data) => new JsonResult(new { apiVersion = "1", ok = true, data }, Contracts.Wire.Options);
    private IActionResult Error(string code, int status) => new JsonResult(new { apiVersion = "1", ok = false, error = new { code, message = code, retryable = status >= 500 } }, Contracts.Wire.Options) { StatusCode = status };
}
