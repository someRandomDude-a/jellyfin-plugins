using AccountHub.Core;
using AccountHub.Notifications;
using Jellyfin.Plugin.UserManager.Data;
using Jellyfin.Plugin.UserManager.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AccountHub;

public interface IHubMaintenance
{
    BackgroundService CreateDeliveryWorker();
    Task RefreshSecurityAsync(CancellationToken ct);
    Task RevokePendingSessionsAsync(CancellationToken ct);
    Task PruneAuditAsync(CancellationToken ct);
}

/// <summary>Lazy module resolution keeps lifecycle outages independent of notification startup.</summary>
public sealed class HubMaintenance(IServiceProvider services, HubStore store) : IHubMaintenance
{
    public BackgroundService CreateDeliveryWorker()
    {
        if (!services.GetRequiredService<NotificationStore>().Ready) throw new HubException("unavailable", "Notification storage unavailable", true);
        return ActivatorUtilities.CreateInstance<DeliveryWorker>(services);
    }
    public async Task RefreshSecurityAsync(CancellationToken ct)
    {
        var host = services.GetRequiredService<HubHost>();
        var settings = await host.ReadSettingsAsync(ct);
        var secret = await store.ReadSecretAsync("security.webhookSecret", ct);
        host.SecurityAlertMode = settings.Settings.AlertMode;
        host.SecurityWebhookSecret = secret;
    }
    public async Task RevokePendingSessionsAsync(CancellationToken ct)
    {
        var lifecycle = services.GetRequiredService<PluginStore>();
        await new ResetOperations(lifecycle).RevokePendingAsync(
            userId => services.GetRequiredService<JellyfinUserService>().RevokeSessionsAsync(userId), ct);
    }
    public Task PruneAuditAsync(CancellationToken ct) => store.PruneAuditAsync(Guid.Empty, ct);
}
