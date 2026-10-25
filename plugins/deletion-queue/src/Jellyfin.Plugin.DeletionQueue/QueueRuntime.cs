using DeletionQueue.Core;
using DeletionQueue.Infrastructure;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Jellyfin.Plugin.DeletionQueue;

public sealed class QueueRuntime : IDisposable, IStateStore
{
    private readonly AuthenticatedStore? store;
    private int failed;
    private int collectorFailed;
    private int collectorStarted;
    public bool Available => store is not null && Volatile.Read(ref failed) == 0;
    public bool CollectorHealthy => Volatile.Read(ref collectorStarted) == 1 && Volatile.Read(ref collectorFailed) == 0;
    public void MarkCollectorStarted() => Interlocked.Exchange(ref collectorStarted, 1);
    public void MarkCollectorFailed() => Interlocked.Exchange(ref collectorFailed, 1);
    public const string ReleaseMode = "Seerr-managed removal — explicit automatic-deletion opt-in, complete activity coverage, current consent and delay required. Local authenticated journal; no external rollback witness.";

    private readonly TimeProvider clock;
    private readonly HubAvailability hub;
    public QueueRuntime(IApplicationPaths paths, ILogger<QueueRuntime> logger, TimeProvider clock, HubAvailability? hub = null)
    {
        this.clock = clock;
        this.hub = hub ?? new HubAvailability();
        try
        {
            store = new AuthenticatedStore(Path.Combine(paths.DataPath, "deletion-queue"));
            if (store.Read().Schema == 1)
                store.Write(Guid.NewGuid(), "migration", "AccountHubFormatV2", state => { state.Schema = 2; return true; });
        }
        catch (Exception)
        {
            Interlocked.Exchange(ref failed, 1);
            // Do not include file paths, keys or decrypted authorization records in logs.
            try { logger.LogError("Deletion queue storage unavailable or failed integrity validation. Original state preserved; no reset performed."); } catch (Exception) { }
        }
    }

    public QueueState Read() => Guard(() => RequireStore().Read());
    public IReadOnlyList<JournalEntry> History() => Guard(() => RequireStore().History());
    public T Write<T>(Guid command, string actor, string kind, Func<QueueState, T> update) =>
        Guard(() => RequireStore().Write(command, actor, kind, state =>
        {
            var revisions = state.Proposals.ToDictionary(p => p.Id, p => p.Revision);
            var notices = state.Notices.Select(n => n.Id).ToHashSet();
            var configurationBefore = HubIntegration.ConfigurationSummary(state);
            var result = update(state);
            // Notices and proposal changes commit together, including policy/coverage/deadline changes.
            // Progress/stop events still invalidate consent and block execution,
            // but must not generate another inbox notice every playback tick.
            if (kind != "PlaybackProgressCaptured")
                NoticeRules.Publish(state, state.Proposals.Where(p => !revisions.TryGetValue(p.Id, out var revision) || p.Revision != revision), clock.GetUtcNow());
            object? summary = null;
            if (kind == "AccountHubPolicyReset" && result is string json)
            {
                using var document = JsonDocument.Parse(json);
                var data = document.RootElement.GetProperty("data");
                if (data.TryGetProperty("scope", out var scope) && data.TryGetProperty("skippedAdminCount", out var skipped))
                    summary = new { scope = scope.GetString(), skippedAdminCount = skipped.GetInt32() };
            }
            HubIntegration.Capture(state, revisions, notices, actor, kind, clock.GetUtcNow(), hub.Ready, configurationBefore, summary);
            return result;
        }));
    private AuthenticatedStore RequireStore() => Available ? store! : throw new IntegrityException("Storage unavailable; authorization frozen.");
    private T Guard<T>(Func<T> action)
    {
        try { return action(); }
        catch (QueueException e)
        {
            if (e.Code.EndsWith("CapacityReached", StringComparison.Ordinal)) Interlocked.Exchange(ref failed, 1);
            throw;
        }
        catch (Exception) { Interlocked.Exchange(ref failed, 1); throw new IntegrityException("Plugin unavailable; original state preserved."); }
    }
    public void Dispose() { try { store?.Dispose(); } catch (Exception) { Interlocked.Exchange(ref failed, 1); } }
}
