using AccountHub.Core;
using DiscordBridge.Api;
using Jellyfin.Plugin.UserManager.Services;
using Microsoft.Extensions.Hosting;

namespace AccountHub;

public sealed class InviteDeliveryWorker(HubStore store, InviteOnboardingService invites, DiscordBridgeClient bridge, PluginSettingsService settings) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (store.Ready) await invites.DeliverPendingAsync(async (destination, token, ct) =>
                {
                    var config = await settings.GetAsync(ct);
                    if (!Uri.TryCreate(config.PublicBaseUrl, UriKind.Absolute, out var baseUrl) || baseUrl.Scheme != "https") return false;
                    var link = baseUrl.ToString().TrimEnd('/') + "/AccountHub/invite#token=" + token;
                    var reply = await bridge.CallAsync("send.secret", new { destination, directMessage = true, text = "Accept your Jellyfin invitation: " + link, expiresAt = DateTimeOffset.UtcNow.AddMinutes(5) }, ct);
                    return reply is { } response && response.GetProperty("ok").GetBoolean();
                }, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch { /* Secret-bearing transport exceptions must never be logged. Durable delivery status records uncertainty. */ }
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }
}
