using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DeletionQueue;

// Coalesced configuration/action refresh, NOT another deletion schedule.
public sealed class QueueReevaluationWorker(QueueRuntime runtime,RemovalMaintenanceService maintenance,ILogger<QueueReevaluationWorker> logger):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct){
        await Task.Yield();
        try{while(!ct.IsCancellationRequested){
            // A cancelled wait is disposed before the next iteration; no orphaned channel readers.
            using(var wait=CancellationTokenSource.CreateLinkedTokenSource(ct)){
                wait.CancelAfter(TimeSpan.FromMinutes(1));
                try{if(!await runtime.Reevaluations.WaitToReadAsync(wait.Token))break;}
                catch(OperationCanceledException)when(!ct.IsCancellationRequested){}
            }
            while(runtime.Reevaluations.TryRead(out _)){}
            try{if(runtime.Available)await maintenance.ReevaluateAsync(ct);}
            catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
            catch{logger.LogWarning("Deletion queue re-evaluation deferred; no removal was attempted. Check saved connection and collector health.");}
        }}catch(OperationCanceledException)when(ct.IsCancellationRequested){}
    }
}
