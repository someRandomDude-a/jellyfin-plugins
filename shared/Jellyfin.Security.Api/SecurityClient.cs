using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace AccountHub.Security;

// No generic proxy method: only the explicitly allowed upstream operations exist.
public sealed class SecurityClient(HttpClient http, ISecurityCompatibility compatibility, ReadModelCache cache)
{
    public Task<UserStatus> MyStatusAsync(CallerSession caller, CancellationToken ct) =>
        ReadAsync<UserStatus>(caller, "MyStatus", ct);
    public Task<UserStatus[]> UsersAsync(CallerSession caller, CancellationToken ct, bool refresh = false) =>
        CachedAsync<UserStatus>(caller, "Users", refresh, ct);
    public Task<TrustedDevice[]> DevicesAsync(CallerSession caller, CancellationToken ct, bool refresh = false) =>
        CachedAsync<TrustedDevice>(caller, "AllTrustedDevices", refresh, ct);

    public async Task<UserSummary> SummaryAsync(CallerSession caller, Guid userId, CancellationToken ct)
    {
        if (userId == Guid.Empty) throw new SecurityFailure("invalid_request", 400);
        return await ReadAsync<UserSummary>(caller, $"Users/{userId:D}/Summary", ct);
    }

    private async Task<T[]> CachedAsync<T>(CallerSession caller, string route, bool refresh, CancellationToken ct)
    {
        if (!compatibility.Check().IsOk) { cache.Invalidate(); throw new SecurityFailure("security_incompatible", 409); }
        var key = $"{caller.UserId:D}:{caller.ServerBaseUri.AbsoluteUri}:{route}";
        if (!refresh && cache.Get<T[]>(key) is { } cached) return cached.ToArray();
        var result = await ReadAsync<T[]>(caller, route, ct);
        cache.Put(key, result, result.LongLength * 1024);
        return result.ToArray();
    }

    public async Task VerifyAsync(CallerSession caller, StepUpProof proof, CancellationToken ct)
    {
        if ((string.IsNullOrWhiteSpace(proof.Code) && string.IsNullOrWhiteSpace(proof.StepUpToken))
            || proof.Code?.Length > 256 || proof.StepUpToken?.Length > 4096)
            throw new SecurityFailure("step_up_required", 403);
        HttpResponseMessage response;
        try { response = await SendAsync(caller, HttpMethod.Post, "StepUp/Verify", proof, ct); }
        catch (SecurityFailure ex) when (ex.Code == "forbidden") { throw new SecurityFailure("step_up_failed", 403); }
        using var ownedResponse = response;
        using var body = await ReadJsonAsync(response, ct);
        if (body.RootElement.ValueKind != JsonValueKind.Object
            || !body.RootElement.TryGetProperty("verified", out var verified) || verified.ValueKind != JsonValueKind.True)
            throw new SecurityFailure("step_up_failed", 403);
    }

    internal async Task RevokeAsync(CallerSession caller, Guid userId, string recordId, CancellationToken ct)
    {
        if (userId == Guid.Empty || !ValidRecordId(recordId))
            throw new SecurityFailure("invalid_request", 400);
        try
        {
            using var response = await SendAsync(caller, HttpMethod.Delete,
                $"Users/{userId:D}/Devices/{Uri.EscapeDataString(recordId)}", null, ct);
        }
        finally { cache.Invalidate(); }
    }

    internal async Task<int?> ForceLogoutAsync(CallerSession caller, Guid userId, CancellationToken ct)
    {
        if (userId == Guid.Empty) throw new SecurityFailure("invalid_request", 400);
        try
        {
            using var response = await SendAsync(caller, HttpMethod.Post, $"Users/{userId:D}/ForceLogout", null, ct);
            using var body = await ReadJsonAsync(response, ct);
            return body.RootElement.ValueKind == JsonValueKind.Object
                && body.RootElement.TryGetProperty("sessionsTerminated", out var count)
                && count.ValueKind == JsonValueKind.Number && count.TryGetInt32(out var n) && n >= 0 ? n : null;
        }
        finally { cache.Invalidate(); }
    }

    internal async Task WebhookTestAsync(CallerSession caller, CancellationToken ct)
    {
        // Upstream tests ALL channels. Do not return arbitrary native channel response data.
        using var response = await SendAsync(caller, HttpMethod.Post, "Admin/WebhookTest", null, ct);
    }

    private async Task<T> ReadAsync<T>(CallerSession caller, string route, CancellationToken ct)
    {
        using var response = await SendAsync(caller, HttpMethod.Get, route, null, ct);
        using var doc = await ReadJsonAsync(response, ct);
        try { return doc.RootElement.Deserialize<T>(SecurityJson.Options) ?? throw new SecurityFailure("unavailable", 503); }
        catch (JsonException) { throw new SecurityFailure("unavailable", 503); }
    }

    private async Task<HttpResponseMessage> SendAsync(CallerSession caller, HttpMethod method, string route, object? payload, CancellationToken ct)
    {
        if (!compatibility.Check().IsOk) throw new SecurityFailure("security_incompatible", 409);
        if (caller.UserId == Guid.Empty || string.IsNullOrWhiteSpace(caller.AccessToken))
            throw new SecurityFailure("unauthorized", 401);
        using var request = new HttpRequestMessage(method, new Uri(caller.ServerBaseUri, "TwoFactorAuth/" + route));
        request.Headers.Authorization = new AuthenticationHeaderValue("MediaBrowser", "Token=\"" + caller.AccessToken + "\"");
        request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (payload is not null) request.Content = JsonContent.Create(payload, options: SecurityJson.Options);
        HttpResponseMessage response;
        try { response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct); }
        catch (HttpRequestException) { throw new SecurityFailure("unavailable", 503, true); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new SecurityFailure("unavailable", 503, true); }
        if (response.IsSuccessStatusCode) return response;
        using (response)
        {
            using var doc = await ReadJsonAsync(response, ct, tolerateInvalid: true);
            var status = (int)response.StatusCode;
            var code = SecurityJson.ErrorCode(status, doc.RootElement);
            var retry = response.Headers.RetryAfter?.Delta?.TotalSeconds;
            throw new SecurityFailure(code, status is >= 500 or >= 300 and < 400 ? 503 : status,
                status >= 500, retry is > 0 ? (int)Math.Min(retry.Value, 86400) : null);
        }
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct, bool tolerateInvalid = false)
    {
        const int limit = 4 * 1024 * 1024;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var buffer = new MemoryStream();
            var bytes = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(bytes, deadline.Token)) > 0)
            {
                if (buffer.Length + read > limit) throw new SecurityFailure("unavailable", 503);
                buffer.Write(bytes, 0, read);
            }
            return JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
        }
        catch (JsonException) when (tolerateInvalid) { return JsonDocument.Parse("{}"); }
        catch (JsonException) { throw new SecurityFailure("unavailable", 503); }
        catch (IOException) { throw new SecurityFailure("unavailable", 503, true); }
        catch (HttpRequestException) { throw new SecurityFailure("unavailable", 503, true); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new SecurityFailure("unavailable", 503, true); }
    }

    // Prevent URI dot-segment normalization / encoded-slash routing to another upstream operation.
    internal static bool ValidRecordId(string? id) => id is { Length: > 0 and <= 256 }
        && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
