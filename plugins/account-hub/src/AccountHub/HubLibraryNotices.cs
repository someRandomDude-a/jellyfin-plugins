using System.Text.Json;
using AccountHub.Core;
using DiscordBridge.Api;
using Jellyfin.LibraryNotifications;
using Microsoft.Extensions.DependencyInjection;

namespace AccountHub;

public sealed class HubLibraryNotices(IServiceProvider services, HubStore hub, DiscordBridgeClient bridge) : ILibraryNoticeHost
{
    public bool Ready => hub.Ready;
    // Do not retain media/recipient details while both destinations are opted out.
    // Only a framework delegate crosses the optional Bridge boundary.
    public bool CaptureLocally => services.GetRequiredService<LibraryNoticeStore>().Settings().Enabled ||
        services.GetKeyedService<Func<bool>>(DiscordBridgeClient.LibraryCaptureKey)?.Invoke() == true;
    public bool IncludeAudience => services.GetRequiredService<LibraryNoticeStore>().Settings().Enabled;
    public bool DirectDiscord => false;
    public bool DiscordConnected => true;
    public async Task<bool> InboxAsync(LibraryNotice notice, CancellationToken ct)
    {
        var settings = services.GetRequiredService<LibraryNoticeStore>().Settings();
        if (!settings.Includes(notice)) return true;
        var batch = 0;
        foreach (var audience in notice.Audience.Chunk(500))
        {
            var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(notice.Id.ToString("D") + ":inbox:" + batch++));
            var entry = new AccountHub.Api.NotificationEvent(1, new Guid(bytes.AsSpan(0, 16)), "account-hub", "library." + notice.Kind + ".added", "info", notice.Title, notice.Body, audience, false, notice.OccurredAt,
                Link: "web/index.html#/details?id=" + notice.ItemId.ToString("D"), Metadata: new Dictionary<string, string> { ["itemId"] = notice.ItemId.ToString("D") });
            var json = AccountHub.Api.Contract.Serialize(entry);
            var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json)));
            await services.GetRequiredService<AccountHub.Notifications.NotificationService>().PublishAsync(entry, hash, ct);
        }
        return true;
    }
    public async Task<bool> DiscordAsync(LibraryNotice notice, CancellationToken ct)
    {
        if (services.GetKeyedService<Func<string, CancellationToken, Task<string>>>(DiscordBridgeClient.ServiceKey) is null) return true;
        // Never forward Jellyfin audience identities, sessions or media paths to a shared-channel transport.
        var reply = await bridge.CallAsync("library.publish", new { notice = notice with { Audience = [] } }, ct);
        return reply is { } value && value.GetProperty("ok").GetBoolean();
    }
}
