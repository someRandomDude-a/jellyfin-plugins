using Jellyfin.Plugin.UserManager.Discord;
using Jellyfin.Plugin.UserManager.FileTransformation;
using Jellyfin.Plugin.UserManager.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.UserManager.Api;

[Authorize(Roles = "Administrator")]
[Route("UserManager/api/admin")]
public sealed class SettingsController : ApiBaseController
{
    private readonly PluginSettingsService _settings;
    private readonly DiscordWebhookClient _webhook;
    private readonly DiscordBotService _bot;
    private readonly SeerrClient _seerr;
    private readonly FileTransformationBridge _transformation;
    private readonly AuditService _audit;
    private readonly Data.PluginStore _store;

    public SettingsController(
        PluginSettingsService settings,
        DiscordWebhookClient webhook,
        DiscordBotService bot,
        SeerrClient seerr,
        FileTransformationBridge transformation,
        AuditService audit,
        Data.PluginStore store)
    {
        _settings = settings;
        _webhook = webhook;
        _bot = bot;
        _seerr = seerr;
        _transformation = transformation;
        _audit = audit;
        _store = store;
    }

    [HttpGet("settings")]
    public async Task<ActionResult<SettingsDto>> Get(CancellationToken cancellationToken)
    {
        var current = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
        return Ok(ToDto(current));
    }

    [HttpPut("settings")]
    public async Task<ActionResult<SettingsDto>> Update([FromBody] SettingsInput input, CancellationToken cancellationToken)
    {
        var updated = await _settings.ApplyAsync(new SettingsUpdate
        {
            SeerrUrl = input.SeerrUrl,
            SeerrApiKey = input.SeerrApiKey,
            DiscordBotToken = input.DiscordBotToken,
            DiscordGuildId = input.DiscordGuildId,
            DiscordAdminWebhookUrl = input.DiscordAdminWebhookUrl,
            DiscordAdminRoleIds = input.DiscordAdminRoleIds,
            PublicBaseUrl = input.PublicBaseUrl,
            InviteDefaultExpiryDays = input.InviteDefaultExpiryDays,
            InviteDefaultMaxUses = input.InviteDefaultMaxUses,
            AllowPublicInviteRequests = input.AllowPublicInviteRequests,
            NotifyAdminsOnJoin = input.NotifyAdminsOnJoin,
            NotifyAdminsOnPasswordReset = input.NotifyAdminsOnPasswordReset,
            NotifyAdminsOnInviteUsed = input.NotifyAdminsOnInviteUsed,
            SyncSeerrOnCreate = input.SyncSeerrOnCreate,
            SyncSeerrOnPresetApply = input.SyncSeerrOnPresetApply
        }, cancellationToken).ConfigureAwait(false);

        _bot.RequestRestart();

        var actor = Actor;
        await _audit.LogAsync(
            AuditService.ActionSettingsUpdated,
            actor.Id,
            actor.Name,
            null,
            null,
            "Plugin settings updated.",
            ClientIp,
            cancellationToken).ConfigureAwait(false);

        return Ok(ToDto(updated));
    }

    [HttpPost("discord/test")]
    public async Task<ActionResult> TestDiscord(CancellationToken cancellationToken)
    {
        var current = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(current.DiscordAdminWebhookUrl))
        {
            throw new UserManagerException(400, "Set an admin webhook URL first.");
        }

        var sent = await _webhook.SendAsync(
            current.DiscordAdminWebhookUrl,
            "**User Manager test notification**\nDiscord notifications are configured correctly.",
            cancellationToken).ConfigureAwait(false);

        return sent
            ? Ok(new { message = "Test notification sent." })
            : StatusCode(502, new { error = "Discord rejected the webhook request." });
    }

    [HttpPost("discord/reconnect")]
    public ActionResult ReconnectDiscord()
    {
        _bot.RequestRestart();
        return Ok(new { message = "Discord bot reconnect requested." });
    }

    [HttpPost("seerr/test")]
    public async Task<ActionResult> TestSeerr(CancellationToken cancellationToken)
    {
        try
        {
            await _seerr.TestConnectionAsync(cancellationToken).ConfigureAwait(false);
            return Ok(new { ok = true, message = "Connected to Seerr." });
        }
        catch (Exception ex)
        {
            return Ok(new { ok = false, message = ex.Message });
        }
    }

    [HttpGet("status")]
    public async Task<ActionResult<StatusDto>> Status(CancellationToken cancellationToken)
    {
        var settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
        var stats = await _store.GetStatsAsync(cancellationToken).ConfigureAwait(false);
        return Ok(new StatusDto
        {
            Version = Plugin.Instance?.Version?.ToString() ?? "unknown",
            FileTransformationRegistered = _transformation.Registered,
            FileTransformationError = _transformation.LastError,
            FileTransformationLastAttempt = _transformation.LastAttempt,
            DiscordConnected = _bot.IsConnected,
            SeerrConfigured = settings.SeerrConfigured,
            DiscordConfigured = settings.DiscordConfigured,
            PublicBaseUrl = settings.PublicBaseUrl,
            Invites = stats.Invites,
            LinkedAccounts = stats.LinkedAccounts,
            AuditEntries = stats.AuditEntries,
            Presets = stats.Presets
        });
    }

    private static SettingsDto ToDto(PluginSettings current) => new()
    {
        SeerrUrl = current.SeerrUrl,
        SeerrApiKeySet = !string.IsNullOrWhiteSpace(current.SeerrApiKey),
        DiscordGuildId = current.DiscordGuildId,
        DiscordBotTokenSet = !string.IsNullOrWhiteSpace(current.DiscordBotToken),
        DiscordAdminWebhookSet = !string.IsNullOrWhiteSpace(current.DiscordAdminWebhookUrl),
        DiscordAdminRoleIds = current.DiscordAdminRoleIds,
        PublicBaseUrl = current.PublicBaseUrl,
        InviteDefaultExpiryDays = current.InviteDefaultExpiryDays,
        InviteDefaultMaxUses = current.InviteDefaultMaxUses,
        AllowPublicInviteRequests = current.AllowPublicInviteRequests,
        NotifyAdminsOnJoin = current.NotifyAdminsOnJoin,
        NotifyAdminsOnPasswordReset = current.NotifyAdminsOnPasswordReset,
        NotifyAdminsOnInviteUsed = current.NotifyAdminsOnInviteUsed,
        SyncSeerrOnCreate = current.SyncSeerrOnCreate,
        SyncSeerrOnPresetApply = current.SyncSeerrOnPresetApply
    };
}
