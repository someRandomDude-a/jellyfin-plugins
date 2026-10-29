using System.Text.Json;
using Jellyfin.LibraryNotifications;
using Jellyfin.Plugin.DiscordBridge;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DiscordBridge.Tests;

public sealed class LibraryNoticeTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "opencode", "library-notice-tests", Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(directory, "bridge.db");
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1792800000);
    private static LibraryNotice Notice() => new(Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid()], "episode", "New episode available", "Disposable series · Season 1, episode 2", Now, []);
    [Fact]public void SharedBridgeIntakeRejectsAllPrivateAudienceArraysBeforeStorage(){
        var receiver=new BridgeLibraryNotices(null!);var user=Guid.NewGuid();
        foreach(var notice in new[]{Notice() with{Audience=[user]},Notice() with{FavouriteAudience=[user]},Notice() with{RequesterAudience=[user]},Notice() with{EnrichmentVersion=2,WatchlistedAudience=[user]}})
            Assert.Throws<ArgumentException>(()=>receiver.Accept(notice));
    }
    [Theory]
    [InlineData(true, false, false, true)]
    [InlineData(false, true, true, false)]
    [InlineData(true, true, true, true)]
    public void NativeWatchlistAndFavouriteFlagsRemainIndependent(bool likes, bool favourite, bool expectedFavourite, bool expectedWatchlisted) {
        var data = new MediaBrowser.Controller.Entities.UserItemData { Key = "native-interest-fixture", Likes = likes, IsFavorite = favourite };
        var interests = LibraryNoticeFactory.Interests(data, null);
        Assert.Equal(expectedFavourite, interests.Favourite); Assert.Equal(expectedWatchlisted, interests.Watchlisted);
        Assert.Equal(interests, LibraryNoticeFactory.Interests(null, data));
        Assert.Equal((false, false), LibraryNoticeFactory.Interests(null, null));
    }
    [Fact] public void NewWatchlistEnrichmentSurvivesRestartWithoutReinterpretingOlderNotices() {
        var store = new LibraryNoticeStore(Database); var user = Guid.NewGuid();
        var notice = Notice() with { EnrichmentVersion = 2, Audience = [user], WatchlistedAudience = [user] };
        Assert.True(store.Capture(notice.ItemId, notice.Kind, Now, notice));
        var frozen = Assert.Single(new LibraryNoticeStore(Database).Due(Now)).Notice!;
        Assert.Equal(2, frozen.EnrichmentVersion); Assert.Equal(user, Assert.Single(frozen.WatchlistedAudience!));
        Assert.Throws<ArgumentException>(() => LibraryNoticeFactory.Validate(notice with { EnrichmentVersion = 1 }));
        Assert.Throws<ArgumentException>(() => LibraryNoticeFactory.Validate(notice with { WatchlistedAudience = [Guid.Empty] }));
        LibraryNoticeFactory.Validate(Notice() with { EnrichmentVersion = 1, FavouriteAudience = [user] });
    }
    [Fact]
    public void IntakeIsDelayedDeduplicatedAndDurableAcrossRestart()
    {
        var store = new LibraryNoticeStore(Database); var id = Guid.NewGuid();
        Assert.True(store.Capture(id, "episode", Now)); Assert.True(store.Capture(id, "episode", Now));
        Assert.Empty(store.Due(Now.AddSeconds(29)));
        var job = Assert.Single(new LibraryNoticeStore(Database).Due(Now.AddSeconds(30)));
        Assert.Equal(id, job.ItemId); Assert.Null(job.Notice);
        var notice = Notice() with { Id = job.Id, ItemId = id };
        store.Prepare(job.Id, notice); store.InboxDone(job.Id); store.DiscordDone(job.Id);
        Assert.Empty(new LibraryNoticeStore(Database).Due(Now.AddMinutes(5)));
        Assert.True(store.Capture(id, "episode", Now.AddMinutes(5)));
        Assert.Empty(store.Due(Now.AddMinutes(6)));
    }
    [Fact]
    public void ForwardedNoticeIsReadyImmediatelyAndFrozen()
    {
        var store = new LibraryNoticeStore(Database); var notice = Notice();
        Assert.True(store.Capture(notice.ItemId, notice.Kind, Now, notice));
        store.Prepare(notice.Id, notice with { Body = "changed metadata" });
        var job = Assert.Single(store.Due(Now)); Assert.Equal(notice.Body, job.Notice!.Body);
        Assert.Throws<ArgumentException>(() => store.Capture(Guid.NewGuid(), notice.Kind, Now, notice));
        Assert.Throws<ArgumentException>(() => store.Prepare(Guid.NewGuid(), notice));
    }
    [Fact]
    public void InterruptedAndFailedDiscordSendsAreNeverAutomaticallyReplayed()
    {
        var store = new LibraryNoticeStore(Database); var notice = Notice(); store.Capture(notice.ItemId, notice.Kind, Now, notice);
        store.InboxDone(notice.Id); store.DiscordClaim(notice.Id);
        var restarted = new LibraryNoticeStore(Database); Assert.Empty(restarted.Due(Now.AddDays(1)));
        var health = JsonSerializer.SerializeToElement(restarted.Health()); Assert.Equal(1, health.GetProperty("uncertainDiscord").GetInt64());
        var second = Notice(); restarted.Capture(second.ItemId, second.Kind, Now, second);
        restarted.InboxDone(second.Id); restarted.DiscordUncertain(second.Id);
        Assert.Empty(restarted.Due(Now.AddDays(1)));
    }
    [Fact]
    public void RetryBudgetIsBoundedAndOtherItemsCanProceed()
    {
        var store = new LibraryNoticeStore(Database); var notice = Notice(); store.Capture(notice.ItemId, notice.Kind, Now, notice);
        for (var attempt = 0; attempt < 10; attempt++) store.Defer(notice.Id, Now);
        Assert.Empty(store.Due(Now.AddMinutes(2)));
        var other = Notice(); store.Capture(other.ItemId, other.Kind, Now, other);
        Assert.Equal(other.Id, Assert.Single(store.Due(Now.AddMinutes(2))).Id);
        Assert.Equal(1, JsonSerializer.SerializeToElement(store.Health()).GetProperty("failed").GetInt64());
    }
    [Fact]
    public void OptInTimestampsAreServerOwnedAndExcludeOldAdditions()
    {
        var store = new LibraryNoticeStore(Database); Assert.False(store.Settings().Enabled);
        store.SaveSettings(new(0, true, EnabledSince: Now));
        var settings = store.Settings(); Assert.NotNull(settings.EnabledSince); Assert.NotEqual(Now, settings.EnabledSince);
        Assert.False(settings.Includes(Notice() with { OccurredAt = settings.EnabledSince!.Value.AddSeconds(-1) }));
        Assert.True(settings.Includes(Notice() with { OccurredAt = settings.EnabledSince!.Value.AddSeconds(1) }));
        Assert.Throws<InvalidOperationException>(() => store.SaveSettings(new(0, true)));
        store.SaveSettings(settings with { Enabled = false }); Assert.Null(store.Settings().EnabledSince);
        store.SaveSettings(store.Settings() with { Enabled = true }); Assert.NotNull(store.Settings().EnabledSince);
    }
    [Fact]
    public void InboxKindsAreIndependentAndUnknownKindsAreDenied()
    {
        var config = new LibraryNoticeSettings(Enabled: true, Episodes: false, EnabledSince: Now);
        Assert.False(config.Includes(Notice())); Assert.True(config.Includes(Notice() with { Kind = "movie" }));
        Assert.False(config.Includes(Notice() with { Kind = "unknown" }));
    }
    private static Configuration Config(LibraryNotice notice) => new()
    {
        LibraryAnnouncementsEnabled = true, LibraryDisclosureAcknowledged = true, LibraryEnabledSince = Now,
        LibraryIds = notice.LibraryIds, AllowedChannelIds = ["1234567890"], LibraryChannelIds = ["1234567890"]
    };
    [Fact]
    public void DiscordScopeRequiresExplicitConsentLibraryChannelAndOptInTime()
    {
        var notice = Notice(); var config = Config(notice); BridgeStore.Validate(config); Assert.True(BridgeLibraryNotices.InScope(config, notice));
        Assert.False(BridgeLibraryNotices.InScope(config, notice with { OccurredAt = Now.AddSeconds(-1) }));
        Assert.False(BridgeLibraryNotices.InScope(config, notice with { LibraryIds = [Guid.NewGuid()] }));
        config.LibraryEpisodes = false; Assert.False(BridgeLibraryNotices.InScope(config, notice)); config.LibraryEpisodes = true;
        config.LibraryDisclosureAcknowledged = false; Assert.False(BridgeLibraryNotices.InScope(config, notice)); Assert.Throws<ArgumentException>(() => BridgeStore.Validate(config));
        config.LibraryDisclosureAcknowledged = true; config.AllowedChannelIds = []; Assert.Throws<ArgumentException>(() => BridgeStore.Validate(config)); Assert.False(BridgeLibraryNotices.InScope(config, notice));
        config.AllowedChannelIds = config.LibraryChannelIds; config.LibraryIds = []; Assert.Throws<ArgumentException>(() => BridgeStore.Validate(config));
    }
    [Fact]
    public void BridgeEnableTimestampCannotBeForgedOrBackfilled()
    {
        var bridge = new BridgeStore(directory); var notice = Notice(); var config = Config(notice);
        bridge.Save(config, 0, null, false); var first = bridge.Read().Settings.LibraryEnabledSince;
        Assert.NotNull(first); Assert.NotEqual(Now, first);
        bridge.Save(Config(notice), 1, null, false); Assert.Equal(first, bridge.Read().Settings.LibraryEnabledSince);
        bridge.Save(new Configuration(), 2, null, false); Assert.Null(bridge.Read().Settings.LibraryEnabledSince);
    }
    [Fact]
    public void LibrarySchemaDoesNotChangeOwnersUserVersion()
    {
        Directory.CreateDirectory(directory);
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database, Pooling = false }.ToString()); db.Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "PRAGMA user_version=27"; cmd.ExecuteNonQuery(); _ = new LibraryNoticeStore(Database);
        cmd.CommandText = "PRAGMA user_version"; Assert.Equal(27L, cmd.ExecuteScalar());
    }
    [Fact]
    public async Task ConcurrentNativeAndForwardedCaptureIsOneItem()
    {
        var store = new LibraryNoticeStore(Database); var notice = Notice();
        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(() => store.Capture(notice.ItemId, notice.Kind, Now, notice))));
        Assert.Single(store.Due(Now));
    }
    [Fact]
    public void InvalidNoticeIdentifiersAreRejected()
    {
        Assert.Throws<ArgumentException>(() => LibraryNoticeFactory.Validate(Notice() with { LibraryIds = [Guid.Empty] }));
        Assert.Throws<ArgumentException>(() => LibraryNoticeFactory.Validate(Notice() with { Audience = [Guid.Empty] }));
        Assert.Throws<ArgumentException>(() => LibraryNoticeFactory.Validate(Notice() with { Kind = "folder" }));
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
}
