using DiscordBridge.Api;
using Jellyfin.Plugin.UserManager.Services;

namespace AccountHub;

public sealed class LifecycleDiscordSender(DiscordBridgeClient client) : IDiscordMessageSender
{
    public bool IsConnected => false; // Actual health is asynchronous; never fabricate a connected status.
    public async Task<bool> SendDirectMessageAsync(string discordUserId, string message, CancellationToken cancellationToken = default)
    {
        var result = await client.CallAsync("send.secret", new { destination = discordUserId, directMessage = true, text = message, expiresAt = DateTimeOffset.UtcNow.AddMinutes(5) }, cancellationToken);
        return result is { } reply && reply.GetProperty("ok").GetBoolean();
    }
}
