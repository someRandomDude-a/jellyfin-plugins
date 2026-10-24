using Jellyfin.Database.Implementations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AccountHub;

/// <summary>Detection only: changing a live legacy ID cannot safely invalidate native user-data caches.</summary>
public sealed class NativeUserIntegrity(IServiceProvider services, HubWorkerHealth health) : BackgroundService
{
    private object state = new { status = "waiting", duplicateGroups = 0, affectedUsers = 0, offlineRepairRequired = false };
    public object Snapshot => Volatile.Read(ref state);
    public async Task ScanAsync(CancellationToken ct)
    {
        await using var db = await services.GetRequiredService<IDbContextFactory<JellyfinDbContext>>().CreateDbContextAsync(ct);
        InviteNativeProvisioner.RequireSupportedDatabase(typeof(JellyfinDbContext).Assembly.GetName().Version, db.Database.ProviderName);
        var duplicates = await db.Users.AsNoTracking().GroupBy(u => u.InternalId).Where(g => g.Count() > 1).Select(g => g.Count()).ToArrayAsync(ct);
        Volatile.Write(ref state, new { status = duplicates.Length == 0 ? "ok" : "duplicate_internal_ids", duplicateGroups = duplicates.Length, affectedUsers = duplicates.Sum(), offlineRepairRequired = duplicates.Length != 0 });
        health.Set("native-user-integrity", duplicates.Length == 0 ? "running" : "remediation_required");
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!services.GetRequiredService<MediaBrowser.Controller.IServerApplicationHost>().CoreStartupHasCompleted)
                await Task.Delay(1000, stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
            do
            {
                try { await ScanAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch { health.Set("native-user-integrity", "unavailable"); Volatile.Write(ref state, new { status = "unavailable", offlineRepairRequired = false }); }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch { health.Set("native-user-integrity", "unavailable"); }
    }
}
