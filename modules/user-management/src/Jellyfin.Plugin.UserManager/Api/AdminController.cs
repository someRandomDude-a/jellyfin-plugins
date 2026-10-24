using System.Text.Json;
using Jellyfin.Plugin.UserManager.Data;
using Jellyfin.Plugin.UserManager.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.UserManager.Api;

[Authorize(Roles = "Administrator")]
[Route("UserManager/api/admin")]
public sealed class AdminController : ApiBaseController
{
    private readonly JellyfinUserService _users;
    private readonly PluginStore _store;
    private readonly PresetService _presets;
    private readonly InviteService _invites;
    private readonly AccountService _accounts;
    private readonly SeerrClient _seerr;
    private readonly PluginSettingsService _settings;
    private readonly AuditService _audit;
    private readonly NotificationService _notifications;
    private readonly ILogger<AdminController> _logger;

    public AdminController(
        JellyfinUserService users,
        PluginStore store,
        PresetService presets,
        InviteService invites,
        AccountService accounts,
        SeerrClient seerr,
        PluginSettingsService settings,
        AuditService audit,
        NotificationService notifications,
        ILogger<AdminController> logger)
    {
        _users = users;
        _store = store;
        _presets = presets;
        _invites = invites;
        _accounts = accounts;
        _seerr = seerr;
        _settings = settings;
        _audit = audit;
        _notifications = notifications;
        _logger = logger;
    }

