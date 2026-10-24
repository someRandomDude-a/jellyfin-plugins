using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AccountHub.Api;

namespace AccountHub.Notifications;

/// <summary>REST transport for the Hub-owned bot, not a second bot runtime or command worker.</summary>
public sealed class DiscordBotTransport(HttpClient client, Func<string?> currentBotToken) : IDiscordTransport
{
    public async Task<SendResult> SendAsync(string destination, bool directMessage, string text, string deliveryId, CancellationToken ct)
    {
        if (destination.Length is < 1 or > 30 || !destination.All(char.IsAsciiDigit)) return new(false, false, "invalid_discord_destination");
        var token = currentBotToken();
        if (string.IsNullOrEmpty(token)) return new(false, true, "bot_unavailable");
        try
        {
            var channel = destination;
            if (directMessage)
            {
                using var dm = await Request(HttpMethod.Post, "users/@me/channels", new { recipient_id = destination }, token, ct);
                var error = await Failure(dm, ct); if (error is not null) return error;
                using var document = JsonDocument.Parse(await ReadBounded(dm, ct));
                channel = document.RootElement.GetProperty("id").GetString()!;
                if (channel.Length > 30 || !channel.All(char.IsAsciiDigit)) return new(false, false, "invalid_channel");
            }
            else
            {
                // Validate that this is a reachable guild channel, not a DM accidentally configured as public.
                using var check = await Request(HttpMethod.Get, "channels/" + channel, null, token, ct);
                var error = await Failure(check, ct); if (error is not null) return error;
                using var document = JsonDocument.Parse(await ReadBounded(check, ct));
                var root = document.RootElement;
                if (!root.TryGetProperty("guild_id", out _) || !root.TryGetProperty("type", out var type) || type.GetInt32() is not (0 or 5 or 10 or 11 or 12))
                    return new(false, false, "invalid_guild_channel");
            }
            // Discord enforces recently-used nonce uniqueness only; it is NOT durable exactly-once.
            var nonce = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(deliveryId)).AsSpan(0, 12));
            using var sent = await Request(HttpMethod.Post, "channels/" + channel + "/messages", new
            {
                content = text.Length <= 2000 ? text : text[..1997] + "...",
                allowed_mentions = new { parse = Array.Empty<string>() },
                nonce,
                enforce_nonce = true
            }, token, ct);
            var failure = await Failure(sent, ct); if (failure is not null) return failure;
            using var result = JsonDocument.Parse(await ReadBounded(sent, ct));
            return new(true, MessageId: result.RootElement.GetProperty("id").GetString());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        { return new(false, true, "discord_transport_failure"); }
    }
    private async Task<HttpResponseMessage> Request(HttpMethod method, string path, object? payload, string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, "https://discord.com/api/v10/" + path);
        request.Headers.Authorization = new("Bot", token);
        if (payload is not null) request.Content = JsonContent.Create(payload, options: Contract.Json);
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }
    private static async Task<SendResult?> Failure(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return null;
        var status = (int)response.StatusCode;
        TimeSpan? delay = response.Headers.RetryAfter?.Delta;
        if (status == 429)
        {
            try
            {
                using var document = JsonDocument.Parse(await ReadBounded(response, ct));
                if (document.RootElement.TryGetProperty("retry_after", out var retry) && retry.TryGetDouble(out var seconds) && double.IsFinite(seconds))
                    delay = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, 86400));
            }
            catch (JsonException) { }
        }
        return new(false, status == 429 || status >= 500, "discord_" + status, delay);
    }
    private static async Task<byte[]> ReadBounded(HttpResponseMessage response, CancellationToken ct)
    {
        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream(); var bytes = new byte[2048]; int count;
        while ((count = await stream.ReadAsync(bytes, ct)) > 0)
        {
            if (output.Length + count > 16384) throw new HttpRequestException("Discord response too large");
            output.Write(bytes, 0, count);
        }
        return output.ToArray();
    }
}
