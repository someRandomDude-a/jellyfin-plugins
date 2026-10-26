using AccountHub.Api;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AccountHub.Notifications;

public sealed class DeliveryWorker(NotificationStore store, NotificationService service, INotificationHost host,
    IDiscordTransport discord, WebhookSender webhook, TimeProvider time, ILogger<DeliveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextPrune = time.GetUtcNow().AddDays(1);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var receipt in store.PendingReceipts())
                {
                    await ProcessReceiptAsync(receipt, stoppingToken);
                }
                for (var i = 0; i < 20; i++)
                {
                    var delivery = store.Claim();
                    if (delivery is null) break;
                    var result = await DeliverAsync(delivery, stoppingToken);
                    store.Complete(delivery, result);
                }
                await new WebPush(store,host,time).ProcessAsync(stoppingToken);
                if (time.GetUtcNow() >= nextPrune)
                {
                    await host.AppendAuditAsync(new(1, Guid.NewGuid(), "account-hub", "notifications.retention.pruning", "success", time.GetUtcNow()), stoppingToken);
                    store.PrunePresentation(store.Settings().NotificationDays);
                    nextPrune = time.GetUtcNow().AddDays(1);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) { logger.LogWarning("Notification work deferred; inspect module/database health. No transport payload is logged."); }
            await Task.Delay(TimeSpan.FromSeconds(5), time, stoppingToken);
        }
    }
    public async Task ProcessReceiptAsync((string Hash, NotificationEvent? Event, AuditEvent? Audit) receipt, CancellationToken ct)
    {
        // Neither stream's outage blocks the other's acknowledgment or existing external deliveries.
        if (receipt.Event is { } entry)
        {
            try
            {
                await service.PublishAsync(entry, Validation.Hash(Contract.Serialize(entry)), ct);
                store.AcknowledgeReceipt(receipt.Hash, audit: false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning("Notification receipt fanout deferred."); }
        }
        if (receipt.Audit is { } audit)
        {
            try
            {
                await host.AppendAuditAsync(audit, ct);
                store.AcknowledgeReceipt(receipt.Hash, audit: true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning("Notification receipt audit deferred."); }
        }
    }
    public async Task<SendResult> DeliverAsync(Delivery delivery, CancellationToken ct)
    {
        if (!await host.UserExistsAsync(delivery.UserId, ct) || (delivery.AdminOnly && !await host.IsAdministratorAsync(delivery.UserId, ct)))
            return new(false, false, "recipient_revoked");
        if (!await host.CanAccessNotificationAsync(delivery.UserId, delivery.Event, ct)) return new(false, false, "library_access_revoked");
        var current = store.Settings(false).Routes.FirstOrDefault(r => r.Id == delivery.Route.Id);
        if (current is null || current.Channel != delivery.Route.Channel || current.DirectMessage != delivery.Route.DirectMessage ||
            (!current.DirectMessage && current.Destination != delivery.Route.Destination)) return new(false, false, "route_revoked");
        if (delivery.Route.DirectMessage)
        {
            var link = await host.DiscordLinkAsync(delivery.UserId, ct);
            if (link is null || link != delivery.DiscordLink || link != delivery.Route.Destination) return new(false, false, "link_revoked");
            if (!delivery.AdminOnly && delivery.Event.Severity != "critical" && !NotificationService.Enabled(store.Preferences(delivery.UserId), delivery.Event, "discord", false))
                return new(false, false, "opt_out");
        }
        // Recheck policy and use the latest secret; immutable ID/body remain unchanged.
        delivery = delivery with { Route = delivery.Route with { Secret = current.Secret, AllowHttp = current.AllowHttp, AllowPrivate = current.AllowPrivate } };
        if (delivery.Route.Channel == "webhook") return await webhook.SendAsync(delivery, ct);
        var text = delivery.Route.DirectMessage ? delivery.Event.Title + "\n" + delivery.Event.Body : "Account Hub notification. Open Account Hub to review.";
        return await discord.SendAsync(delivery.Route.Destination, delivery.Route.DirectMessage, text, delivery.Id.ToString(), ct);
    }
}