    [HttpGet("users")]
    public async Task<ActionResult<UsersResponse>> GetUsers(CancellationToken cancellationToken)
    {
        var response = new UsersResponse();
        var settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
        var links = (await _store.GetLinksAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(l => l.JellyfinUserId, StringComparer.OrdinalIgnoreCase);
        var presetIds = await _store.GetUserPresetsAsync(cancellationToken).ConfigureAwait(false);
        var presets = (await _presets.GetAllAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);

        Dictionary<string, SeerrUser> seerrUsers = new(StringComparer.OrdinalIgnoreCase);
        if (settings.SeerrConfigured)
        {
            try
            {
                foreach (var seerrUser in await _seerr.GetUsersAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
                {
                    if (!string.IsNullOrWhiteSpace(seerrUser.JellyfinUserId))
                    {
                        seerrUsers[NormalizeId(seerrUser.JellyfinUserId)] = seerrUser;
                    }
                }
            }
            catch (Exception ex)
            {
                response.SeerrError = ex.Message;
                _logger.LogWarning(ex, "Could not load Seerr users");
            }
        }

        foreach (var user in _users.GetUsers())
        {
            var id = user.Id.ToString("N");
            links.TryGetValue(id, out var link);
            seerrUsers.TryGetValue(id, out var seerrUser);
            presetIds.TryGetValue(id, out var presetId);
            var presetName = presetId is not null && presets.TryGetValue(presetId, out var preset) ? preset.Name : null;
            response.Users.Add(ApiMapping.ToSummary(_users.GetDto(user.Id), link, seerrUser, presetId, presetName));
        }

        return Ok(response);
    }

    [HttpGet("users/{id:guid}")]
    public async Task<ActionResult<UserDetailDto>> GetUser(Guid id, CancellationToken cancellationToken)
    {
        var user = _users.GetUser(id) ?? throw new UserManagerException(404, "User not found.");
        var link = await _accounts.GetLinkAsync(id, cancellationToken).ConfigureAwait(false);
        var presetIds = await _store.GetUserPresetsAsync(cancellationToken).ConfigureAwait(false);
        presetIds.TryGetValue(id.ToString("N"), out var presetId);
        var presets = await _presets.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var presetName = presets.FirstOrDefault(p => p.Id == presetId)?.Name;

        SeerrUser? seerrUser = null;
        var settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
        if (settings.SeerrConfigured)
        {
            try
            {
                seerrUser = await _seerr.GetUserByJellyfinIdAsync(id, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not load Seerr user {UserId}", id);
            }
        }

        var summary = ApiMapping.ToSummary(_users.GetDto(id), link, seerrUser, presetId, presetName);
        return Ok(new UserDetailDto
        {
            Summary = summary,
            Policy = JsonSerializer.SerializeToElement(_users.GetPolicy(id), PolicyTools.JsonOptions),
            SeerrPermissionNames = seerrUser is null ? [] : SeerrPermissions.Describe(seerrUser.Permissions).ToList()
        });
    }

    [HttpPost("users")]
    public async Task<ActionResult<UserSummaryDto>> CreateUser([FromBody] CreateUserInput input, CancellationToken cancellationToken)
    {
        var preset = string.IsNullOrWhiteSpace(input.PresetId)
            ? await _presets.GetDefaultAsync(cancellationToken).ConfigureAwait(false)
            : await _presets.GetAsync(input.PresetId, cancellationToken).ConfigureAwait(false)
              ?? throw new UserManagerException(404, "Preset not found.");

        var policy = PolicyTools.Deserialize(preset.JellyfinPolicyJson);
        var user = await _users.CreateUserAsync(
            input.Username,
            input.Password,
            policy,
            input.IsAdministrator,
            DiscordPasswordResetProvider.ProviderId,
            cancellationToken).ConfigureAwait(false);

        await _store.SetUserPresetAsync(user.Id.ToString("N"), preset.Id, cancellationToken).ConfigureAwait(false);

        string? seerrError = null;
        var settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
        if (input.SyncSeerr && settings.SyncSeerrOnCreate && settings.SeerrConfigured)
        {
            try
            {
                await _seerr.SyncUserAsync(user.Id, preset.SeerrPermissions, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                seerrError = ex.Message;
                _logger.LogWarning(ex, "Could not sync new user {UserId} to Seerr", user.Id);
            }
        }

        var actor = Actor;
        await _audit.LogAsync(
            AuditService.ActionUserCreated,
            actor.Id,
            actor.Name,
            user.Id.ToString("N"),
            user.Username,
            $"Created with preset '{preset.Name}'. Administrator: {input.IsAdministrator}. Seerr: {seerrError ?? "ok"}.",
            ClientIp,
            cancellationToken).ConfigureAwait(false);

        if (settings.NotifyAdminsOnJoin)
        {
            await _notifications.NotifyAdminsAsync(
                "User created",
                $"{user.Username} was created by {actor.Name} with preset {preset.Name}.",
                cancellationToken).ConfigureAwait(false);
        }

        var link = await _accounts.GetLinkAsync(user.Id, cancellationToken).ConfigureAwait(false);
        return Ok(ApiMapping.ToSummary(_users.GetDto(user.Id), link, null, preset.Id, preset.Name));
    }

    [HttpPut("users/{id:guid}")]
    public async Task<ActionResult<UserSummaryDto>> UpdateUser(
        Guid id,
        [FromBody] UpdateUserInput input,
        CancellationToken cancellationToken)
    {
        var user = _users.GetUser(id) ?? throw new UserManagerException(404, "User not found.");
        var actor = Actor;

        if (id == RequireUserId() && (input.IsAdministrator == false || input.IsDisabled == true))
        {
            throw new UserManagerException(400, "You cannot disable or demote your own account.");
        }

        if (input.IsAdministrator == false && _users.IsAdministrator(id) && _users.CountAdministrators() <= 1)
        {
            throw new UserManagerException(400, "The last administrator cannot be demoted.");
        }

        if (input.IsDisabled == true && _users.IsAdministrator(id) && _users.CountAdministrators() <= 1)
        {
            throw new UserManagerException(400, "The last administrator cannot be disabled.");
        }

        var changes = new List<string>();
        if (!string.IsNullOrWhiteSpace(input.PresetId))
        {
            var result = await _presets.ApplyToUserAsync(
                id,
                input.PresetId,
                actor.Id,
                actor.Name,
                ClientIp,
                cancellationToken).ConfigureAwait(false);
            changes.Add($"preset {input.PresetId}");
            if (result.SeerrError is not null)
            {
                changes.Add($"Seerr error: {result.SeerrError}");
            }
        }

        if (input.IsAdministrator is bool isAdmin && isAdmin != _users.IsAdministrator(id))
        {
            await _users.SetAdministratorAsync(id, isAdmin, cancellationToken).ConfigureAwait(false);
            changes.Add($"administrator={isAdmin}");
        }

        if (input.IsDisabled is bool isDisabled && isDisabled != _users.IsDisabled(id))
        {
            await _users.SetDisabledAsync(id, isDisabled, cancellationToken: cancellationToken).ConfigureAwait(false);
            changes.Add($"disabled={isDisabled}");
        }

        await _audit.LogAsync(
            AuditService.ActionUserUpdated,
            actor.Id,
            actor.Name,
            id.ToString("N"),
            user.Username,
            changes.Count == 0 ? "No changes." : string.Join("; ", changes),
            ClientIp,
            cancellationToken).ConfigureAwait(false);

        var link = await _accounts.GetLinkAsync(id, cancellationToken).ConfigureAwait(false);
        var presetMap = await _store.GetUserPresetsAsync(cancellationToken).ConfigureAwait(false);
        presetMap.TryGetValue(id.ToString("N"), out var presetId);
        var presetName = (await _presets.GetAllAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(p => p.Id == presetId)?.Name;

        SeerrUser? seerrUser = null;
        var settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
        if (settings.SeerrConfigured)
        {
            try
            {
                seerrUser = await _seerr.GetUserByJellyfinIdAsync(id, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not refresh Seerr data for {UserId}", id);
            }
        }

        return Ok(ApiMapping.ToSummary(_users.GetDto(id), link, seerrUser, presetId, presetName));
    }

    [HttpDelete("users/{id:guid}")]
    public async Task<ActionResult> DeleteUser(Guid id, CancellationToken cancellationToken)
    {
        var user = _users.GetUser(id) ?? throw new UserManagerException(404, "User not found.");
        var actor = Actor;

        if (id == RequireUserId())
        {
            throw new UserManagerException(400, "You cannot delete your own account.");
        }

        if (_users.IsAdministrator(id) && _users.CountAdministrators() <= 1)
        {
            throw new UserManagerException(400, "The last administrator cannot be deleted.");
        }

        var settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
        if (settings.SeerrConfigured)
        {
            await _seerr.DeleteUserAsync(id, cancellationToken).ConfigureAwait(false);
        }

        await _store.DeleteLinkAsync(id.ToString("N"), cancellationToken).ConfigureAwait(false);
        await _store.SetUserPresetAsync(id.ToString("N"), null, cancellationToken).ConfigureAwait(false);
        await _users.DeleteUserAsync(id, cancellationToken).ConfigureAwait(false);

        await _audit.LogAsync(
            AuditService.ActionUserDeleted,
            actor.Id,
            actor.Name,
            id.ToString("N"),
            user.Username,
            "User deleted from Jellyfin and Seerr.",
            ClientIp,
            cancellationToken).ConfigureAwait(false);

        return NoContent();
    }

    [HttpPost("users/{id:guid}/reset-password")]
    public async Task<ActionResult> ResetPassword(
        Guid id,
        [FromBody] ResetPasswordInput input,
        CancellationToken cancellationToken)
    {
        var user = _users.GetUser(id) ?? throw new UserManagerException(404, "User not found.");
        var actor = Actor;

        if (!string.IsNullOrWhiteSpace(input.NewPassword))
        {
            await _users.ChangePasswordAsync(id, input.NewPassword, cancellationToken).ConfigureAwait(false);
            await _audit.LogAsync(
                AuditService.ActionPasswordReset,
                actor.Id,
                actor.Name,
                id.ToString("N"),
                user.Username,
                "Password set by an administrator.",
                ClientIp,
                cancellationToken).ConfigureAwait(false);
            return Ok(new { message = "Password updated." });
        }

        var link = await _accounts.GetLinkAsync(id, cancellationToken).ConfigureAwait(false);
        if (link is null)
        {
            throw new UserManagerException(400, "This user has not linked Discord. Set a password directly or ask them to link first.");
        }

        var baseUrl = await GetBaseUrlAsync(_settings, cancellationToken).ConfigureAwait(false);
        var reset = await _accounts.CreateResetTokenAsync(id, RequireUserId(), baseUrl, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var delivered = input.SendDiscordLink;
        if (delivered)
        {
            delivered = await _accounts.TrySendResetAsync(id, reset, cancellationToken).ConfigureAwait(false);
        }

        await _audit.LogAsync(
            AuditService.ActionPasswordReset,
            actor.Id,
            actor.Name,
            id.ToString("N"),
            user.Username,
            delivered ? "Reset link delivered over Discord." : "Reset link created (Discord delivery failed).",
            ClientIp,
            cancellationToken).ConfigureAwait(false);

        return Ok(new { message = delivered ? "Reset link sent over Discord." : "Reset link created.", delivered, resetUrl = reset.Url });
    }

    [HttpPost("users/{id:guid}/link-code")]
    public async Task<ActionResult<LinkCodeDto>> CreateLinkCode(Guid id, CancellationToken cancellationToken)
    {
        _ = _users.GetUser(id) ?? throw new UserManagerException(404, "User not found.");
        var code = await _accounts.CreateLinkCodeAsync(id, cancellationToken).ConfigureAwait(false);
        return Ok(new LinkCodeDto
        {
            Code = code.Code,
            ExpiresAt = code.ExpiresAt,
            Instructions = $"Ask the user to run /link {code.Code} in Discord."
        });
    }

    [HttpPost("seerr/sync-all")]
    public async Task<ActionResult> SyncAll(CancellationToken cancellationToken)
    {
        var settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
        if (!settings.SeerrConfigured)
        {
            throw new UserManagerException(400, "Seerr is not configured.");
        }

        var presetMap = await _store.GetUserPresetsAsync(cancellationToken).ConfigureAwait(false);
        var presets = (await _presets.GetAllAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
        var synced = 0;
        var failed = new List<string>();

        foreach (var user in _users.GetUsers())
        {
            var permissions = 0L;
            if (presetMap.TryGetValue(user.Id.ToString("N"), out var presetId) && presets.TryGetValue(presetId, out var preset))
            {
                permissions = preset.SeerrPermissions;
            }

            try
            {
                await _seerr.SyncUserAsync(user.Id, permissions, cancellationToken).ConfigureAwait(false);
                synced++;
            }
            catch (Exception ex)
            {
                failed.Add($"{user.Username}: {ex.Message}");
            }
        }

        var actor = Actor;
        await _audit.LogAsync(
            AuditService.ActionSeerrSync,
            actor.Id,
            actor.Name,
            null,
            null,
            $"Synced {synced} user(s) to Seerr; {failed.Count} failed.",
            ClientIp,
            cancellationToken).ConfigureAwait(false);

        return Ok(new { synced, failed });
    }

    private static string NormalizeId(string value) =>
        Guid.TryParse(value, out var id) ? id.ToString("N") : value;
}
