using System.Text.Json;
using AccountHub.Core;
using AccountHub.Core.Seerr;
using Jellyfin.Plugin.UserManager.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AccountHub.Seerr;

public sealed class SeerrNativeDirectory(JellyfinUserService users) : ISeerrDirectory
{
    public SeerrNativeUser? Find(Guid id)
    {
        var user = users.GetUser(id);
        return user is null ? null : new(id, user.Username, users.IsAdministrator(id), users.IsDisabled(id));
    }
}
public sealed class SeerrEvents(HubStore store) : ISeerrEvents
{
    public async Task PublishAsync(SeerrJobEvent entry, CancellationToken ct) => await store.AppendAuditAsync(new AccountHub.Api.Contracts.AuditEvent
    {
        EventId = entry.EventId, Source = "account-hub", EventType = entry.Type, ActorUserId = entry.ActorId,
        Outcome = entry.State == "completed" ? "success" : "failure", OccurredAt = DateTimeOffset.FromUnixTimeMilliseconds(entry.OccurredAt),
        Metadata = JsonSerializer.SerializeToElement(new { status = entry.State, errorCode = entry.Error })
    }, ct);
}
public sealed class SeerrReconcileWorker(SeerrLifecycle lifecycle, HubStore hub, MediaBrowser.Controller.IServerApplicationHost app, ILogger<SeerrReconcileWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        do
        {
            try
            {
                if (hub.Ready && app.CoreStartupHasCompleted)
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    deadline.CancelAfter(TimeSpan.FromSeconds(90));
                    await lifecycle.TickAsync(deadline.Token);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) { logger.LogWarning("Seerr reconciliation paused; inspect administrator job health. No upstream content or identities are logged."); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
public static class SeerrRegistration
{
    /// <summary>Parent calls after native lifecycle/Hub services. No competing bot, audit writer or DbContext.</summary>
    public static IServiceCollection AddHubSeerr(this IServiceCollection services)
    {
        services.AddSingleton<SeerrStore>(); services.AddSingleton<SeerrLifecycle>();
        services.AddSingleton<ISeerrDirectory, SeerrNativeDirectory>(); services.AddSingleton<ISeerrEvents, SeerrEvents>();
        services.AddSingleton<SeerrConnectionDiscovery>(); services.AddSingleton<ISeerrUpstream, SeerrUpstream>();
        services.AddHostedService<SeerrReconcileWorker>(); return services;
    }
}
