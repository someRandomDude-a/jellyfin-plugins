using Discord;
using Jellyfin.LibraryNotifications;

namespace Jellyfin.Plugin.DiscordBridge;

/// <summary>Explicit media-only shared-channel projection, never a serialized domain/user object.</summary>
public static class LibraryDiscordCard
{
    public static Embed Create(LibraryNotice notice)
    {
        LibraryNoticeFactory.Validate(notice);
        // Even a caller with a recipient list cannot turn this into a public user disclosure.
        // No author, avatar, footer identity, item/library IDs, images, URLs or bearer links.
        return new EmbedBuilder()
            .WithTitle(notice.Kind == "episode" ? "New episode available" : "New movie available")
            .WithDescription(Plain(notice.Body))
            .WithColor(new Color(0x43B581))
            .WithTimestamp(notice.OccurredAt)
            .AddField("Content", notice.Kind == "episode" ? "Episode" : "Movie", inline: true)
            .AddField("Status", "Available", inline: true)
            .Build();
    }
    private static string Plain(string value) => string.Concat(value.Select(c => c == '@' ? "＠" : "\\`*_~|[]()<>".Contains(c) ? "\\" + c : c.ToString()));
}
