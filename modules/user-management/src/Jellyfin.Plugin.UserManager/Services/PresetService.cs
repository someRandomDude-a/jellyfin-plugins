using Jellyfin.Plugin.UserManager.Data;
using MediaBrowser.Model.Users;

namespace Jellyfin.Plugin.UserManager.Services;

public sealed record PresetApplyResult(bool JellyfinApplied, bool SeerrSynced, string? SeerrError);

public sealed class PresetService
{
    private readonly PluginStore _store;
    private readonly JellyfinUserService _users;
    private readonly SeerrClient _seerr;
    private readonly PluginSettingsService _settings;
    private readonly AuditService _audit;
    private readonly ILogger<PresetService> _logger;

    public PresetService(
        PluginStore store,
        JellyfinUserService users,
        SeerrClient seerr,
        PluginSettingsService settings,
        AuditService audit,
        ILogger<PresetService> logger)
    {
        _store = store;
        _users = users;
        _seerr = seerr;
        _settings = settings;
        _audit = audit;
        _logger = logger;
    }

    public Task<IReadOnlyList<PresetRecord>> GetAllAsync(CancellationToken cancellationToken = default) =>
        _store.GetPresetsAsync(cancellationToken);

    public async Task<PresetRecord?> GetAsync(string id, CancellationToken cancellationToken = default) =>
        await _store.GetPresetAsync(id, cancellationToken).ConfigureAwait(false);

    public async Task<PresetRecord> GetDefaultAsync(CancellationToken cancellationToken = default) =>
        await _store.GetDefaultPresetAsync(cancellationToken).ConfigureAwait(false)
        ?? throw new UserManagerException(500, "No permission presets exist.");

    public async Task<PresetRecord> CreateAsync(
        string name,
        string? description,
        string? jellyfinPolicyJson,
        long seerrPermissions,
        bool isDefault,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var preset = new PresetRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = ValidateName(name),
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            JellyfinPolicyJson = PolicyTools.Serialize(PolicyTools.Deserialize(jellyfinPolicyJson)),
            SeerrPermissions = seerrPermissions,
            IsDefault = isDefault,
            CreatedAt = now,
            UpdatedAt = now
        };

