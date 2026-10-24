using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.UserManager.Services;

public sealed class SeerrException : Exception
{
    public SeerrException(int statusCode, string message)
        : base(message)
    {
        StatusCode = statusCode;
    }

    public int StatusCode { get; }
}

public sealed class SeerrUser
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("username")]
    public string? Username { get; set; }

    [JsonPropertyName("email")]
    public string? Email { get; set; }

    [JsonPropertyName("jellyfinUserId")]
    public string? JellyfinUserId { get; set; }

    [JsonPropertyName("permissions")]
    public long Permissions { get; set; }

    [JsonPropertyName("requestCount")]
    public int RequestCount { get; set; }

    [JsonPropertyName("createdAt")]
    public string? CreatedAt { get; set; }
}

internal sealed class SeerrUserPage
{
    [JsonPropertyName("results")]
    public List<SeerrUser> Results { get; set; } = [];
}

/// <summary>
/// Minimal Seerr API client. Jellyfin remains the identity provider; Seerr accounts are imported
/// from Jellyfin and only their permission bitmask is managed here.
/// </summary>
public sealed class SeerrClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly PluginSettingsService _settings;
    private PluginSettings? _snapshot;

    // A lifecycle operation uses one frozen destination/credential set; settings rotation cannot retarget a job mid-flight.
    public SeerrClient Snapshot(PluginSettings connection) => new(null!, _settings) { _snapshot = new PluginSettings
    {
        SeerrUrl = connection.SeerrUrl, SeerrApiKey = connection.SeerrApiKey,
        SeerrAllowHttp = connection.SeerrAllowHttp, SeerrAllowPrivateNetwork = connection.SeerrAllowPrivateNetwork
    } };

    public SeerrClient(IHttpClientFactory httpClientFactory, PluginSettingsService settings)
    {
        _settings = settings;
    }

    public async Task<SeerrUser?> GetUserByJellyfinIdAsync(Guid jellyfinUserId, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            $"/user/jellyfin/{jellyfinUserId:N}",
            null,
            allowNotFound: true,
            cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        var user = await ReadAsync<SeerrUser>(response, cancellationToken).ConfigureAwait(false);
        if (user is null || !Guid.TryParse(user.JellyfinUserId, out var actual) || actual != jellyfinUserId) throw new SeerrException(502, "Seerr did not prove the requested Jellyfin identity.");
        return user;
    }

    public async Task<IReadOnlyList<SeerrUser>> GetUsersAsync(int take = 500, CancellationToken cancellationToken = default, int skip = 0)
    {
        if (take is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(take));
        if (skip is < 0 or > 100000) throw new ArgumentOutOfRangeException(nameof(skip));
        using var response = await SendAsync(HttpMethod.Get, $"/user?take={take}&skip={skip}", null, false, cancellationToken)
            .ConfigureAwait(false);
        var page = await ReadAsync<SeerrUserPage>(response, cancellationToken).ConfigureAwait(false);
        return page?.Results ?? [];
    }

    /// <summary>
    /// Ensures a Seerr account exists for a Jellyfin user and applies the given permission bitmask.
    /// </summary>
    public async Task<SeerrUser> SyncUserAsync(Guid jellyfinUserId, long permissions, CancellationToken cancellationToken = default)
    {
        var user = await EnsureUserAsync(jellyfinUserId, cancellationToken).ConfigureAwait(false);
        var merged = SeerrPermissions.Merge(user.Permissions, permissions);
        if (user.Permissions != merged)
        {
            await SetPermissionsAsync(user.Id, permissions, cancellationToken).ConfigureAwait(false);
            user.Permissions = merged;
        }

        return user;
    }

    public async Task<SeerrUser> EnsureUserAsync(Guid jellyfinUserId, CancellationToken cancellationToken = default)
    {
        var existing = await GetUserByJellyfinIdAsync(jellyfinUserId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing;
        }

        using var response = await SendAsync(
            HttpMethod.Post,
            "/user/import-from-jellyfin",
            new { jellyfinUserIds = new[] { jellyfinUserId.ToString("N") } },
            allowNotFound: true,
            cancellationToken).ConfigureAwait(false);

        // Only documented success responses are evidence; never turn an HTTP conflict into success.
        if (!response.IsSuccessStatusCode)
        {
            await ThrowFromResponseAsync(response, cancellationToken).ConfigureAwait(false);
        }

        var imported = await GetUserByJellyfinIdAsync(jellyfinUserId, cancellationToken).ConfigureAwait(false);
        if (imported is not null)
        {
            return imported;
        }

        // Only immutable Jellyfin IDs are acceptable evidence. Never fall back to names.
        var all = await GetUsersAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        return all.SingleOrDefault(u => Guid.TryParse(u.JellyfinUserId, out var id) && id == jellyfinUserId)
            ?? throw new SeerrException(502, "Seerr imported no account for this Jellyfin user. Check that the user exists in Jellyfin and that Seerr can reach it.");
    }

    public async Task SetPermissionsAsync(int seerrUserId, long permissions, CancellationToken cancellationToken = default)
    {
        var actual = await RequireMutableAsync(seerrUserId, cancellationToken);
        permissions = SeerrPermissions.Merge(actual.Permissions, permissions & SeerrPermissions.KnownMask);
        using var response = await SendAsync(
            HttpMethod.Post,
            $"/user/{seerrUserId}/settings/permissions",
            new { permissions },
            false,
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteUserAsync(Guid jellyfinUserId, CancellationToken cancellationToken = default)
    {
        var user = await GetUserByJellyfinIdAsync(jellyfinUserId, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return;
        }
        _ = await RequireMutableAsync(user.Id, cancellationToken);

        using var response = await SendAsync(
            HttpMethod.Delete,
            $"/user/{user.Id}",
            null,
            allowNotFound: true,
            cancellationToken).ConfigureAwait(false);

        if (response.StatusCode != HttpStatusCode.NotFound)
        {
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<SeerrUser> CreateRequestAsync(
        Guid jellyfinUserId,
        int tmdbId,
        string mediaType,
        CancellationToken cancellationToken = default)
    {
        var user = await EnsureUserAsync(jellyfinUserId, cancellationToken).ConfigureAwait(false);
        var payload = new Dictionary<string, object?>
        {
            ["mediaType"] = mediaType,
            ["mediaId"] = tmdbId,
            ["userId"] = user.Id
        };

        using var response = await SendAsync(HttpMethod.Post, "/request", payload, false, cancellationToken)
            .ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return user;
    }

    /// <summary>
    /// Verifies the URL and API key by making an authenticated request.
    /// </summary>
    public async Task TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Get, "/user?take=1&skip=0", null, false, cancellationToken)
            .ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<JsonElement> ReadDocumentAsync(string path, CancellationToken ct)
    {
        if (path is not ("/status?checkUpdateAvailable=false" or "/settings/main") && !System.Text.RegularExpressions.Regex.IsMatch(path, @"^/user/[1-9][0-9]*(/settings/main|/requests\?take=[0-9]+&skip=[0-9]+)?$"))
            throw new ArgumentException("Unsupported lifecycle read");
        using var response = await SendAsync(HttpMethod.Get, path, null, false, ct);
        return await ReadAsync<JsonElement>(response, ct);
    }
    public async Task<SeerrUser?> GetUserAsync(int id, CancellationToken ct)
    {
        if (id < 1) throw new ArgumentOutOfRangeException(nameof(id));
        using var response = await SendAsync(HttpMethod.Get, $"/user/{id}", null, true, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        var user = await ReadAsync<SeerrUser>(response, ct);
        if (user?.Id != id) throw new SeerrException(502, "Account identity mismatch");
        return user;
    }
    public async Task ImportNativeAsync(Guid id, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Post, "/user/import-from-jellyfin", new { jellyfinUserIds = new[] { id.ToString("N") } }, false, ct);
        await EnsureSuccessAsync(response, ct);
    }
    public async Task SetQuotaDocumentAsync(int id, JsonElement preservedProfile, CancellationToken ct)
    {
        _ = await RequireMutableAsync(id, ct);
        using var response = await SendAsync(HttpMethod.Post, $"/user/{id}/settings/main", preservedProfile, false, ct);
        await EnsureSuccessAsync(response, ct);
    }
    public async Task DeleteVerifiedAsync(int id, Guid nativeId, string createdAt, CancellationToken ct)
    {
        var actual = await GetUserAsync(id, ct); if (actual is null) return;
        actual = await RequireMutableAsync(id, ct);
        if (!Guid.TryParse(actual.JellyfinUserId, out var native) || native != nativeId || actual.CreatedAt != createdAt) throw new SeerrException(409, "Saved account identity changed");
        using var response = await SendAsync(HttpMethod.Delete, $"/user/{id}", null, true, ct);
        if (response.StatusCode != HttpStatusCode.NotFound) await EnsureSuccessAsync(response, ct);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        object? body,
        bool allowNotFound,
        CancellationToken cancellationToken)
    {
        var settings = _snapshot ?? await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
        if (!settings.SeerrConfigured)
        {
            throw new SeerrException(400, "Seerr is not configured. Add the Seerr URL and API key in User Manager settings.");
        }

        var uri = SeerrDestination.ApiUri(settings.SeerrUrl!, path, settings.SeerrAllowHttp);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20)); cancellationToken = deadline.Token;

        using var request = new HttpRequestMessage(method, uri);
        request.Headers.TryAddWithoutValidation("X-Api-Key", settings.SeerrApiKey);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }

        using var client = new HttpClient(SeerrDestination.Handler(uri, settings.SeerrAllowPrivateNetwork));
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var buffer = new MemoryStream(); var bytes = new byte[8192]; int count;
            while ((count = await stream.ReadAsync(bytes, cancellationToken)) > 0)
            {
                if (buffer.Length + count > 1024 * 1024) throw new SeerrException(502, "Seerr response exceeds the allowed size.");
                buffer.Write(bytes, 0, count);
            }
            var old = response.Content; response.Content = new ByteArrayContent(buffer.ToArray()); old.Dispose();
            return response;
        }
        catch { response.Dispose(); throw; }
    }

    private async Task<SeerrUser> RequireMutableAsync(int id, CancellationToken ct)
    {
        if (id <= 1) throw new SeerrException(409, "Seerr owner is protected.");
        using var response = await SendAsync(HttpMethod.Get, $"/user/{id}", null, false, ct);
        var user = await ReadAsync<SeerrUser>(response, ct);
        if (user is null || user.Id != id || (user.Permissions & SeerrPermissions.Admin) != 0) throw new SeerrException(409, "Seerr administrator or unverified account is protected.");
        return user;
    }

    private static async Task<T?> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        await ThrowFromResponseAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private static Task ThrowFromResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        throw new SeerrException(
            (int)response.StatusCode,
            $"Seerr returned HTTP {(int)response.StatusCode}. Upstream content is deliberately withheld.");
    }
}
