using Jellyfin.Plugin.UserManager.Data;
using Jellyfin.Plugin.UserManager.Security;

namespace Jellyfin.Plugin.UserManager.Services;

public sealed record InviteInfo(
    bool Valid,
    string? Reason,
    string? PresetId,
    string? PresetName,
    DateTimeOffset? ExpiresAt,
    int RemainingUses);

public sealed record InviteRegistrationResult(
    Guid UserId,
    string Username,
    string LoginUrl,
    bool SeerrSynced,
    string? SeerrWarning);

public sealed class InviteService
{
    private readonly PluginStore _store;
    private readonly PluginSettingsService _settings;
    private readonly PresetService _presets;
    private readonly JellyfinUserService _users;
    private readonly SeerrClient _seerr;
    private readonly AuditService _audit;
    private readonly NotificationService _notifications;
    private readonly ILogger<InviteService> _logger;

    public InviteService(
        PluginStore store,
        PluginSettingsService settings,
        PresetService presets,
        JellyfinUserService users,
        SeerrClient seerr,
        AuditService audit,
        NotificationService notifications,
        ILogger<InviteService> logger)
    {
        _store = store;
        _settings = settings;
        _presets = presets;
        _users = users;
        _seerr = seerr;
        _audit = audit;
        _notifications = notifications;
        _logger = logger;
    }

    public async Task<(InviteRecord Invite, string Url)> CreateAsync(
        string actorId,
        string actorName,
        string? presetId,
        int? maxUses,
        int? expiresInDays,
        string? note,
        string baseUrl,
        CancellationToken cancellationToken = default)
    {
        var settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
        var preset = string.IsNullOrWhiteSpace(presetId)
            ? await _presets.GetDefaultAsync(cancellationToken).ConfigureAwait(false)
            : await _presets.GetAsync(presetId, cancellationToken).ConfigureAwait(false)
              ?? throw new UserManagerException(404, "Preset not found.");

        var uses = Math.Clamp(maxUses ?? settings.InviteDefaultMaxUses, 1, 10000);
        var days = Math.Clamp(expiresInDays ?? settings.InviteDefaultExpiryDays, 1, 365);
        var now = DateTimeOffset.UtcNow;
        var token = TokenTools.CreateUrlToken();

        var invite = new InviteRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            TokenHash = TokenTools.HashToken(token),
            CreatedByUserId = actorId,
            CreatedByName = actorName,
            PresetId = preset.Id,
            PresetName = preset.Name,
            CreatedAt = now.ToUnixTimeMilliseconds(),
            ExpiresAt = now.AddDays(days).ToUnixTimeMilliseconds(),
            MaxUses = uses,
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            Uses = []
        };

        await _store.CreateInviteAsync(invite, cancellationToken).ConfigureAwait(false);
        await _audit.LogAsync(
            AuditService.ActionInviteCreated,
            actorId,
            actorName,
            null,
            null,
            $"Invite for preset '{preset.Name}', {uses} use(s), {days} day(s).",
            null,
            cancellationToken).ConfigureAwait(false);

