using DiscordBridge.Api;
using Jellyfin.Plugin.PrivateSyncPlay.Api;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using AccountHub.Security;

namespace Jellyfin.Plugin.DiscordBridge;

public sealed class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost host)
    {
        AccountHub.Api.NotificationIntegration.RegisterNotificationTypes(services, new("discord-bridge", "1", [
            new("transport.*", "Discord alerts", "Discord transport needs administrator attention", "administrators", new(), [new("Open Discord settings")])
        ]));
        services.AddSingleton(sp => new BridgeStore(Path.Combine(sp.GetRequiredService<IApplicationPaths>().PluginConfigurationsPath, "DiscordBridge")));
        services.AddSingleton<BridgeRuntime>();
        services.AddSingleton<Jellyfin.Plugin.PrivateSyncPlay.NativeWebStyles>();
        services.AddHostedService(sp => sp.GetRequiredService<BridgeRuntime>());
        services.AddPrivateSyncPlayClient();
        services.AddSingleton<WatchPartyCommands>();
        services.AddSingleton<Jellyfin.LibraryNotifications.LibraryNoticeStore>(sp => new(Path.Combine(sp.GetRequiredService<IApplicationPaths>().PluginConfigurationsPath, "DiscordBridge", "discord-bridge.db")));
        services.AddSingleton<BridgeLibraryNotices>();
        services.AddKeyedSingleton<Func<bool>>(DiscordBridgeClient.LibraryCaptureKey,
            (sp, _) => () => sp.GetRequiredService<BridgeStore>().Read().Settings.LibraryAnnouncementsEnabled);
        services.AddSingleton<Jellyfin.LibraryNotifications.ILibraryNoticeHost>(sp => sp.GetRequiredService<BridgeLibraryNotices>());
        services.AddHostedService<Jellyfin.LibraryNotifications.LibraryNoticeWorker>();
        services.AddJellyfinSecurityClient();
        services.AddKeyedSingleton<Func<string, CancellationToken, Task<string>>>(DiscordBridgeClient.ServiceKey,
            (sp, _) => (json, ct) => sp.GetRequiredService<BridgeRuntime>().DispatchAsync(json, ct));
    }
}
