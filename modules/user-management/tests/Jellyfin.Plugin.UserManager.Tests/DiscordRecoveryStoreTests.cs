using Jellyfin.Plugin.UserManager.Data;
using Jellyfin.Plugin.UserManager.Services;
using Xunit;

namespace Jellyfin.Plugin.UserManager.Tests;

public sealed class DiscordRecoveryStoreTests
{
    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private static ResetTokenRecord Token(string hash) => new() { TokenHash = hash, JellyfinUserId = "user", CreatedByUserId = "user", CreatedAt = Now, ExpiresAt = Now + 300000 };
    private static async Task Link(PluginStore store, string discord = "discord")
    {
        var code = Guid.NewGuid().ToString("N");
        await store.SetLinkCodeAsync(new() { CodeHash = code, JellyfinUserId = "user", CreatedAt = Now, ExpiresAt = Now + 300000 });
        Assert.NotNull(await store.ConsumeLinkCodeAsync(code, discord, "Test", Now));
    }

    [Fact] public async Task UnlinkRevokesUnusedRecoveryButRetainsClaimsAndPendingSessionRevocation()
    {
        using var folder = new TempFolder(); var store = new PluginStore(folder.Path); await Link(store);
        await store.CreateResetTokenAsync(Token("claimed"), expectedDiscordUserId: "discord");
        Assert.NotNull(await store.ConsumeResetTokenAsync("claimed", Now));
        await store.CreateResetTokenAsync(Token("unused"), expectedDiscordUserId: "discord");
        await store.DeleteLinkAsync("user");
        var reopened = new PluginStore(folder.Path);
        Assert.Null(await reopened.GetLinkByJellyfinUserIdAsync("user"));
        Assert.Null(await reopened.GetResetTokenAsync("unused"));
        Assert.Equal("claimed", (await reopened.GetResetTokenAsync("claimed"))!.Stage);
        Assert.Single(await reopened.PendingSessionRevocationsAsync());
        await Assert.ThrowsAsync<UserManagerException>(() => reopened.CreateResetTokenAsync(Token("must-not-issue"), expectedDiscordUserId: "discord"));
        Assert.Null(await reopened.GetResetTokenAsync("must-not-issue"));
    }

    [Fact] public async Task WrongOrReplacedDiscordLinkCannotIssueRecoveryOrEraseCurrentCredential()
    {
        using var folder = new TempFolder(); var store = new PluginStore(folder.Path); await Link(store);
        await store.CreateResetTokenAsync(Token("valid"), expectedDiscordUserId: "discord");
        await Assert.ThrowsAsync<UserManagerException>(() => store.CreateResetTokenAsync(Token("wrong"), expectedDiscordUserId: "another"));
        Assert.NotNull(await store.GetResetTokenAsync("valid"));
        await Link(store, "replacement");
        Assert.Null(await store.GetResetTokenAsync("valid"));
        await Assert.ThrowsAsync<UserManagerException>(() => store.CreateResetTokenAsync(Token("old-owner"), expectedDiscordUserId: "discord"));
        await store.CreateResetTokenAsync(Token("new-owner"), expectedDiscordUserId: "replacement");
    }

    [Fact] public async Task FailedDeliveryCleanupNeverErasesAnAlreadyClaimedPasswordEffect()
    {
        using var folder = new TempFolder(); var store = new PluginStore(folder.Path);
        await store.CreateResetTokenAsync(Token("unused"));
        await store.RevokeUnusedResetTokenAsync("unused"); Assert.Null(await store.GetResetTokenAsync("unused"));
        await store.CreateResetTokenAsync(Token("claimed"));
        await store.ConsumeResetTokenAsync("claimed", Now);
        await store.RevokeUnusedResetTokenAsync("claimed");
        Assert.Equal("claimed", (await store.GetResetTokenAsync("claimed"))!.Stage);
    }
}
