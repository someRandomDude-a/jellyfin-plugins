using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AccountHub.Core;
using AccountHub.Core.Seerr;
using Jellyfin.Plugin.UserManager.Services;
using MediaBrowser.Common.Plugins;

namespace AccountHub.Seerr;

/// <summary>Read-only public runtime configuration discovery. No Enhanced source or private config file access.</summary>
public sealed class SeerrConnectionDiscovery(IPluginManager plugins, PluginSettingsService settings)
{
    public async Task<(PluginSettings Settings, string Source)> ResolveAsync(CancellationToken ct)
    {
        var configured = await settings.GetAsync(ct);
        if (configured.SeerrConfigured) return (Copy(configured, configured.SeerrUrl!, configured.SeerrApiKey!), "override");
        var candidates = plugins.Plugins.Where(p => p.IsEnabledAndSupported && p.Id == Guid.Parse("f69e946a-4b3c-4e9a-8f0a-8d7c1b2c4d9b")).ToArray();
        if (candidates.Length == 1 && candidates[0].Instance is IHasPluginConfiguration enhanced && ReadPublicConfiguration(enhanced.Configuration) is { } value)
            return (Copy(configured, value.Url, value.Key), "enhanced");
        throw new HubException("seerr_not_configured", "Configure an explicit connection, or one unambiguous Enhanced connection", true);
    }
    private static PluginSettings Copy(PluginSettings value, string url, string key) => new() { SeerrUrl = url, SeerrApiKey = key, SeerrAllowHttp = value.SeerrAllowHttp, SeerrAllowPrivateNetwork = value.SeerrAllowPrivateNetwork };
    public static (string Url, string Key)? ReadPublicConfiguration(object config)
    {
        try
        {
            object? Get(string name) => config.GetType().GetProperty(name)?.GetValue(config);
            if (Get("JellyseerrEnabled") is not true || Get("JellyseerrUrls") is not string urls || Get("JellyseerrApiKey") is not string key) return null;
            var choices = urls.Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (choices.Length != 1 || key.Length is < 1 or > 512 || key.Any(c => c is < (char)33 or > (char)126)) return null;
            _ = SeerrDestination.ApiUri(choices[0], "/user", allowHttp: true);
            return (choices[0], key);
        }
        catch { return null; }
    }
}

