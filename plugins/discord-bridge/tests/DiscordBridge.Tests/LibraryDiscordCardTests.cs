using System.Text.Json;
using Jellyfin.LibraryNotifications;
using Jellyfin.Plugin.DiscordBridge;
using Xunit;

namespace DiscordBridge.Tests;

public sealed class LibraryDiscordCardTests
{
    [Theory]
    [InlineData("episode", "Episode")]
    [InlineData("movie", "Movie")]
    public void SharedCardContainsMediaOnlyAndCannotExposeAudienceOrInjectedTitle(string kind, string label)
    {
        var user = Guid.NewGuid(); var item = Guid.NewGuid(); var library = Guid.NewGuid();
        var notice = new LibraryNotice(Guid.NewGuid(), item, [library], kind, "Requested by private-user", "Disposable media", DateTimeOffset.UtcNow, [user]);
        var card = LibraryDiscordCard.Create(notice);
        Assert.Equal(kind == "episode" ? "New episode available" : "New movie available", card.Title);
        Assert.Equal("Disposable media", card.Description);
        Assert.Null(card.Author); Assert.Null(card.Footer); Assert.Null(card.Url); Assert.Null(card.Image); Assert.Null(card.Thumbnail);
        Assert.Equal(label, card.Fields[0].Value); Assert.Equal("Available", card.Fields[1].Value);
        var serialized = JsonSerializer.Serialize(card);
        foreach (var value in new[] { user.ToString(), item.ToString(), library.ToString(), notice.Id.ToString(), "private-user" }) Assert.DoesNotContain(value, serialized);
    }
    [Fact]
    public void MetadataCannotForgeMentionsOrMarkdownIdentityFields()
    {
        var notice = new LibraryNotice(Guid.NewGuid(), Guid.NewGuid(), [], "movie", "unused", "**Requested by** <@123456789> @everyone", DateTimeOffset.UtcNow, []);
        var card = LibraryDiscordCard.Create(notice);
        Assert.DoesNotContain('@', card.Description); Assert.Contains('＠', card.Description);
        Assert.NotEqual(notice.Body, card.Description); Assert.Equal(2, card.Fields.Length);
    }
}
