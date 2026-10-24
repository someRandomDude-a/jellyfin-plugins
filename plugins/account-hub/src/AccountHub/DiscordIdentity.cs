using System.Text.Json;
using AccountHub.Core;
using DiscordBridge.Api;
using Jellyfin.Plugin.UserManager.Data;
using Jellyfin.Plugin.UserManager.Services;

namespace AccountHub;

public sealed class DiscordIdentity(PluginStore lifecycle, DiscordLinkService links, AccountService accounts, PluginSettingsService settings, HubHost host, DiscordBridgeClient bridge)
{
    public async Task<string> DispatchAsync(string request, CancellationToken ct)
    {
        try
        {
            if (request.Length > 4096) throw new ArgumentException();
            using var doc = JsonDocument.Parse(request);
            var root = doc.RootElement;
            if (root.GetProperty("apiVersion").GetString() != "1") throw new ArgumentException();
            var discord = root.GetProperty("discordId").GetString()!;
            if (!ulong.TryParse(discord, out var snowflake) || snowflake == 0) throw new ArgumentException();
            var operation = root.GetProperty("operation").GetString();
            var config = await settings.GetAsync(ct);
            if (operation == "link.prepare")
            {
                if (!Uri.TryCreate(config.PublicBaseUrl, UriKind.Absolute, out var url) || url.Scheme != "https" || url.UserInfo.Length != 0 || url.Query.Length != 0 || url.Fragment.Length != 0) throw new InvalidOperationException();
                var token = await links.PrepareAsync(discord, ct);
                return Reply(new { message = "Sign in to Jellyfin and explicitly confirm this five-minute link:\n" + config.PublicBaseUrl!.TrimEnd('/') + "/AccountHub/discord/link#token=" + token });
            }
            var linked = await lifecycle.GetLinkByDiscordUserIdAsync(discord, ct);
            if (linked is null || !Guid.TryParse(linked.JellyfinUserId, out var user) || !await host.UserExistsAsync(user, ct)) throw new UnauthorizedAccessException();
            if (operation == "identity") return Reply(new { userId = user });
            if (operation == "status") return Reply(new { message = "Your Discord account has a verified, enabled Jellyfin link." });
            if (operation == "unlink")
            {
                await accounts.UnlinkAsync(user, user.ToString("N"), "Discord self-service", null, ct);
                return Reply(new { message = "Your Discord account was unlinked." });
            }
            if (operation == "reset")
            {
                var security = (await host.ReadSettingsAsync(ct)).Settings;
                if (security.PasswordLoginPolicy == "local_disabled" || string.IsNullOrEmpty(config.PublicBaseUrl)) throw new UnauthorizedAccessException();
                var reset = await accounts.CreateResetTokenAsync(user, user, config.PublicBaseUrl, TimeSpan.FromMinutes(5), ct);
                var delivered = await bridge.CallAsync("send.secret", new { destination = discord, directMessage = true, text = "Your one-time Jellyfin password reset (five minutes):\n" + reset.Url.Replace("/UserManager/reset", "/AccountHub/reset", StringComparison.Ordinal), expiresAt = reset.ExpiresAt }, ct);
                if (delivered is not { } response || !response.GetProperty("ok").GetBoolean()) throw new InvalidOperationException();
                return Reply(new { message = "Your recovery link was sent privately. It preserves 2FA and does not sign you in automatically." });
            }
            throw new ArgumentException();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return JsonSerializer.Serialize(new { apiVersion = "1", ok = false, error = new { code = "unavailable", retryable = false } }); }
    }
    private static string Reply(object data) => JsonSerializer.Serialize(new { apiVersion = "1", ok = true, data });
}
