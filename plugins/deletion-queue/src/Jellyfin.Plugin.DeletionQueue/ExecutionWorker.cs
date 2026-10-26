using DeletionQueue.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DeletionQueue;

// Startup recovery is not a second deletion schedule. Claimed attempts are never resent.
public sealed class ExecutionWorker(QueueRuntime runtime, SeerrExecutionGateway gateway, TimeProvider clock,
    ILogger<ExecutionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct) {
        await Task.Yield();
        try {
            ct.ThrowIfCancellationRequested();
            if (runtime.Available) new ExecutionCoordinator(runtime, gateway, clock).RecoverClaims();
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception) { logger.LogError("Deletion queue recovery stopped safely. Durable claims retained; no deletion resent."); }
    }
}
