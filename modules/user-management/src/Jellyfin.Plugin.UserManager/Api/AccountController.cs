using Jellyfin.Plugin.UserManager.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.UserManager.Api;

[Authorize]
[Route("UserManager/api")]
public sealed class AccountController : ApiBaseController
{
    private readonly JellyfinUserService _users;
    private readonly AccountService _accounts;
    private readonly PluginSettingsService _settings;
    private readonly AuditService _audit;
    private readonly NotificationService _notifications;

    public AccountController(
        JellyfinUserService users,
        AccountService accounts,
        PluginSettingsService settings,
        AuditService audit,
        NotificationService notifications)
    {
        _users = users;
        _accounts = accounts;
        _settings = settings;
        _audit = audit;
        _notifications = notifications;
    }

    [HttpGet("me")]
    [HttpGet("account")]
    public async Task<ActionResult<MeDto>> Me(CancellationToken cancellationToken)
    {
        var id = RequireUserId();
        var user = _users.GetUser(id) ?? throw new UserManagerException(404, "User not found.");
        var policy = _users.GetPolicy(id);
        var link = await _accounts.GetLinkAsync(id, cancellationToken).ConfigureAwait(false);
        var settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);

        return Ok(new MeDto
        {
            Id = id.ToString("N"),
            Username = user.Username,
            IsAdministrator = policy.IsAdministrator,
            IsDisabled = policy.IsDisabled,
            DiscordUserId = link?.DiscordUserId,
            DiscordUsername = link?.DiscordUsername,
            SeerrConfigured = settings.SeerrConfigured,
            DiscordConfigured = settings.DiscordConfigured,
            PublicBaseUrl = settings.PublicBaseUrl
        });
    }

    [HttpPost("account/link-code")]
    public async Task<ActionResult<LinkCodeDto>> CreateLinkCode(CancellationToken cancellationToken)
    {
        var id = RequireUserId();
        var result = await _accounts.CreateLinkCodeAsync(id, cancellationToken).ConfigureAwait(false);
        return Ok(new LinkCodeDto
        {
            Code = result.Code,
            ExpiresAt = result.ExpiresAt,
            Instructions = $"In Discord, run /link {result.Code}. The code expires in 10 minutes."
        });
    }

    [HttpDelete("account/link")]
    public async Task<ActionResult> Unlink(CancellationToken cancellationToken)
    {
        var id = RequireUserId();
        var actor = Actor;
        await _accounts.UnlinkAsync(id, actor.Id, actor.Name, ClientIp, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    [HttpPost("account/reset")]
    public async Task<ActionResult> RequestReset(CancellationToken cancellationToken)
    {
        var id = RequireUserId();
        var link = await _accounts.GetLinkAsync(id, cancellationToken).ConfigureAwait(false);
        if (link is null)
        {
            throw new UserManagerException(400, "Link your Discord account first.");
        }

        var baseUrl = await GetBaseUrlAsync(_settings, cancellationToken).ConfigureAwait(false);
        var actor = Actor;
        var reset = await _accounts.CreateResetTokenAsync(id, id, baseUrl, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var delivered = await _accounts.TrySendResetAsync(id, reset, cancellationToken).ConfigureAwait(false);

        await _audit.LogAsync(
            AuditService.ActionPasswordReset,
            actor.Id,
            actor.Name,
            id.ToString("N"),
            actor.Name,
            delivered ? "Self-service reset link delivered over Discord." : "Self-service reset link created (Discord delivery failed).",
            ClientIp,
            cancellationToken).ConfigureAwait(false);

        return Ok(new
        {
            message = delivered ? "A reset link was sent to your Discord account." : "The reset link could not be delivered. Use the link below.",
            delivered,
            resetUrl = reset.Url
        });
    }

    [HttpPost("account/password")]
    public async Task<ActionResult> ChangePassword([FromBody] ChangePasswordInput input, CancellationToken cancellationToken)
    {
        var id = RequireUserId();
        if (!await _users.VerifyPasswordAsync(id, input.CurrentPassword, cancellationToken).ConfigureAwait(false))
        {
            throw new UserManagerException(400, "The current password is incorrect.");
        }

        await _users.ChangePasswordAsync(id, input.NewPassword, cancellationToken).ConfigureAwait(false);
        var actor = Actor;
        await _audit.LogAsync(
            AuditService.ActionPasswordChanged,
            actor.Id,
            actor.Name,
            id.ToString("N"),
            actor.Name,
            "Password changed from the account page.",
            ClientIp,
            cancellationToken).ConfigureAwait(false);

        var settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
        if (settings.NotifyAdminsOnPasswordReset)
        {
            await _notifications.NotifyAdminsAsync(
                "Password changed",
                $"{actor.Name} changed their password from the account page.",
                cancellationToken).ConfigureAwait(false);
        }

        return Ok(new { message = "Password updated." });
    }

    [HttpGet("account/audit")]
    public async Task<ActionResult<IReadOnlyList<AuditEntryDto>>> MyAudit(
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var id = RequireUserId();
        var entries = await _audit.RecentAsync(Math.Clamp(limit, 1, 200), 0, null, id.ToString("N"), cancellationToken)
            .ConfigureAwait(false);
        return Ok(entries.Select(ApiMapping.ToDto).ToList());
    }
}
