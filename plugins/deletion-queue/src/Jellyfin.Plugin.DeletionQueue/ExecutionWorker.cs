using DeletionQueue.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DeletionQueue;

public sealed class ExecutionWorker(QueueRuntime runtime, SeerrExecutionGateway gateway, TimeProvider clock,
    ILogger<ExecutionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield(); var offset = 0;
        var checkedAt = new Dictionary<Guid, (long Revision, DateTimeOffset Due)>();
        try
        {
            var coordinator = new ExecutionCoordinator(runtime, gateway, clock);
            if (!runtime.Available) return;
            coordinator.RecoverClaims();
            while (!stoppingToken.IsCancellationRequested && runtime.Available)
            {
                var now = clock.GetUtcNow();
                var proposals = runtime.Read().Proposals;
                foreach (var id in checkedAt.Keys.Where(id => !proposals.Any(p => p.Id == id && !p.Terminal)).ToArray()) checkedAt.Remove(id);
                var all = proposals.Where(p => !p.Terminal && p.Claim is null &&
                    (!checkedAt.TryGetValue(p.Id, out var check) || check.Revision != p.Revision || now >= check.Due)).ToArray();
                if (offset >= all.Length) offset = 0;
                foreach (var proposal in all.Skip(offset).Take(10))
                {
                    stoppingToken.ThrowIfCancellationRequested();
                    try
                    {
                        var preflight = await gateway.PreflightAsync(proposal, stoppingToken);
                        runtime.Write(Guid.NewGuid(), "scheduler", "EligibilityEvaluated", state =>
                        {
                            var p = QueueRules.Find(state, proposal.Id);
                            if (p.Revision != preflight.ProposalRevision || p.Claim is not null) throw new QueueException("RevisionChanged");
                            var before = (p.Status, p.Reason, p.NotBefore, p.EligibleSince, p.DelayFloorHours);
                            QueueRules.Evaluate(state, p, preflight.Evidence, clock.GetUtcNow());
                            if (before != (p.Status, p.Reason, p.NotBefore, p.EligibleSince, p.DelayFloorHours)) p.Revision++;
                            return true;
                        });
                        var state = runtime.Read(); var current = QueueRules.Find(state, proposal.Id);
                        var due = clock.GetUtcNow().AddDays(1);
                        if (current.Reason is "ActivityCoverageIncomplete" or "VerificationUnavailable") due = clock.GetUtcNow().AddMinutes(5);
                        if (current.NotBefore is { } deadline && deadline > clock.GetUtcNow() && deadline < due) due = deadline;
                        if (current.ApprovalDeadline is { } decisionDeadline && decisionDeadline > clock.GetUtcNow() && decisionDeadline < due) due = decisionDeadline;
                        checkedAt[current.Id] = (current.Revision, due);
                        if (state.Policy.AutomaticDeletion && state.Policy.SeerrManagedDeletionAcknowledged && current.Status == ProposalStatus.Ready)
                            await coordinator.ExecuteAsync(current.Id, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                    // Another consent/activity write won the race. Retry fresh evidence
                    // next pass; a concurrency conflict is not an authorization failure.
                    catch (QueueException e) when (e.Code == "RevisionChanged") { }
                    catch (Exception)
                    {
                        if (!runtime.Available) return;
                        runtime.Write(Guid.NewGuid(), "scheduler", "VerificationUnavailable", state =>
                        {
                            var p = QueueRules.Find(state, proposal.Id);
                            if (p.Claim is null && !p.Terminal)
                            {
                                var before = (p.Status, p.Reason, p.NotBefore, p.EligibleSince, p.DelayFloorHours);
                                QueueRules.VerificationUnavailable(state, p, clock.GetUtcNow());
                                if (before != (p.Status, p.Reason, p.NotBefore, p.EligibleSince, p.DelayFloorHours)) p.Revision++;
                            }
                            return true;
                        });
                        var failed = QueueRules.Find(runtime.Read(), proposal.Id);
                        checkedAt[failed.Id] = (failed.Revision, clock.GetUtcNow().AddMinutes(5));
                    }
                }
                offset += 10;
                foreach (var p in runtime.Read().Proposals.Where(p => p.Status == ProposalStatus.FilesDeleted))
                    runtime.Write(Guid.NewGuid(), "recovery", "CompletedAfterFilesDeletedCheckpoint", state => { QueueRules.Complete(state, p.Id, clock.GetUtcNow()); return true; });
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception) { try { logger.LogError("Deletion queue execution stopped safely. Durable claims retained; Jellyfin remains running."); } catch (Exception) { } }
    }
}