public sealed class SeerrUpstream(SeerrClient client, SeerrConnectionDiscovery discovery) : ISeerrUpstream
{
    public async Task<ISeerrSession> OpenAsync(CancellationToken ct)
    {
        var connection = await discovery.ResolveAsync(ct);
        var frozen = client.Snapshot(connection.Settings);
        var status = await frozen.ReadDocumentAsync("/status?checkUpdateAvailable=false", ct);
        var version = status.GetProperty("version").GetString();
        if (version is not ("3.5.1" or "v3.5.1")) throw new HubException("seerr_version_unsupported", "Lifecycle writes require source-reviewed Seerr 3.5.1");
        var main = await frozen.ReadDocumentAsync("/settings/main", ct);
        if (main.GetProperty("mediaServerType").GetInt32() != 2) throw new HubException("seerr_native_server_required", "Seerr must use Jellyfin as its media server");
        var owner = await frozen.GetUserAsync(1, ct);
        if (owner?.CreatedAt is not { Length: > 0 } || !Guid.TryParse(owner.JellyfinUserId, out var ownerNative)) throw new HubException("seerr_connection_unverified", "Cannot establish immutable owner/instance evidence");
        var defaults = main.GetProperty("defaultQuotas");
        SeerrQuota Quota(string kind) { var q = defaults.GetProperty(kind); return new(NullableInt(q, "quotaLimit"), NullableInt(q, "quotaDays")); }
        var canonical = SeerrDestination.ApiUri(connection.Settings.SeerrUrl!, "/user", connection.Settings.SeerrAllowHttp).GetLeftPart(UriPartial.Path);
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical + "\n" + ownerNative.ToString("N") + "\n" + owner.CreatedAt)));
        // The settings document may include API keys. Project only these fields; never persist/return the document.
        var capabilities = new SeerrCapabilities(fingerprint, version, main.GetProperty("defaultPermissions").GetInt64(), Quota("movie"), Quota("tv"), connection.Source);
        return new Session(frozen, capabilities);
    }
    internal static int? NullableInt(JsonElement value, string key) => value.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : null;
    private sealed class Session(SeerrClient client, SeerrCapabilities capabilities) : ISeerrSession
    {
        public SeerrCapabilities Capabilities => capabilities;
        private static SeerrAccount Account(SeerrUser u) => new(u.Id, Guid.TryParse(u.JellyfinUserId, out var native) ? native : null, u.Permissions, u.CreatedAt ?? "", u.Username);
        public async Task<SeerrAccount?> FindAsync(int id, CancellationToken ct) => await client.GetUserAsync(id, ct) is { } u ? Account(u) : null;
        public async Task<SeerrAccount?> FindNativeAsync(Guid id, CancellationToken ct)
        {
            var exact = await client.GetUserByJellyfinIdAsync(id, ct);
            // The lookup route uses findOne, not a uniqueness constraint. Verify uniqueness with bounded pagination.
            SeerrUser? match = null;
            for (var skip = 0; skip <= 10000; skip += 100)
            {
                var page = await client.GetUsersAsync(100, ct, skip);
                foreach (var candidate in page.Where(u => Guid.TryParse(u.JellyfinUserId, out var native) && native == id))
                {
                    if (match is not null) throw new HubException("seerr_identity_ambiguous", "Multiple upstream accounts carry the native ID");
                    match = candidate;
                }
                if (page.Count < 100)
                {
                    if (exact?.Id != match?.Id) throw new HubException("seerr_identity_mismatch", "Upstream identity lookup and directory disagree");
                    return match is null ? null : Account(match);
                }
            }
            throw new HubException("seerr_directory_bound", "Cannot prove uniqueness beyond 10000 upstream accounts");
        }
        public Task ImportAsync(Guid id, CancellationToken ct) => client.ImportNativeAsync(id, ct);
        public async Task<SeerrDesired> ReadDesiredAsync(int id, CancellationToken ct)
        {
            var account = await client.GetUserAsync(id, ct) ?? throw new HubException("not_found", "Account missing");
            var profile = await client.ReadDocumentAsync($"/user/{id}/settings/main", ct);
            return new(account.Permissions & SeerrPermissions.KnownMask, new(NullableInt(profile, "movieQuotaLimit"), NullableInt(profile, "movieQuotaDays")), new(NullableInt(profile, "tvQuotaLimit"), NullableInt(profile, "tvQuotaDays")));
        }
        public async Task ApplyAsync(int id, Guid nativeId, string createdAt, SeerrDesired desired, CancellationToken ct)
        {
            SeerrLifecycle.Validate(desired);
            var account = await client.GetUserAsync(id, ct) ?? throw new HubException("not_found", "Account missing");
            SeerrLifecycle.Verify(Account(account), nativeId, id, createdAt);
            if (account.Id <= 1 || (account.Permissions & SeerrPermissions.Admin) != 0) throw new HubException("administrator_protected", "Upstream administrator protected");
            var current = await ReadDesiredAsync(id, ct);
            if (current.MovieQuota != desired.MovieQuota || current.TvQuota != desired.TvQuota)
            {
                // Upstream skips quota writes for MANAGE_USERS targets. Do not silently claim success or demote privileges transiently.
                if ((account.Permissions & SeerrPermissions.ManageUsers) != 0) throw new HubException("seerr_quota_privilege_bypass", "Remove Manage users explicitly before changing quotas; this privilege also bypasses request limits");
                var profile = await client.ReadDocumentAsync($"/user/{id}/settings/main", ct);
                await client.SetQuotaDocumentAsync(id, PreserveQuotaProfile(profile, desired), ct);
            }
            if (current.Permissions != desired.Permissions) await client.SetPermissionsAsync(id, desired.Permissions, ct);
        }
        public async Task DeleteAsync(int id, Guid nativeId, string createdAt, CancellationToken ct)
        {
            await client.DeleteVerifiedAsync(id, nativeId, createdAt, ct);
        }
        public async Task<JsonElement> RequestsAsync(int id, int skip, int limit, CancellationToken ct)
        {
            var page = await client.ReadDocumentAsync($"/user/{id}/requests?take={limit}&skip={skip}", ct);
            // No requester/modifier, email, avatar, comments, tokens or arbitrary upstream JSON crosses this boundary.
            return ProjectRequests(page);
        }
    }
    public static JsonElement PreserveQuotaProfile(JsonElement profile, SeerrDesired desired)
    {
        var body = new JsonObject();
        foreach (var key in new[] { "username", "email", "locale", "discoverRegion", "streamingRegion", "originalLanguage", "watchlistSyncMovies", "watchlistSyncTv" })
            if (profile.TryGetProperty(key, out var value)) body[key] = JsonNode.Parse(value.GetRawText());
        body["movieQuotaLimit"] = desired.MovieQuota.Limit; body["movieQuotaDays"] = desired.MovieQuota.Days;
        body["tvQuotaLimit"] = desired.TvQuota.Limit; body["tvQuotaDays"] = desired.TvQuota.Days;
        return JsonSerializer.SerializeToElement(body);
    }
    public static JsonElement ProjectRequests(JsonElement page) => JsonSerializer.SerializeToElement(new
    {
        items = page.GetProperty("results").EnumerateArray().Take(100).Select(r => new
        {
            id = r.GetProperty("id").GetInt32(),
            status = r.GetProperty("status").GetInt32(),
            mediaType = r.TryGetProperty("type", out var type) && type.GetString() is "movie" or "tv" ? type.GetString() : null,
            createdAt = r.TryGetProperty("createdAt", out var date) ? date.GetString() : null
        }).ToArray()
    }, AccountHub.Api.Contracts.Wire.Options);
}