        return (invite, BuildInviteUrl(baseUrl, token));
    }

    public async Task<IReadOnlyList<InviteRecord>> ListAsync(CancellationToken cancellationToken = default) =>
        await _store.GetInvitesAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

    public async Task<InviteInfo> GetInfoAsync(string token, CancellationToken cancellationToken = default)
    {
        var invite = await FindAsync(token, cancellationToken).ConfigureAwait(false);
        if (invite is null)
        {
            return new InviteInfo(false, "This invite link is not valid.", null, null, null, 0);
        }

        var reason = invite.Revoked
            ? "This invite has been revoked."
            : invite.IsExpired
                ? "This invite has expired."
                : invite.IsExhausted
                    ? "This invite has already been used."
                    : null;

        return new InviteInfo(
            reason is null,
            reason,
            invite.PresetId,
            invite.PresetName,
            DateTimeOffset.FromUnixTimeMilliseconds(invite.ExpiresAt),
            Math.Max(0, invite.MaxUses - invite.UseCount));
    }

    public async Task<InviteRegistrationResult> RegisterAsync(
        string token,
        string username,
        string password,
        string remoteIp,
        string baseUrl,
        CancellationToken cancellationToken = default)
    {
        var invite = await FindAsync(token, cancellationToken).ConfigureAwait(false);
        if (invite is null || invite.Revoked || invite.IsExpired || invite.IsExhausted)
        {
            throw new UserManagerException(400, "This invite link is no longer valid.");
        }

        var reserved = await _store.TryReserveInviteAsync(invite.TokenHash, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), cancellationToken)
            .ConfigureAwait(false);
        if (!reserved)
        {
            throw new UserManagerException(400, "This invite link is no longer valid.");
        }

        Data.PresetRecord? preset = null;
        if (!string.IsNullOrWhiteSpace(invite.PresetId))
        {
            preset = await _presets.GetAsync(invite.PresetId, cancellationToken).ConfigureAwait(false);
        }

        preset ??= await _presets.GetDefaultAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var policy = PolicyTools.Deserialize(preset.JellyfinPolicyJson);
            var user = await _users.CreateUserAsync(
                username,
                password,
                policy,
                isAdministrator: false,
                passwordResetProviderId: DiscordPasswordResetProvider.ProviderId,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            await _store.SetUserPresetAsync(user.Id.ToString("N"), preset.Id, cancellationToken).ConfigureAwait(false);

            try
            {
                await _store.AddInviteUseAsync(new InviteUseRecord
                {
                    Id = Guid.NewGuid().ToString("N"),
                    InviteId = invite.Id,
                    UserId = user.Id.ToString("N"),
                    Username = user.Username,
                    UsedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    RemoteIp = remoteIp
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // The account is valid; do not fail the signup because monitoring failed.
                _logger.LogWarning(ex, "Could not record invite use for {InviteId}", invite.Id);
            }

            var seerrSynced = false;
            string? seerrWarning = null;
            var settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
            if (settings.SyncSeerrOnCreate && settings.SeerrConfigured)
            {
                try
                {
                    await _seerr.SyncUserAsync(user.Id, preset.SeerrPermissions, cancellationToken).ConfigureAwait(false);
                    seerrSynced = true;
                }
                catch (Exception ex)
                {
                    seerrWarning = ex.Message;
                    _logger.LogWarning(ex, "Could not sync new user {UserId} to Seerr", user.Id);
                }
            }

            await _audit.LogAsync(
                AuditService.ActionInviteUsed,
                "invite",
                invite.CreatedByName,
                user.Id.ToString("N"),
                user.Username,
                $"Invite {invite.Id} used. Preset '{preset.Name}'. Seerr synced: {seerrSynced}.",
                remoteIp,
                cancellationToken).ConfigureAwait(false);

            if (settings.NotifyAdminsOnInviteUsed)
            {
                await _notifications.NotifyAdminsAsync(
                    "Invite used",
                    $"{user.Username} created an account with an invite ({preset.Name}).",
                    cancellationToken).ConfigureAwait(false);
            }

            return new InviteRegistrationResult(
                user.Id,
                user.Username,
                $"{baseUrl.TrimEnd('/')}/web/index.html#/login",
                seerrSynced,
                seerrWarning);
        }
        catch
        {
            await _store.ReleaseInviteAsync(invite.TokenHash, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public async Task RevokeAsync(string id, string actorId, string actorName, string? ipAddress, CancellationToken cancellationToken = default)
    {
        var invite = await _store.GetInviteAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new UserManagerException(404, "Invite not found.");
        await _store.RevokeInviteAsync(id, cancellationToken).ConfigureAwait(false);
        await _audit.LogAsync(
            AuditService.ActionInviteRevoked,
            actorId,
            actorName,
            null,
            null,
            $"Invite {invite.Id} revoked.",
            ipAddress,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string id, string actorId, string actorName, string? ipAddress, CancellationToken cancellationToken = default)
    {
        await _store.DeleteInviteAsync(id, cancellationToken).ConfigureAwait(false);
        await _audit.LogAsync(
            AuditService.ActionInviteRevoked,
            actorId,
            actorName,
            null,
            null,
            $"Invite {id} deleted.",
            ipAddress,
            cancellationToken).ConfigureAwait(false);
    }

    public static string BuildInviteUrl(string baseUrl, string token) =>
        $"{baseUrl.TrimEnd('/')}/UserManager/invite#token={Uri.EscapeDataString(token)}";

    private async Task<InviteRecord?> FindAsync(string token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        if (!Guid.TryParseExact(token, "N", out _) && token.Length > 200)
        {
            return null;
        }

        return await _store.GetInviteByTokenHashAsync(TokenTools.HashToken(token), cancellationToken).ConfigureAwait(false);
    }
}
