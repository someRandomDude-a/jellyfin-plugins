using AccountHub.Api;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.AutomaticDeletionRequests;

public sealed class RuleMaintenanceTask(RuleStore store, RuleRunner runner, DeletionRequestsClient queue) : IScheduledTask
{
    public string Name => "Process automatic removal rules and collections";
    public string Key => "AutomaticDeletionRequests.RuleMaintenance";
    public string Description => "Evaluate enabled rules against current evidence, submit ordinary consent-driven requests, and refresh Deletion Queue collections. Never grants consent or executes deletion.";
    public string Category => "Deletion Queue";
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [new() { Type = TaskTriggerInfoType.DailyTrigger, TimeOfDayTicks = TimeSpan.FromHours(2).Ticks }];
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken ct) {
        progress.Report(0);
        if (store.Read(s => s.Configuration.Enabled)) await runner.RunAsync(false, ct);
        progress.Report(80);
        if (queue.CollectionMaintenanceAvailable) await queue.MaintainCollectionsAsync(ct);
        progress.Report(100);
    }
}
