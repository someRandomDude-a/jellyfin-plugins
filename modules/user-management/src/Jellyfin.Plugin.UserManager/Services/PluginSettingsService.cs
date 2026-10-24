using Jellyfin.Plugin.UserManager.Data;
using Jellyfin.Plugin.UserManager.Security;

namespace Jellyfin.Plugin.UserManager.Services;

public interface IIntegrationSecretReader
{
    Task<string?> ReadAsync(string name, CancellationToken ct);
}

/// <summary>
/// Effective plugin settings. Secret values are decrypted only in memory.
/// </summary>
public sealed class PluginSettings
{
    public string? SeerrUrl { get; set; }

    public string? SeerrApiKey { get; set; }
    public bool SeerrAllowHttp { get; set; }
    public bool SeerrAllowPrivateNetwork { get; set; }

    public string? DiscordBotToken { get; set; }

    public string? DiscordGuildId { get; set; }

    public string? DiscordAdminWebhookUrl { get; set; }

    public string? DiscordAdminRoleIds { get; set; }

    public string? PublicBaseUrl { get; set; }

    public int InviteDefaultExpiryDays { get; set; } = 7;

    public int InviteDefaultMaxUses { get; set; } = 1;

    public bool AllowPublicInviteRequests { get; set; }

    public bool NotifyAdminsOnJoin { get; set; } = true;

    public bool NotifyAdminsOnPasswordReset { get; set; } = true;

    public bool NotifyAdminsOnInviteUsed { get; set; } = true;

    public bool SyncSeerrOnCreate { get; set; } = true;

    public bool SyncSeerrOnPresetApply { get; set; } = true;

    public bool SeerrConfigured => !string.IsNullOrWhiteSpace(SeerrUrl) && !string.IsNullOrWhiteSpace(SeerrApiKey);

    public bool DiscordConfigured => !string.IsNullOrWhiteSpace(DiscordBotToken);
}

/// <summary>
/// Partial update for settings. A null secret means "keep existing", an empty string clears it.
/// </summary>
public sealed class SettingsUpdate
{
    public bool? SeerrAllowHttp { get; set; }
    public bool? SeerrAllowPrivateNetwork { get; set; }
    public string? SeerrUrl { get; set; }

    public string? SeerrApiKey { get; set; }

    public string? DiscordBotToken { get; set; }

    public string? DiscordGuildId { get; set; }

    public string? DiscordAdminWebhookUrl { get; set; }

    public string? DiscordAdminRoleIds { get; set; }

    public string? PublicBaseUrl { get; set; }

    public int? InviteDefaultExpiryDays { get; set; }

    public int? InviteDefaultMaxUses { get; set; }

    public bool? AllowPublicInviteRequests { get; set; }

    public bool? NotifyAdminsOnJoin { get; set; }

    public bool? NotifyAdminsOnPasswordReset { get; set; }

    public bool? NotifyAdminsOnInviteUsed { get; set; }

    public bool? SyncSeerrOnCreate { get; set; }

    public bool? SyncSeerrOnPresetApply { get; set; }
}

public sealed class PluginSettingsService
{
    public const string SeerrUrlKey = "seerr.url";
    public const string SeerrApiKeyKey = "seerr.apiKey";
    public const string DiscordBotTokenKey = "discord.botToken";
    public const string DiscordGuildIdKey = "discord.guildId";
    public const string DiscordWebhookKey = "discord.adminWebhook";
    public const string DiscordAdminRoleIdsKey = "discord.adminRoleIds";
    public const string PublicBaseUrlKey = "general.publicBaseUrl";
    public const string InviteExpiryKey = "invites.defaultExpiryDays";
    public const string InviteUsesKey = "invites.defaultMaxUses";
    public const string PublicInviteRequestsKey = "invites.allowPublicRequests";
    public const string NotifyJoinKey = "notifications.onJoin";
    public const string NotifyResetKey = "notifications.onReset";
    public const string NotifyInviteKey = "notifications.onInviteUsed";
    public const string SyncSeerrCreateKey = "seerr.syncOnCreate";
    public const string SyncSeerrPresetKey = "seerr.syncOnPreset";

    private readonly PluginStore _store;
    private readonly SecretProtector _protector;
    private readonly SemaphoreSlim _cacheLock = new(1, 1);
    private PluginSettings? _cached;
    private readonly IIntegrationSecretReader? _secrets;

    public PluginSettingsService(PluginStore store, SecretProtector protector, IIntegrationSecretReader? secrets = null)
    {
        _store = store;
        _protector = protector;
        _secrets = secrets;
    }

