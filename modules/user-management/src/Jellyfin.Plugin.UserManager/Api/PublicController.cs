using Jellyfin.Plugin.UserManager.Security;
using Jellyfin.Plugin.UserManager.Services;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.UserManager.Api;

[AllowAnonymous]
[Route("UserManager/api")]
public sealed class PublicController : ApiBaseController
{
    private readonly PluginSettingsService _settings;
    private readonly InviteService _invites;
    private readonly AccountService _accounts;
    private readonly AuditService _audit;
    private readonly NotificationService _notifications;
    private readonly RateLimiter _rateLimiter;
    private readonly Data.PluginStore _store;
    private readonly IServerApplicationHost _applicationHost;

    public PublicController(
        PluginSettingsService settings,
        InviteService invites,
        AccountService accounts,
        AuditService audit,
        NotificationService notifications,
        RateLimiter rateLimiter,
        Data.PluginStore store,
        IServerApplicationHost applicationHost)
    {
        _settings = settings;
        _invites = invites;
        _accounts = accounts;
        _audit = audit;
        _notifications = notifications;
        _rateLimiter = rateLimiter;
        _store = store;
        _applicationHost = applicationHost;
    }

    [HttpGet("public/config")]
    public async Task<ActionResult> Config(CancellationToken cancellationToken)
    {
        var settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
        return Ok(new
        {
            serverName = _applicationHost.FriendlyName,
            version = Plugin.Instance?.Version?.ToString() ?? "unknown",
            inviteRequestsEnabled = settings.AllowPublicInviteRequests,
            discordResetsEnabled = settings.DiscordConfigured
        });
    }

    [HttpGet("invite/{token}/info")]
    public async Task<ActionResult<PublicInviteInfoDto>> InviteInfo(string token, CancellationToken cancellationToken)
    {
        var info = await _invites.GetInfoAsync(token, cancellationToken).ConfigureAwait(false);
        return Ok(new PublicInviteInfoDto
        {
            Valid = info.Valid,
            Reason = info.Reason,
            PresetName = info.PresetName,
            ExpiresAt = info.ExpiresAt,
            RemainingUses = info.RemainingUses
        });
    }

    [HttpPost("public/invites/lookup")]
    public Task<ActionResult<PublicInviteInfoDto>> LookupInvite([FromBody] ValidateResetInput input, CancellationToken cancellationToken) =>
        InviteInfo(input.Token, cancellationToken);

    [HttpPost("public/invites/register")]
    public Task<ActionResult<RegisterResultDto>> RegisterInvite([FromBody] RegisterInput input, CancellationToken cancellationToken) =>
        Register(input.Token, input, cancellationToken);

    [HttpPost("invite/{token}/register")]
    public async Task<ActionResult<RegisterResultDto>> Register(
        string token,
        [FromBody] RegisterInput input,
        CancellationToken cancellationToken)
    {
        if (!_rateLimiter.IsAllowed($"register:{ClientIp}", 8, TimeSpan.FromHours(1)))
        {
            throw new UserManagerException(429, "Too many attempts. Try again later.");
        }

        var baseUrl = await GetBaseUrlAsync(_settings, cancellationToken).ConfigureAwait(false);
        var result = await _invites.RegisterAsync(
            token,
            input.Username,
            input.Password,
            ClientIp,
            baseUrl,
            cancellationToken).ConfigureAwait(false);

        return Ok(new RegisterResultDto
        {
            UserId = result.UserId,
            Username = result.Username,
            LoginUrl = result.LoginUrl,
            SeerrSynced = result.SeerrSynced,
            SeerrWarning = result.SeerrWarning
        });
    }

    [HttpPost("public/reset/request")]
    public async Task<ActionResult> RequestReset([FromBody] ForgotPasswordInput input, CancellationToken cancellationToken)
    {
        if (!_rateLimiter.IsAllowed($"reset-request:{ClientIp}", 5, TimeSpan.FromHours(1)))
        {
            throw new UserManagerException(429, "Too many attempts. Try again later.");
        }

        var username = (input.Username ?? string.Empty).Trim();
        if (username.Length is > 0 and <= 64)
        {
            var baseUrl = await GetBaseUrlAsync(_settings, cancellationToken).ConfigureAwait(false);
            await _accounts.RequestResetAsync(username, baseUrl, ClientIp, cancellationToken).ConfigureAwait(false);
        }

        return Ok(new
        {
            message = "If that account exists and has Discord linked, a reset link has been sent. Check your Discord direct messages."
        });
    }

    [HttpPost("public/reset/validate")]
    public async Task<ActionResult> ValidateReset([FromBody] ValidateResetInput input, CancellationToken cancellationToken)
    {
        var record = await _store.GetResetTokenAsync(TokenTools.HashToken(input.Token ?? string.Empty), cancellationToken)
            .ConfigureAwait(false);
        var valid = record is not null
            && record.UsedAt is null
            && record.ExpiresAt > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        return Ok(new
        {
            valid,
            expiresAt = record is null ? (DateTimeOffset?)null : DateTimeOffset.FromUnixTimeMilliseconds(record.ExpiresAt)
        });
    }

    [HttpPost("public/reset/complete")]
    public async Task<ActionResult> CompleteReset([FromBody] ResetWithTokenInput input, CancellationToken cancellationToken)
    {
        if (!_rateLimiter.IsAllowed($"reset-complete:{ClientIp}", 10, TimeSpan.FromHours(1)))
        {
            throw new UserManagerException(429, "Too many attempts. Try again later.");
        }

        var (_, username) = await _accounts.ResetWithTokenAsync(input.Token, input.NewPassword, ClientIp, cancellationToken)
            .ConfigureAwait(false);
        var baseUrl = await GetBaseUrlAsync(_settings, cancellationToken).ConfigureAwait(false);
        return Ok(new
        {
            message = "Password updated. Sign in with your new password.",
            username,
            loginUrl = $"{baseUrl.TrimEnd('/')}/web/index.html#/login"
        });
    }

    [HttpPost("public/invite-request")]
    public async Task<ActionResult> InviteRequest([FromBody] InviteRequestInput input, CancellationToken cancellationToken)
    {
        var settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
        if (!settings.AllowPublicInviteRequests)
        {
            throw new UserManagerException(403, "Invite requests are disabled.");
        }

        if (!_rateLimiter.IsAllowed($"invite-request:{ClientIp}", 3, TimeSpan.FromHours(1)))
        {
            throw new UserManagerException(429, "Too many attempts. Try again later.");
        }

        var name = (input.Name ?? string.Empty).Trim();
        var contact = (input.Contact ?? string.Empty).Trim();
        if (name.Length is < 1 or > 64 || contact.Length is < 1 or > 128)
        {
            throw new UserManagerException(400, "Enter your name and a way to contact you.");
        }

        await _audit.LogAsync(
            "invite.requested",
            "public",
            name,
            null,
            null,
            $"Contact: {contact}\nMessage: {Truncate(input.Message, 500)}",
            ClientIp,
            cancellationToken).ConfigureAwait(false);

        await _notifications.NotifyAdminsAsync(
            "Invite requested",
            $"{name} requested an invite.\nContact: {contact}\n{Truncate(input.Message, 300)}",
            cancellationToken).ConfigureAwait(false);

        return Ok(new { message = "Request sent. An administrator will contact you." });
    }

    [HttpGet("public/health")]
    public ActionResult Health() => Ok(new { ok = true, version = Plugin.Instance?.Version?.ToString() ?? "unknown" });

    private static string Truncate(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Length <= max ? value : value[..max];
}
