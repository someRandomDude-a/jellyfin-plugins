using System.Text.Json;
using AccountHub.Api;
using AccountHub.Core;
using Jellyfin.Plugin.UserManager.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Users;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AccountHub.Groups;

public sealed class NativeGroupPolicy(IServiceProvider services) : IGroupPolicyNative
{
    private IUserManager Manager => services.GetRequiredService<IUserManager>();
    public UserPolicy Read(Guid id)
    {
        var user = Manager.GetUserById(id) ?? throw new HubException("not_found", "User no longer exists");
        return PolicyTools.Normalize(Manager.GetUserDto(user).Policy);
    }
    public Task WriteManagedAsync(Guid id, JsonElement patch, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return services.GetRequiredService<NativeUserWrites>().ApplyManagedAsync(id, patch, requireDisabled: false, enable: false, ct);
    }
}

/// <summary>Native events schedule read-only drift checks. No implicit reconciliation or fire-and-forget effects.</summary>
public sealed class GroupDriftObserver(IServiceProvider services, GroupPolicyService policies) : BackgroundService
{
    private readonly SemaphoreSlim changed = new(0, 1);
    public long CheckedUsers { get; private set; }
    public long DriftedUsers { get; private set; }
    public string State { get; private set; } = "pending";
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var manager = services.GetRequiredService<IUserManager>();
        void Signal() { if (changed.CurrentCount == 0) try { changed.Release(); } catch (SemaphoreFullException) { } }
        manager.OnUserUpdated += OnUpdated;
        void OnUpdated(object? sender, Jellyfin.Data.Events.GenericEventArgs<Jellyfin.Database.Implementations.Entities.User> user) => Signal();
        try
        {
            while (!services.GetRequiredService<MediaBrowser.Controller.IServerApplicationHost>().CoreStartupHasCompleted || !services.GetRequiredService<HubStore>().Ready)
                await Task.Delay(1000, stoppingToken);
            Signal();
            while (!stoppingToken.IsCancellationRequested)
            {
                await changed.WaitAsync(stoppingToken);
                // Coalesce event bursts without ever writing user policy.
                await Task.Delay(250, stoppingToken);
                try
                {
                    await policies.GroupsAsync(0, 1, stoppingToken);
                    await using var db = policies.Open();
                    var skip = 0; long total = 0, drifted = 0;
                    while (true)
                    {
                        var ids = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToArrayAsync(db.Assignments.OrderBy(x => x.UserId).Skip(skip).Take(100).Select(x => x.UserId), stoppingToken);
                        if (ids.Length == 0) break;
                        foreach (var id in ids)
                        {
                            try { var view = await policies.DriftAsync(id, stoppingToken); total++; if (view.ChangedFields.Length > 0) drifted++; }
                            catch (HubException ex) when (ex.Code == "not_found") { }
                        }
                        skip += ids.Length;
                    }
                    CheckedUsers = total; DriftedUsers = drifted; State = "ready";
                }
                catch (Exception) when (!stoppingToken.IsCancellationRequested) { State = "unavailable"; }
            }
        }
        finally { manager.OnUserUpdated -= OnUpdated; }
    }
}

public static class GroupPolicyRegistration
{
    public static IServiceCollection AddHubGroupPolicy(this IServiceCollection services)
    {
        services.AddSingleton<IGroupPolicyNative, NativeGroupPolicy>();
        services.AddSingleton<GroupPolicyService>();
        services.AddSingleton<GroupDriftObserver>();
        services.AddHostedService(sp => sp.GetRequiredService<GroupDriftObserver>());
        return services;
    }
}
