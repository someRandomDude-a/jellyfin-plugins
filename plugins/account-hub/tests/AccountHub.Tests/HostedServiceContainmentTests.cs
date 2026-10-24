using AccountHub.Core;
using Jellyfin.Plugin.UserManager.Data;
using Jellyfin.Plugin.UserManager.Security;
using Jellyfin.Plugin.UserManager.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace AccountHub.Tests;

public sealed class HostedServiceContainmentTests
{
    [Fact]
    public void ProductionRegistrationDefersBothSettingsDependentWorkers()
    {
        var services = new ServiceCollection();
        new PluginServiceRegistrator().RegisterServices(services, null!);
        var hosted = services.Where(service => service.ServiceType == typeof(IHostedService)).ToArray();
        Assert.Contains(hosted, service => service.ImplementationType == typeof(DeferredHostedService<InviteDeliveryWorker>));
        Assert.Contains(hosted, service => service.ImplementationType == typeof(DeferredHostedService<Seerr.SeerrReconcileWorker>));
        Assert.DoesNotContain(hosted, service => service.ImplementationType == typeof(InviteDeliveryWorker) || service.ImplementationType == typeof(Seerr.SeerrReconcileWorker));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FallibleLifecycleConstructionCannotStopHost(bool inaccessible)
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp", "opencode", "hub-worker-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var conflict = Path.Combine(directory, "lifecycle");
        await File.WriteAllTextAsync(conflict, "This test owns the conflicting file.");
        try
        {
            var resolutions = 0;
            using var host = new HostBuilder().ConfigureServices(services =>
            {
                services.AddSingleton<HubWorkerHealth>();
                services.AddSingleton<PluginStore>(_ =>
                {
                    Interlocked.Increment(ref resolutions);
                    // Portable permission-denial simulation; the other case exercises real filesystem I/O.
                    if (inaccessible) throw new UnauthorizedAccessException("simulated inaccessible lifecycle directory");
                    return new PluginStore(conflict);
                });
                services.AddSingleton(_ => new SecretProtector(conflict));
                services.AddSingleton<PluginSettingsService>();
                services.AddHostedService<SettingsConsumer>();
                services.DeferHubWorker<SettingsConsumer>();
            }).Build();
            _ = host.Services.GetServices<IHostedService>().ToArray();
            Assert.Equal(0, resolutions);
            await host.StartAsync();
            var health = host.Services.GetRequiredService<HubWorkerHealth>();
            await UntilAsync(() => health.Snapshot().TryGetValue(nameof(SettingsConsumer), out var state) && state == "unavailable");
            Assert.False(health.Healthy);
            Assert.False(host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested);
            Assert.Equal(1, resolutions);
            Assert.True(File.Exists(conflict));
            await host.StopAsync();
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task TransientMaintenanceFailureLeavesNotificationsAndLaterTicksRunning()
    {
        await using var fixture = new StoreFixture();
        await fixture.InitializeAsync();
        var clock = new ManualClock();
        var work = new MaintenanceFake();
        using var host = new HostBuilder().ConfigureServices(services =>
        {
            services.AddSingleton(fixture.Store);
            services.AddSingleton<TimeProvider>(clock);
            services.AddSingleton<HubWorkerHealth>();
            services.AddSingleton<IHubMaintenance>(work);
            services.AddHostedService<CompositeWorker>();
        }).Build();
        await host.StartAsync();
        var health = host.Services.GetRequiredService<HubWorkerHealth>();
        await UntilAsync(() => health.Snapshot().TryGetValue("lifecycle-maintenance", out var state) && state == "unavailable");
        await work.Delivery.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(health.IsRunning("notifications"));
        Assert.True(health.IsRunning("security-refresh"));
        Assert.False(health.Healthy);
        Assert.False(work.Delivery.ExecuteTask!.IsCompleted);
        Assert.False(host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested);

        clock.Tick();
        await UntilAsync(() => health.IsRunning("lifecycle-maintenance"));
        Assert.True(Volatile.Read(ref work.SecurityCalls) >= 2);
        Assert.Equal(2, Volatile.Read(ref work.LifecycleCalls));
        Assert.Equal(1, Volatile.Read(ref work.DeliveryCreates));
        Assert.False(work.Delivery.ExecuteTask!.IsCompleted);
        Assert.True(health.Healthy);
        await host.StopAsync();
        Assert.Equal("stopped", health.Snapshot()["notifications"]);
    }

    [Fact]
    public async Task NotificationStartupFailureDoesNotBlockMaintenanceAndRetriesNextTick()
    {
        await using var fixture = new StoreFixture();
        await fixture.InitializeAsync();
        var clock = new ManualClock();
        var work = new MaintenanceFake { FailDeliveryOnce = true, FailLifecycleOnce = false };
        using var host = new HostBuilder().ConfigureServices(services =>
        {
            services.AddSingleton(fixture.Store);
            services.AddSingleton<TimeProvider>(clock);
            services.AddSingleton<HubWorkerHealth>();
            services.AddSingleton<IHubMaintenance>(work);
            services.AddHostedService<CompositeWorker>();
        }).Build();
        await host.StartAsync();
        var health = host.Services.GetRequiredService<HubWorkerHealth>();
        await UntilAsync(() => health.IsRunning("lifecycle-maintenance"));
        Assert.Equal("unavailable", health.Snapshot()["notifications"]);
        Assert.True(health.IsRunning("security-refresh"));
        clock.Tick();
        await UntilAsync(() => health.IsRunning("notifications"));
        Assert.Equal(2, Volatile.Read(ref work.DeliveryCreates));
        Assert.True(Volatile.Read(ref work.LifecycleCalls) >= 2);
        await host.StopAsync();
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, deadline.Token);
    }

    public sealed class SettingsConsumer(PluginSettingsService settings) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _ = await settings.GetAsync(stoppingToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
    }

    private sealed class MaintenanceFake : IHubMaintenance
    {
        public int SecurityCalls, LifecycleCalls, DeliveryCreates;
        public bool FailDeliveryOnce;
        public bool FailLifecycleOnce = true;
        public readonly DeliveryFake Delivery = new();
        public BackgroundService CreateDeliveryWorker()
        {
            if (Interlocked.Increment(ref DeliveryCreates) == 1 && FailDeliveryOnce) throw new IOException("transient notification initialization failure");
            return Delivery;
        }
        public Task RefreshSecurityAsync(CancellationToken ct) { Interlocked.Increment(ref SecurityCalls); return Task.CompletedTask; }
        public Task RevokePendingSessionsAsync(CancellationToken ct)
        {
            if (Interlocked.Increment(ref LifecycleCalls) == 1 && FailLifecycleOnce) throw new IOException("transient lifecycle query failure");
            return Task.CompletedTask;
        }
        public Task PruneAuditAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class DeliveryFake : BackgroundService
    {
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private ManualTimer? timer;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => timer = new ManualTimer(callback, state);
        public void Tick() => timer!.Fire();
        private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
        {
            public void Fire() => callback(state);
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
