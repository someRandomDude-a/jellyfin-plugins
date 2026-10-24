using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AccountHub;

/// <summary>Resolve fallible module dependencies inside the worker, never while the host constructs its service list.</summary>
public sealed class DeferredHostedService<T>(IServiceProvider services, HubWorkerHealth health, ILogger<DeferredHostedService<T>> logger) : BackgroundService
    where T : class, IHostedService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        T? worker = null;
        var name = typeof(T).Name;
        health.Set(name, "starting");
        try
        {
            worker = ActivatorUtilities.CreateInstance<T>(services);
            await worker.StartAsync(stoppingToken);
            health.Set(name, "running");
            if (worker is BackgroundService background && background.ExecuteTask is { } execution) await execution;
            else await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
            if (!stoppingToken.IsCancellationRequested) health.Set(name, "stopped");
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch
        {
            health.Set(name, "unavailable");
            logger.LogError("Account Hub worker {Worker} is unavailable. Inspect module storage/dependencies and restart after repair; Jellyfin remains available. No exception details are logged.", name);
        }
        finally
        {
            if (worker is not null)
            {
                try
                {
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await worker.StopAsync(deadline.Token).WaitAsync(deadline.Token);
                }
                catch { health.Set(name, "unavailable"); }
                try { (worker as IDisposable)?.Dispose(); }
                catch { health.Set(name, "unavailable"); }
            }
            if (stoppingToken.IsCancellationRequested) health.Set(name, "stopped");
        }
    }
}

public static class HostedServiceIsolation
{
    public static void DeferHubWorker<T>(this IServiceCollection services) where T : class, IHostedService
    {
        var registration = services.Single(descriptor => descriptor.ServiceType == typeof(IHostedService) && descriptor.ImplementationType == typeof(T));
        services[services.IndexOf(registration)] = ServiceDescriptor.Singleton<IHostedService, DeferredHostedService<T>>();
    }
}
