using AccountHub.Api;
using DeletionQueue.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DeletionQueue;

public sealed class HubDispatcher(QueueRuntime runtime, AccountHubClient client, HubAvailability availability,
    TimeProvider clock, ILogger<HubDispatcher> logger, NotificationsClient notifications) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield(); var warned = false;
        while (!stoppingToken.IsCancellationRequested && runtime.Available)
        {
            try
            {
                var settings = runtime.Read().Hub;
                availability.Set(settings.HubIntegrationEnabled && await notifications.ReadyForRegisteredTypesAsync(stoppingToken) && await notifications.ReadyForUpsertAsync(stoppingToken));
                if (!availability.Ready && !warned)
                { logger.LogInformation("Notifications absent, outdated or unavailable; existing requests remain accessible through the queue."); warned = true; }
                if (availability.Ready) warned = false;
                var now = clock.GetUtcNow();
                var due = runtime.Read().Outbox.Where(e => e.Status == "pending" && e.NextAttemptAt <= now ||
                    e.Status == "delivering" && e.LeaseUntil <= now).Take(10).Select(e => e.EventId).ToArray();
                if (due.Length > 0)
                {
                    var lease = Guid.NewGuid();
                    var batch = runtime.Write(Guid.NewGuid(), "hub-delivery", "HubDeliveryLeased", state =>
                    {
                        var events = state.Outbox.Where(e => due.Contains(e.EventId) &&
                            (e.Status == "pending" || e.Status == "delivering" && e.LeaseUntil <= now)).ToArray();
                        foreach (var e in events) { e.Status = "delivering"; e.LeaseId = lease; e.LeaseUntil = now.AddMinutes(5); }
                        return events;
                    });
                    var results = new List<(Guid Id, string Result)>();
                    foreach (var e in batch)
                    {
                        var enabled = settings.HubIntegrationEnabled && (e.Kind == "audit" ? settings.PublishAudit :
                            settings.NotificationMode is NotificationMode.Auto or NotificationMode.Hub);
                        using var payload = System.Text.Json.JsonDocument.Parse(e.PayloadJson);
                        var upsert = e.Kind == "notification" && payload.RootElement.TryGetProperty("adminOnly", out var adminOnly) && (!adminOnly.GetBoolean()||payload.RootElement.TryGetProperty("type",out var type)&&type.GetString()=="proposal.approval_required");
                        var result = enabled && availability.Ready ? await client.PublishWithUpdateAsync(e.Kind, e.PayloadJson, stoppingToken, upsert) : "unavailable";
                        // Disabled/local changes abandon pending notifications locally, never republish local history.
                        if (!enabled && e.Kind == "notification") result = "rejected";
                        results.Add((e.EventId, result));
                    }
                    runtime.Write(Guid.NewGuid(), "hub-delivery", "HubDeliveryAcknowledged", state =>
                    { foreach (var (id, result) in results) HubIntegration.Complete(state, id, lease, result, clock.GetUtcNow()); return true; });
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) { availability.Set(false); } // Durable expired leases recover; no domain rollback/retry.
            try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
        availability.Set(false);
    }
}