    public async Task<PluginSettings> GetAsync(CancellationToken cancellationToken = default)
    {
        var cached = _cached;
        if (cached is not null)
        {
            return cached;
        }

        await _cacheLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _cached ??= await LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    public async Task<PluginSettings> ReloadAsync(CancellationToken cancellationToken = default)
    {
        await _cacheLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _cached = await LoadAsync(cancellationToken).ConfigureAwait(false);
            return _cached;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    public async Task<PluginSettings> ApplyAsync(SettingsUpdate update, CancellationToken cancellationToken = default)
    {
        var raw = new Dictionary<string, string?>(await _store.GetSettingsAsync(cancellationToken).ConfigureAwait(false), StringComparer.OrdinalIgnoreCase);

        SetIfNotNull(raw, SeerrUrlKey, update.SeerrUrl);
        SetBool(raw, "seerr.allowHttp", update.SeerrAllowHttp);
        SetBool(raw, "seerr.allowPrivateNetwork", update.SeerrAllowPrivateNetwork);
        SetIfNotNull(raw, DiscordGuildIdKey, update.DiscordGuildId);
        SetIfNotNull(raw, DiscordAdminRoleIdsKey, update.DiscordAdminRoleIds);
        SetIfNotNull(raw, PublicBaseUrlKey, update.PublicBaseUrl);

        SetSecret(raw, SeerrApiKeyKey, update.SeerrApiKey, _protector);
        SetSecret(raw, DiscordBotTokenKey, update.DiscordBotToken, _protector);
        SetSecret(raw, DiscordWebhookKey, update.DiscordAdminWebhookUrl, _protector);

        if (update.InviteDefaultExpiryDays is int expiry)
        {
            raw[InviteExpiryKey] = Math.Clamp(expiry, 1, 365).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (update.InviteDefaultMaxUses is int uses)
        {
            raw[InviteUsesKey] = Math.Clamp(uses, 1, 10000).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        SetBool(raw, PublicInviteRequestsKey, update.AllowPublicInviteRequests);
        SetBool(raw, NotifyJoinKey, update.NotifyAdminsOnJoin);
        SetBool(raw, NotifyResetKey, update.NotifyAdminsOnPasswordReset);
        SetBool(raw, NotifyInviteKey, update.NotifyAdminsOnInviteUsed);
        SetBool(raw, SyncSeerrCreateKey, update.SyncSeerrOnCreate);
        SetBool(raw, SyncSeerrPresetKey, update.SyncSeerrOnPresetApply);

        await _store.SaveSettingsAsync(raw, cancellationToken).ConfigureAwait(false);
        return await ReloadAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<PluginSettings> LoadAsync(CancellationToken cancellationToken)
    {
        var raw = await _store.GetSettingsAsync(cancellationToken).ConfigureAwait(false);

        return new PluginSettings
        {
            SeerrUrl = Trim(raw, SeerrUrlKey),
            SeerrAllowHttp = GetBool(raw, "seerr.allowHttp", false),
            SeerrAllowPrivateNetwork = GetBool(raw, "seerr.allowPrivateNetwork", false),
            SeerrApiKey = _secrets is null ? Unprotect(raw, SeerrApiKeyKey) : await _secrets.ReadAsync(SeerrApiKeyKey, cancellationToken),
            DiscordBotToken = Unprotect(raw, DiscordBotTokenKey),
            DiscordGuildId = Trim(raw, DiscordGuildIdKey),
            DiscordAdminWebhookUrl = Unprotect(raw, DiscordWebhookKey),
            DiscordAdminRoleIds = Trim(raw, DiscordAdminRoleIdsKey),
            PublicBaseUrl = Trim(raw, PublicBaseUrlKey)?.TrimEnd('/'),
            InviteDefaultExpiryDays = GetInt(raw, InviteExpiryKey, 7, 1, 365),
            InviteDefaultMaxUses = GetInt(raw, InviteUsesKey, 1, 1, 10000),
            AllowPublicInviteRequests = GetBool(raw, PublicInviteRequestsKey, false),
            NotifyAdminsOnJoin = GetBool(raw, NotifyJoinKey, true),
            NotifyAdminsOnPasswordReset = GetBool(raw, NotifyResetKey, true),
            NotifyAdminsOnInviteUsed = GetBool(raw, NotifyInviteKey, true),
            SyncSeerrOnCreate = GetBool(raw, SyncSeerrCreateKey, true),
            SyncSeerrOnPresetApply = GetBool(raw, SyncSeerrPresetKey, true)
        };
    }

    private string? Unprotect(IReadOnlyDictionary<string, string?> raw, string key)
    {
        if (!raw.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        // A lost or wrong key is an operator recovery condition, not an empty setting.
        return _protector.Unprotect(value);
    }

    private static string? Trim(IReadOnlyDictionary<string, string?> raw, string key) =>
        raw.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    private static int GetInt(IReadOnlyDictionary<string, string?> raw, string key, int fallback, int min, int max)
    {
        if (raw.TryGetValue(key, out var value)
            && int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            return Math.Clamp(parsed, min, max);
        }

        return fallback;
    }

    private static bool GetBool(IReadOnlyDictionary<string, string?> raw, string key, bool fallback)
    {
        if (raw.TryGetValue(key, out var value) && bool.TryParse(value, out var parsed))
        {
            return parsed;
        }

        return fallback;
    }

    private static void SetIfNotNull(Dictionary<string, string?> raw, string key, string? value)
    {
        if (value is not null)
        {
            raw[key] = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }

    private static void SetBool(Dictionary<string, string?> raw, string key, bool? value)
    {
        if (value is bool parsed)
        {
            raw[key] = parsed ? "true" : "false";
        }
    }

    private static void SetSecret(Dictionary<string, string?> raw, string key, string? value, SecretProtector protector)
    {
        if (value is null)
        {
            return;
        }

        raw[key] = string.IsNullOrWhiteSpace(value) ? null : protector.Protect(value.Trim());
    }
}
