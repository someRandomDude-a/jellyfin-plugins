using AccountHub.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AccountHub;

public sealed class CompositeWorker(IHubMaintenance maintenance, HubStore store, HubWorkerHealth health, TimeProvider clock, ILogger<CompositeWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        BackgroundService? delivery = null;
        health.Set("notifications", "starting");
        health.Set("security-refresh", "starting");
        health.Set("lifecycle-maintenance", "starting");
        health.Set("audit-pruning", "waiting");
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), clock);
            var nextPrune = clock.GetUtcNow().AddDays(1);
            do
            {
                if (!store.Ready)
                {
                    foreach (var name in new[] { "notifications", "security-refresh", "lifecycle-maintenance", "audit-pruning" }) health.Set(name, "unavailable");
                    continue;
                }
                await GuardAsync("notifications", async () =>
                {
                    if (delivery?.ExecuteTask?.IsCompleted == true)
                    {
                        // Observe unexpected termination before replacing this read/delivery worker.
                        try { await delivery.ExecuteTask; }
                        finally { delivery.Dispose(); delivery = null; }
                    }
                    if (delivery is null)
                    {
                        var candidate = maintenance.CreateDeliveryWorker();
                        try { await candidate.StartAsync(stoppingToken); delivery = candidate; }
                        catch { candidate.Dispose(); throw; }
                    }
                }, stoppingToken);
                await GuardAsync("security-refresh", () => maintenance.RefreshSecurityAsync(stoppingToken), stoppingToken);
                await GuardAsync("lifecycle-maintenance", () => maintenance.RevokePendingSessionsAsync(stoppingToken), stoppingToken);
                if (clock.GetUtcNow() >= nextPrune && await GuardAsync("audit-pruning", () => maintenance.PruneAuditAsync(stoppingToken), stoppingToken)) nextPrune = clock.GetUtcNow().AddDays(1);
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch
        {
            foreach (var name in new[] { "notifications", "security-refresh", "lifecycle-maintenance", "audit-pruning" }) health.Set(name, "unavailable");
            logger.LogError("Account Hub background scheduling stopped; inspect worker health. No exception details are logged.");
        }
        finally
        {
            if (delivery is not null)
            {
                try
                {
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await delivery.StopAsync(deadline.Token).WaitAsync(deadline.Token);
                }
                catch { health.Set("notifications", "unavailable"); }
                try { delivery.Dispose(); } catch { health.Set("notifications", "unavailable"); }
            }
            if (stoppingToken.IsCancellationRequested)
                foreach (var name in new[] { "notifications", "security-refresh", "lifecycle-maintenance", "audit-pruning" }) health.Set(name, "stopped");
        }
    }

    private async Task<bool> GuardAsync(string name, Func<Task> action, CancellationToken ct)
    {
        try { await action(); health.Set(name, "running"); return true; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch
        {
            health.Set(name, "unavailable");
            logger.LogWarning("Account Hub worker {Worker} deferred; unrelated work continues. No exception details or password writes are retried.", name);
            return false;
        }
    }
}
