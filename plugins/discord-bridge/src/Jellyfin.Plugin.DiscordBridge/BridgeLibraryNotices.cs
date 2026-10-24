using DiscordBridge.Api;
using Jellyfin.LibraryNotifications;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.DiscordBridge;

public sealed class BridgeLibraryNotices(IServiceProvider services) : ILibraryNoticeHost
{
    private Configuration Settings => services.GetRequiredService<BridgeStore>().Read().Settings;
    public bool Ready => true;
    public bool CaptureLocally => Settings.LibraryAnnouncementsEnabled && services.GetKeyedService<Func<string, CancellationToken, Task<string>>>(DiscordBridgeClient.IdentityKey) is null;
    public bool IncludeAudience => false;
    public bool DirectDiscord => true;
    public bool DiscordConnected => services.GetRequiredService<BridgeRuntime>().Connected;
    public Task<bool> InboxAsync(LibraryNotice notice, CancellationToken ct) => Task.FromResult(true);
    public bool Accept(LibraryNotice notice)
    {
        LibraryNoticeFactory.Validate(notice);
        if (notice.Audience.Length != 0) throw new ArgumentException("Shared announcements must not contain Jellyfin audience identities.");
        var config = Settings;
        if (!InScope(config, notice)) return true; // Deliberately disabled/unapproved, no backfill on later enable.
        return services.GetRequiredService<LibraryNoticeStore>().Capture(notice.ItemId, notice.Kind, notice.OccurredAt, notice);
    }
    public static bool InScope(Configuration config, LibraryNotice notice) => config.LibraryAnnouncementsEnabled && config.LibraryDisclosureAcknowledged &&
        config.LibraryEnabledSince is { } since && notice.OccurredAt >= since && notice.LibraryIds.Any(config.LibraryIds.Contains) &&
        config.LibraryChannelIds.Any(config.AllowedChannelIds.Contains) &&
        (notice.Kind == "episode" ? config.LibraryEpisodes : config.LibraryMovies);
    public async Task<bool> DiscordAsync(LibraryNotice notice, CancellationToken ct)
    {
        var config = Settings;
        if (!InScope(config, notice)) return true;
        // Recheck the live item/library scope: deleted or moved media must not be announced
        // using the library membership frozen at intake.
        var library = services.GetRequiredService<MediaBrowser.Controller.Library.ILibraryManager>();
        var item = library.GetItemById(notice.ItemId);
        if (item is null || LibraryNoticeFactory.Kind(item) != notice.Kind ||
            !library.GetCollectionFolders(item).Any(folder => config.LibraryIds.Contains(folder.Id))) return true;
        var runtime = services.GetRequiredService<BridgeRuntime>();
        var card = LibraryDiscordCard.Create(notice);
        foreach (var channel in config.LibraryChannelIds.Where(config.AllowedChannelIds.Contains).Distinct())
            await runtime.SendAsync(channel, false, notice.Kind == "episode" ? "New episode available" : "New movie available", ct, card);
        return true;
    }
}
