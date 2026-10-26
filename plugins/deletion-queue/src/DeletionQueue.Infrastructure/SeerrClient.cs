using System.Net.Http.Json;
using System.Text.Json;
using System.Net;
using System.Net.Sockets;

namespace DeletionQueue.Infrastructure;

/// <summary>Bounded stock-Seerr adapter. No generic forwarding, direct Arr writes or request-history deletion.</summary>
public sealed class SeerrClient : IDisposable
{
    private readonly HttpClient http;

    public SeerrClient(Uri baseUri, string apiKey, HttpMessageHandler? handler = null)
    {
        if (!baseUri.IsAbsoluteUri || baseUri.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(baseUri.UserInfo) || !string.IsNullOrEmpty(baseUri.Query) || !string.IsNullOrEmpty(baseUri.Fragment))
            throw new ArgumentException("A configured HTTP(S) Seerr base URL is required.");
        if (apiKey.Length is 0 or > 512 || apiKey.Any(c => c < 33 || c > 126)) throw new ArgumentException("Invalid API key.");
        http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(3), MaxResponseHeadersLength = 16,
            ConnectCallback = async (context, ct) =>
            {
                var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
                if (addresses.Length == 0 || addresses.Any(a => !EndpointSafety.Allowed(a))) throw new HttpRequestException("Disallowed configured endpoint.");
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                try { await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, ct); return new NetworkStream(socket, ownsSocket: true); }
                catch { socket.Dispose(); throw; }
            }
        })
        { BaseAddress = new Uri(baseUri.AbsoluteUri.TrimEnd('/') + "/api/v1/"), Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
    }

    public async Task<JsonElement> MediaAsync(bool series, int tmdb, CancellationToken ct) =>
        await GetAsync($"{(series ? "tv" : "movie")}/{Positive(tmdb)}", ct);

    public async Task<JsonElement> RequestAsync(int id, CancellationToken ct) => await GetAsync($"request/{Positive(id)}", ct);
    public async Task<JsonElement> RequestsAsync(int skip, CancellationToken ct) => await GetAsync($"request?take=100&skip={Math.Clamp(skip, 0, 10000)}&sort=added", ct);
    // Declining a pending request preserves Seerr history. Never DELETE /request/{id}.
    public async Task DeclineRequestOnceAsync(int id, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"request/{Positive(id)}/decline") { Content = JsonContent.Create(new { }) };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode != HttpStatusCode.OK) throw new HttpRequestException("Seerr request outcome unverified.");
    }
    public async Task<JsonElement> UserAsync(int id, CancellationToken ct) => await GetAsync($"user/{Positive(id)}", ct);
    public async Task<JsonElement> JellyfinSettingsAsync(CancellationToken ct) => await GetAsync("settings/jellyfin", ct);
    public async Task<JsonElement> StatusAsync(CancellationToken ct) => await GetAsync("status", ct);
    public async Task<JsonElement> MediaRecordAsync(int id, CancellationToken ct) => await GetAsync($"media/{Positive(id)}", ct);

    // Exactly one application-level attempt, no redirect/retry loop. Only the file endpoint, never /request or /media/{id}.
    public async Task DeleteFilesOnceAsync(int id, bool is4K, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"media/{Positive(id)}/file?is4k={(is4K ? "true" : "false")}");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode != HttpStatusCode.NoContent) throw new HttpRequestException("Seerr deletion outcome unverified.");
    }

    private async Task<JsonElement> GetAsync(string path, CancellationToken ct)
    {
        // ResponseHeadersRead ends HttpClient.Timeout coverage at the headers.
        // Keep one deadline across DNS/connect, headers, bounded body reads and parsing.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(15)); ct = deadline.Token;
        using var response = await http.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        // Bound external data before parsing; never return raw upstream errors to users.
        await response.Content.LoadIntoBufferAsync(4 * 1024 * 1024, ct);
        return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
    }

    private static int Positive(int id) => id > 0 ? id : throw new ArgumentOutOfRangeException(nameof(id));
    public void Dispose() => http.Dispose();
}
