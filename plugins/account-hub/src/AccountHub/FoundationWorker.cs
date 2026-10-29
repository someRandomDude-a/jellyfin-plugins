using AccountHub.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AccountHub;

public sealed class FoundationWorker(HubStore store, ILogger<FoundationWorker> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(30));
        try { await store.InitializeAsync(budget.Token); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            // Do not log exception details that can contain database values or integration credentials.
            logger.LogError("Account Hub storage failed closed. Stop Jellyfin and restore the AccountHub database and original keys. Jellyfin remains available.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
