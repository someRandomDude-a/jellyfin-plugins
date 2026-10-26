using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.DependencyInjection;
using DeletionQueue.Infrastructure;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Jellyfin.Plugin.DeletionQueue;

// No BasePlugin<T>: its catch-and-reset configuration loader is inappropriate for authorization state.
// All settings are revisioned inside the authenticated plugin-owned store instead.
public sealed class Plugin : BasePlugin, IHasWebPages
{
    public Plugin()
    {
        var assembly = GetType().Assembly;
        SetAttributes(assembly.Location, Path.GetDirectoryName(assembly.Location)!, assembly.GetName().Version!);
    }

    public override Guid Id => Guid.Parse(GetType().Assembly.GetCustomAttribute<GuidAttribute>()!.Value);
    public override string Name => GetType().Assembly.GetCustomAttribute<AssemblyTitleAttribute>()!.Title;
    public override string Description => GetType().Assembly.GetCustomAttribute<AssemblyDescriptionAttribute>()!.Description;

    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = "DeletionQueueAdmin", DisplayName = "Deletion queue", EnableInMainMenu = true,
            MenuIcon = "delete", EmbeddedResourcePath = GetType().Namespace + ".Web.admin.html"
        };
        yield return new PluginPageInfo
        { Name = "DeletionQueueAdmin.js", EmbeddedResourcePath = GetType().Namespace + ".Web.admin.js" };
        yield return new PluginPageInfo { Name = "DeletionQueueAdminUI.js", EmbeddedResourcePath = "Suite.Web.admin-ui.js" };
        yield return new PluginPageInfo { Name = "DeletionQueuePresentation.js", EmbeddedResourcePath = GetType().Namespace + ".Web.presentation.js" };
        yield return new PluginPageInfo { Name = "DeletionQueueOverlayEditor.js", EmbeddedResourcePath = GetType().Namespace + ".Web.overlay-editor.js" };
    }
}

public sealed class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost applicationHost)
    {
        AccountHub.Api.NotificationIntegration.RegisterNotificationTypes(services, new("deletion-queue", "1", [
            new("proposal.*", "Removal requests", "A removal request changes or needs consent", "explicit-users", new(), [new("Review request")]) { Commands = ["allow", "keep", "undo"] },
            new("proposal.uncertain", "Removal outcome updates", "Compatibility updates about an uncertain removal", "event", new(), [new("Review request")]) { Commands = ["allow", "keep", "undo"] },
            new("removal.playback_reset", "Playback resets removal", "The requester plays media pending removal", "explicit-users", new() { ToastSeconds = 6 }, [new("View request")]) { Commands = ["undo"], KeepNavigationActions = true },
            new("alert.removal_uncertain", "Removal needs administrator review", "The deletion outcome cannot be confirmed", "administrators", new("standard", 180), [new("Review deletion", "normal")])
        ]));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<HubAvailability>();
        services.AddSingleton<AccountHub.Api.AccountHubClient>();
        services.AddSingleton<QueueRuntime>();
        services.AddSingleton<LiveIdentity>();
        services.AddSingleton<TargetResolver>();
        services.AddSingleton<NotificationCommands>();
        services.AddSingleton<AutomaticRequests>();
        services.AddKeyedSingleton<Func<string, CancellationToken, Task<string>>>(AccountHub.Api.DeletionRequestsClient.ServiceKey,
            (sp, _) => sp.GetRequiredService<AutomaticRequests>().SubmitAsync);
        AccountHub.Api.NotificationIntegration.RegisterNotificationCommands(services, "deletion-queue", sp => sp.GetRequiredService<NotificationCommands>().DispatchAsync);
        AccountHub.Api.NotificationIntegration.RegisterNotificationAccess(services, "deletion-queue", sp => (user, json, _) =>
        {
            try {
                var entry = System.Text.Json.JsonSerializer.Deserialize<AccountHub.Api.NotificationEvent>(json, AccountHub.Api.Contract.Json);
                var raw = entry?.Metadata?.GetValueOrDefault("proposalId");
                if (!Guid.TryParse(raw, out var id)) return Task.FromResult(false);
                var request = sp.GetRequiredService<QueueRuntime>().Read().Proposals.FirstOrDefault(p => p.Id == id);
                return Task.FromResult(request is not null && sp.GetRequiredService<TargetResolver>().Accessible(user, request));
            } catch { return Task.FromResult(false); }
        });
        services.AddSingleton<EnhancedConnection>();
        services.AddSingleton<ISeerrClientFactory, SeerrClientFactory>();
        services.AddSingleton<ActivityFence>();
        services.AddSingleton<ActivityInventory>();
        services.AddSingleton<ArtworkRenderer>();
        services.AddSingleton<MediaPresentationService>();
        services.AddSingleton<RemovalMaintenanceService>();
        services.AddSingleton<AccountHub.Api.AutomaticRuleValidationClient>();
        services.AddKeyedSingleton<Func<string, CancellationToken, Task<string>>>(AccountHub.Api.DeletionRequestsClient.CollectionsServiceKey,
            (sp, _) => async (_, ct) => { await sp.GetRequiredService<MediaPresentationService>().SyncAsync(ct, syncArtwork: false); return "{}"; });
        services.AddTransient<SeerrExecutionGateway>();
        services.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter, WebInjection>();
        services.AddHostedService<ActivityWorker>();
        services.AddHostedService<ExecutionWorker>();
        services.AddHostedService<HubDispatcher>();
    }
}
