using AccountHub.Api;
using Jellyfin.Plugin.DeletionQueue;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace DeletionQueue.Tests;

public sealed class ScheduledTaskTests
{
    [Fact] public void DailyJobsHaveSeparateStableIdentitiesAndNoStartupDeletionTrigger() {
        IScheduledTask[] tasks = [new RemovalMaintenanceTask(null!), new OverlayGenerationTask(null!)];
        Assert.Equal(2, tasks.Select(t => t.Key).Distinct().Count());
        foreach (var task in tasks) { var trigger = Assert.Single(task.GetDefaultTriggers()); Assert.Equal(TaskTriggerInfoType.DailyTrigger, trigger.Type); }
        Assert.Equal(TimeSpan.FromHours(3).Ticks, Assert.Single(tasks[0].GetDefaultTriggers()).TimeOfDayTicks);
        Assert.Equal(TimeSpan.FromHours(4).Ticks, Assert.Single(tasks[1].GetDefaultTriggers()).TimeOfDayTicks);
    }
    [Fact] public async Task MissingUnavailableOrMalformedRuleProviderNeverAuthorizesProgress() {
        var request = new AutomaticRuleCheck(Guid.NewGuid(), Guid.NewGuid(), "rule");
        using var missing = new ServiceCollection().BuildServiceProvider(); Assert.False(await new AutomaticRuleValidationClient(missing).MatchesAsync(request, default));
        foreach (var response in new[] { "{}", "null", "{\"matches\":\"true\"}", "bad json", "{\"matches\":false}" }) {
            var services = new ServiceCollection(); services.AddKeyedSingleton<Func<string, CancellationToken, Task<string>>>(AutomaticRuleValidationClient.ServiceKey, (_, _) => (_, _) => Task.FromResult(response));
            using var provider = services.BuildServiceProvider(); Assert.False(await new AutomaticRuleValidationClient(provider).MatchesAsync(request, default));
        }
    }
}
