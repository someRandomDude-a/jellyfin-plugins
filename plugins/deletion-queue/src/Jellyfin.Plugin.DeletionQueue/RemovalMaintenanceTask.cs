using DeletionQueue.Core;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.DeletionQueue;

public sealed class RemovalMaintenanceService(QueueRuntime runtime, SeerrExecutionGateway gateway, MediaPresentationService presentation, TimeProvider clock)
{
    private readonly SemaphoreSlim gate = new(1);
    public Task RunAsync(IProgress<double> progress,CancellationToken ct)=>RunCoreAsync(progress,ct,true);
    public Task ReevaluateAsync(CancellationToken ct)=>RunCoreAsync(new Progress<double>(),ct,false);
    private async Task RunCoreAsync(IProgress<double> progress, CancellationToken ct,bool executeRemovals) {
        await gate.WaitAsync(ct);
        try {
            var coordinator = new ExecutionCoordinator(runtime, gateway, clock);
            var pending = runtime.Read().Proposals.Where(p => !p.Terminal && p.Claim is null).Select(p => p.Id).ToArray();
            progress.Report(0);
            for (var i = 0; i < pending.Length; i++) {
                ct.ThrowIfCancellationRequested();
                var proposal = QueueRules.Find(runtime.Read(), pending[i]);
                if (proposal.Terminal || proposal.Claim is not null) continue;
                try {
                    // Every stage uses a new native playback/session scan, target resolution,
                    // live rule check, current consent and administrator/policy evidence.
                    using var verificationBudget = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    verificationBudget.CancelAfter(TimeSpan.FromSeconds(45));
                    var external=await gateway.InspectExternalChangeAsync(proposal,verificationBudget.Token);
                    if(external is not null){
                        runtime.Write(Guid.NewGuid(),"upstream-observation","ExternalTargetChange",state=>{
                            QueueRules.CloseExternalChange(state,proposal.Id,proposal.Revision,proposal.Target.Fingerprint,external,clock.GetUtcNow());return true;
                        });
                        continue;
                    }
                    var preflight = await gateway.PreflightAsync(proposal, verificationBudget.Token);
                    runtime.Write(Guid.NewGuid(), "scheduled-task", "EligibilityEvaluated", state => {
                        var current = QueueRules.Find(state, proposal.Id);
                        if (current.Revision != preflight.ProposalRevision || current.Claim is not null) throw new QueueException("RevisionChanged");
                        var before = (current.Status, current.Reason, current.NotBefore, current.EligibleSince, current.DelayFloorHours);
                        QueueRules.Evaluate(state, current, preflight.Evidence, clock.GetUtcNow());
                        if (before != (current.Status, current.Reason, current.NotBefore, current.EligibleSince, current.DelayFloorHours)) current.Revision++;
                        return true;
                    });
                    var state = runtime.Read(); var current = QueueRules.Find(state, proposal.Id);
                     if (executeRemovals && state.Policy.AutomaticDeletion && state.Policy.SeerrManagedDeletionAcknowledged && current.Status == ProposalStatus.Ready)
                        // Coordinator obtains another preflight before claiming, then rechecks
                        // immediately before dispatch. The date alone never authorizes deletion.
                        await coordinator.ExecuteAsync(current.Id, ct);
                } catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (QueueException e) when (e.Code == "RevisionChanged") { /* A newer consent/activity write won. Next task retries fresh. */ }
                catch (Exception error) {
                    if (!runtime.Available) throw;
                    runtime.Write(Guid.NewGuid(), "scheduled-task", "VerificationUnavailable", state => {
                        var current = QueueRules.Find(state, proposal.Id);
                        if (!current.Terminal && current.Claim is null) { QueueRules.VerificationUnavailable(state, current, clock.GetUtcNow(), (error as QueueException)?.Code); current.Revision++; }
                        return true;
                    });
                }
                progress.Report(90d * (i + 1) / Math.Max(1, pending.Length));
            }
            foreach (var p in runtime.Read().Proposals.Where(p => p.Status == ProposalStatus.FilesDeleted))
                runtime.Write(Guid.NewGuid(), "recovery", "CompletedAfterFilesDeletedCheckpoint", state => { QueueRules.Complete(state, p.Id, clock.GetUtcNow()); return true; });
            await presentation.SyncAsync(ct, syncArtwork: false);
            progress.Report(100);
        } finally { gate.Release(); }
    }
}

public sealed class RemovalMaintenanceTask(RemovalMaintenanceService service) : IScheduledTask
{
    public string Name => "Verify removal conditions and process due deletions";
    public string Key => "DeletionQueue.RemovalMaintenance";
    public string Description => "Freshly verify playback, rules, target, permissions, consent and delay before progressing requests. Remove due media only with explicit administrator opt-in and another pre-dispatch verification. Refresh collections afterward.";
    public string Category => "Deletion Queue";
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [new() { Type = TaskTriggerInfoType.DailyTrigger, TimeOfDayTicks = TimeSpan.FromHours(3).Ticks }];
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken ct) => service.RunAsync(progress, ct);
}

public sealed class OverlayGenerationTask(MediaPresentationService presentation) : IScheduledTask
{
    public string Name => "Generate and restore removal artwork overlays";
    public string Key => "DeletionQueue.OverlayGeneration";
    public string Description => "Generate bounded portrait/landscape overlays for pending requests, or restore owned image pointers for closed/disabled requests. Preserve original files and administrator edits. Does not advance or delete requests.";
    public string Category => "Deletion Queue";
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [new() { Type = TaskTriggerInfoType.DailyTrigger, TimeOfDayTicks = TimeSpan.FromHours(4).Ticks }];
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken ct) {
        progress.Report(0); await presentation.SyncAsync(ct, syncCollections: false, maximumChanges: 1000); progress.Report(100);
    }
}