        await _store.UpsertPresetAsync(preset, cancellationToken).ConfigureAwait(false);
        return preset;
    }

    public async Task<PresetRecord> UpdateAsync(
        string id,
        string name,
        string? description,
        string? jellyfinPolicyJson,
        long seerrPermissions,
        bool isDefault,
        CancellationToken cancellationToken = default)
    {
        var existing = await _store.GetPresetAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new UserManagerException(404, "Preset not found.");

        existing.Name = ValidateName(name);
        existing.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        existing.JellyfinPolicyJson = PolicyTools.Serialize(PolicyTools.Deserialize(jellyfinPolicyJson));
        existing.SeerrPermissions = seerrPermissions;
        existing.IsDefault = isDefault;
        existing.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        await _store.UpsertPresetAsync(existing, cancellationToken).ConfigureAwait(false);
        return existing;
    }

    public async Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        var presets = await _store.GetPresetsAsync(cancellationToken).ConfigureAwait(false);
        if (presets.Count <= 1)
        {
            throw new UserManagerException(400, "At least one preset must remain.");
        }

        var target = presets.FirstOrDefault(p => p.Id == id)
            ?? throw new UserManagerException(404, "Preset not found.");
        await _store.DeletePresetAsync(id, cancellationToken).ConfigureAwait(false);

        if (target.IsDefault)
        {
            var next = presets.First(p => p.Id != id);
            next.IsDefault = true;
            next.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await _store.UpsertPresetAsync(next, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<PresetApplyResult> ApplyToUserAsync(
        Guid userId,
        string presetId,
        string actorId,
        string actorName,
        string? ipAddress,
        CancellationToken cancellationToken = default)
    {
        var preset = await _store.GetPresetAsync(presetId, cancellationToken).ConfigureAwait(false)
            ?? throw new UserManagerException(404, "Preset not found.");
        var user = _users.GetUser(userId) ?? throw new UserManagerException(404, "User not found.");
        var policy = PolicyTools.Deserialize(preset.JellyfinPolicyJson);

        await _users.ApplyPresetAsync(userId, policy, cancellationToken).ConfigureAwait(false);
        await _store.SetUserPresetAsync(userId.ToString("N"), preset.Id, cancellationToken).ConfigureAwait(false);

        var seerrSynced = false;
        string? seerrError = null;
        var settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
        if (settings.SyncSeerrOnPresetApply && settings.SeerrConfigured)
        {
            try
            {
                await _seerr.SyncUserAsync(userId, preset.SeerrPermissions, cancellationToken).ConfigureAwait(false);
                seerrSynced = true;
            }
            catch (Exception ex)
            {
                seerrError = ex.Message;
                _logger.LogWarning(ex, "Could not sync Seerr permissions for {UserId}", userId);
            }
        }

        await _audit.LogAsync(
            AuditService.ActionPresetApplied,
            actorId,
            actorName,
            userId.ToString("N"),
            user.Username,
            $"Preset '{preset.Name}' applied. Seerr synced: {seerrSynced}.",
            ipAddress,
            cancellationToken).ConfigureAwait(false);

        return new PresetApplyResult(true, seerrSynced, seerrError);
    }

    public async Task<PresetRecord> ImportFromUserAsync(
        Guid userId,
        string name,
        string? description,
        CancellationToken cancellationToken = default)
    {
        var user = _users.GetUser(userId) ?? throw new UserManagerException(404, "User not found.");
        var policy = _users.GetPolicy(userId);
        var seerrPermissions = 0L;

        var settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
        if (settings.SeerrConfigured)
        {
            try
            {
                var seerrUser = await _seerr.GetUserByJellyfinIdAsync(userId, cancellationToken).ConfigureAwait(false);
                seerrPermissions = seerrUser?.Permissions ?? 0;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not read Seerr permissions for {UserId}", userId);
            }
        }

        var preset = await CreateAsync(
            string.IsNullOrWhiteSpace(name) ? $"{user.Username} copy" : name,
            description ?? $"Imported from {user.Username}",
            PolicyTools.Serialize(policy),
            seerrPermissions,
            false,
            cancellationToken).ConfigureAwait(false);

        return preset;
    }

    public async Task SeedDefaultsAsync(CancellationToken cancellationToken = default)
    {
        var existing = await _store.GetPresetsAsync(cancellationToken).ConfigureAwait(false);
        if (existing.Count > 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var viewer = BuildViewerPolicy();
        var family = BuildViewerPolicy();
        family.EnableLiveTvAccess = true;
        var trusted = BuildViewerPolicy();
        trusted.EnableContentDeletion = false;

        await _store.UpsertPresetAsync(new PresetRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = "Basic Viewer",
            Description = "Playback and downloads with Seerr movie/TV requests.",
            JellyfinPolicyJson = PolicyTools.Serialize(viewer),
            SeerrPermissions = SeerrPermissions.DefaultRequester,
            IsDefault = true,
            CreatedAt = now,
            UpdatedAt = now
        }, cancellationToken).ConfigureAwait(false);

        await _store.UpsertPresetAsync(new PresetRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = "Family",
            Description = "Playback, downloads and Live TV, without Seerr requests.",
            JellyfinPolicyJson = PolicyTools.Serialize(family),
            SeerrPermissions = 0,
            CreatedAt = now,
            UpdatedAt = now
        }, cancellationToken).ConfigureAwait(false);

        await _store.UpsertPresetAsync(new PresetRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = "Trusted Requester",
            Description = "Requests are auto-approved in Seerr.",
            JellyfinPolicyJson = PolicyTools.Serialize(trusted),
            SeerrPermissions = SeerrPermissions.DefaultRequester | SeerrPermissions.AutoApprove,
            CreatedAt = now,
            UpdatedAt = now
        }, cancellationToken).ConfigureAwait(false);
    }

    private static UserPolicy BuildViewerPolicy()
    {
        var policy = new UserPolicy
        {
            IsHidden = false,
            IsAdministrator = false,
            IsDisabled = false,
            EnableAllFolders = true,
            EnableAllDevices = true,
            EnableAllChannels = true,
            EnableMediaPlayback = true,
            EnableAudioPlaybackTranscoding = true,
            EnableVideoPlaybackTranscoding = true,
            EnablePlaybackRemuxing = true,
            EnableContentDownloading = true,
            EnableContentDeletion = false,
            EnableSyncTranscoding = true,
            EnableMediaConversion = true,
            EnableRemoteAccess = true,
            EnablePublicSharing = false,
            EnableLiveTvAccess = false,
            EnableLiveTvManagement = false,
            EnableCollectionManagement = false,
            EnableSubtitleManagement = false,
            EnableLyricManagement = false,
            EnableRemoteControlOfOtherUsers = false,
            EnableSharedDeviceControl = false,
            EnableUserPreferenceAccess = true,
            MaxActiveSessions = 0,
            LoginAttemptsBeforeLockout = -1
        };
        return PolicyTools.Normalize(policy);
    }

    private static string ValidateName(string name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length is < 1 or > 64)
        {
            throw new UserManagerException(400, "Preset names must be between 1 and 64 characters.");
        }

        return trimmed;
    }
}
