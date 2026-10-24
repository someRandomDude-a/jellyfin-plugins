using AccountHub.Notifications;
using DiscordBridge.Api;

namespace AccountHub;

public sealed class DiscordTransport(DiscordBridgeClient client) : IDiscordTransport
{
    public async Task<SendResult> SendAsync(string destination, bool directMessage, string text, string deliveryId, CancellationToken ct)
    {
        var reply = await client.CallAsync("send", new { destination, directMessage, text, deliveryId }, ct);
        if (reply is { } value && value.GetProperty("ok").GetBoolean()) return new(true);
        return new(false, reply is null || reply.Value.GetProperty("error").GetProperty("retryable").GetBoolean(), "discord_bridge_unavailable");
    }
}
