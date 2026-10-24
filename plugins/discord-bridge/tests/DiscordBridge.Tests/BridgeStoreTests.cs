using Jellyfin.Plugin.DiscordBridge;
using Xunit;

namespace DiscordBridge.Tests;

public sealed class BridgeStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "DiscordBridge.Tests", Guid.NewGuid().ToString("N"));
    [Fact]
    public void EmptyOrDisabledSettingsCanRestartWithoutCredentialKeys()
    {
        var first = new BridgeStore(directory); Assert.False(first.Read().Settings.Enabled);
        first.Save(new Configuration(), 0, null, false);
        var next = new BridgeStore(directory); Assert.Equal(1, next.Read().Revision); Assert.Null(next.Read().Token);
    }
    [Fact]
    public void MissingDatabaseDoesNotResetSettingsOrInteractionHistoryEvenWithoutBotKeys()
    {
        var store = new BridgeStore(directory); Assert.True(store.Claim("1234567890"));
        var path = Path.Combine(directory, "discord-bridge.db"); File.Move(path, path + ".preserved");
        Assert.ThrowsAny<Exception>(() => store.Read()); Assert.False(File.Exists(path));
        Assert.Throws<InvalidDataException>(() => new BridgeStore(directory)); Assert.False(File.Exists(path));
    }
    [Fact]
    public void TokenIsEncryptedPreservedAndExplicitlyCleared()
    {
        var store = new BridgeStore(directory); const string token = "disposable-fake-bot-token-123456";
        store.Save(new Configuration(), 0, token, false);
        Assert.Equal(token, new BridgeStore(directory).Read().Token);
        Assert.DoesNotContain(token, System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(directory, "discord-bridge.db"))));
        store.Save(new Configuration(), 1, null, false); Assert.Equal(token, store.Read().Token);
        store.Save(new Configuration(), 2, null, true); Assert.Null(store.Read().Token);
        Assert.Throws<InvalidOperationException>(() => store.Save(new Configuration(), 2, null, false));
    }
    [Fact]
    public void MissingKeyDoesNotGenerateReplacementForCiphertext()
    {
        var store = new BridgeStore(directory); store.Save(new Configuration(), 0, "disposable-fake-bot-token-123456", false);
        foreach (var file in Directory.GetFiles(Path.Combine(directory, "keys"), "key-*.xml")) File.Delete(file);
        Assert.Throws<InvalidDataException>(() => new BridgeStore(directory));
        Assert.Empty(Directory.GetFiles(Path.Combine(directory, "keys"), "key-*.xml"));
    }
    [Fact]
    public void InteractionClaimsAreDurableAndNotReplayable()
    {
        Assert.True(new BridgeStore(directory).Claim("1234567890"));
        Assert.False(new BridgeStore(directory).Claim("1234567890"));
        Assert.Throws<ArgumentException>(() => new BridgeStore(directory).Claim("not-an-interaction"));
    }
    [Theory]
    [InlineData("https://user:password@example.com")]
    [InlineData("https://example.com?token=secret")]
    [InlineData("http://example.com")]
    [InlineData("https://example.com/#token=secret")]
    public void PublicLinksNeedSafeHttpsBase(string url) => Assert.Throws<ArgumentException>(() => BridgeStore.Validate(new Configuration { PublicServerUrl = url }));
    [Fact]
    public void EnabledBotNeedsGuildAndAllowlistIsValidated()
    {
        Assert.Throws<ArgumentException>(() => BridgeStore.Validate(new Configuration { Enabled = true }));
        Assert.Throws<ArgumentException>(() => BridgeStore.Validate(new Configuration { AllowedChannelIds = ["0"] }));
        BridgeStore.Validate(new Configuration { Enabled = true, GuildId = "1234567890", PublicServerUrl = "https://example.com/jellyfin", AllowedChannelIds = ["9876543210"] });
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
}
