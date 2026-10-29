using Xunit;

namespace Jellyfin.Plugin.PrivateSyncPlay.Tests;

public sealed class WatchPartyNoticeTests
{
    [Theory]
    [InlineData("party.created","created")]
    [InlineData("party.started","started")]
    [InlineData("party.ended","ended")]
    [InlineData("party.invite.created","created")]
    [InlineData("party.invite.revoked","revoked")]
    [InlineData("party.guest.joined","joined")]
    [InlineData("party.guest.left","left")]
    [InlineData("party.guest.revoked","ended")]
    [InlineData("party.moderation","changed")]
    [InlineData("party.settings.changed","changed")]
    public void KnownEventsGiveActionSpecificUsefulTextWithoutLeakingArbitraryReasons(string type,string expected)
    {
        var text=WatchPartyNotice.Text(type,"Guest 4","sensitive-provider-failure");
        Assert.Contains(expected,text.Title,StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("event occurred",text.Body);Assert.DoesNotContain("sensitive-provider-failure",text.Body);
        Assert.True(text.Body.Length>30);
    }
    [Fact] public void GuestLabelsAreBoundedAndControlCharactersRemoved()
    {
        var text=WatchPartyNotice.Text("party.guest.joined","Guest\n"+new string('x',200));
        Assert.DoesNotContain('\n',text.Body);Assert.True(text.Body.Length<160);
        Assert.Contains("expired",WatchPartyNotice.Text("party.guest.left","Guest","lease_ended").Body);
    }
}
