using AccountHub.Notifications;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Jellyfin.Plugin.Notifications;

public sealed class Configuration : BasePluginConfiguration { }
public sealed class Plugin(IApplicationPaths paths, IXmlSerializer serializer) : BasePlugin<Configuration>(paths, serializer), IHasWebPages
{
    public override string Name => "Notifications";
    public override Guid Id => new("c18d78ec-4b70-47cf-a0d2-936d987c37ba");
    public override string Description => "Shared private notification inbox, preferences and delivery for Jellyfin plugins.";
    public IEnumerable<PluginPageInfo> GetPages() => [new() { Name="Notifications", EmbeddedResourcePath="Jellyfin.Plugin.Notifications.Web.admin.html" }, new() { Name="NotificationsAdminUI.js", EmbeddedResourcePath="Suite.Web.admin-ui.js" }];
}

public sealed class Storage(IApplicationPaths paths)
{
    public string DirectoryPath => Path.Combine(paths.PluginConfigurationsPath, "Notifications");
    private readonly Lazy<string> database = new(() => NotificationsMigration.Initialize(paths.PluginConfigurationsPath));
    public string Database => database.Value;
    public IDataProtectionProvider Protection()
    {
        _ = Database;
        var keys = Path.Combine(DirectoryPath, "keys");
        if (File.Exists(Database) && (!System.IO.Directory.Exists(keys) || !System.IO.Directory.EnumerateFiles(keys, "key-*.xml").Any()))
            throw new InvalidOperationException("Restore the original notification keys; refusing to replace them.");
        System.IO.Directory.CreateDirectory(keys);
        var provider=DataProtectionProvider.Create(new DirectoryInfo(keys), builder => builder.SetApplicationName("Notifications"));
        _=provider.CreateProtector("Notifications.Installation.1").Protect("initialized");
        return provider;
    }
}
public sealed class Registrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost applicationHost)
    {
        Jellyfin.PluginSupport.SuiteDiagnostics.Register(services,"notifications");
        AccountHub.Api.NotificationIntegration.RegisterNotificationTypes(services, new("notifications", "1", [
            new("notifications.*", "Notification alerts", "Delivery or notification configuration needs review", "administrators", new(), []) { Defaults=new(Importance:"high"), Example=new("Delivery needs attention","A notification could not be delivered. Review delivery health.","warning") },
            new("library.movie.added", "New movies", "A new physical movie is added to a selected library", "explicit-users", new(), []) { Defaults=new(), Example=new("Example Movie (2026)","A new movie is available in your library.",Image:true) },
            new("library.episode.added", "New episodes", "A new physical episode is added to a selected library", "explicit-users", new(), []) { Defaults=new(), Example=new("Example Series · S01E02 · A new adventure","Season 1, episode 2 is now available.",Image:true) }
        ]) { Grouping=new("groupId","Show or movie","groupLabel") });
        services.AddSingleton<Storage>();
        services.AddSingleton<Jellyfin.LibraryNotifications.LibraryNoticeStore>(sp => {
            var storage=sp.GetRequiredService<Storage>(); var library=new Jellyfin.LibraryNotifications.LibraryNoticeStore(storage.Database);
            var paths=sp.GetRequiredService<IApplicationPaths>();
            library.Import(Path.Combine(paths.PluginConfigurationsPath,"AccountHub","account-hub.db"),true);
            library.Import(Path.Combine(paths.PluginConfigurationsPath,"DiscordBridge","discord-bridge.db"),false);
            return library;
        });
        services.AddSingleton<LibraryNotifications>();
        services.AddHostedService<NotificationLibraryWorker>();
        services.AddKeyedSingleton<Func<string,CancellationToken,Task<string>>>("Notifications.LibrarySettings.1",(sp,_) => (json,ct) => {
            var library=sp.GetRequiredService<Jellyfin.LibraryNotifications.LibraryNoticeStore>();
            if(json!="read") { var settings=System.Text.Json.JsonSerializer.Deserialize<Jellyfin.LibraryNotifications.LibraryNoticeSettings>(json,AccountHub.Api.Contract.Json)??throw new ArgumentException("Missing library settings."); library.SaveSettings(settings); }
            var folders=sp.GetRequiredService<MediaBrowser.Controller.Library.ILibraryManager>().GetVirtualFolders().Select(f=>new{id=Guid.TryParse(f.ItemId,out var id)?id.ToString("D"):null,name=f.Name}).Where(f=>f.id is not null);
            return Task.FromResult(AccountHub.Api.Contract.Serialize(new{settings=library.Settings(),health=library.Health(),libraries=folders}));
        });
        services.AddKeyedSingleton<Func<bool>>(AccountHub.Api.NotificationIntegration.OwnerKey, (_, _) => () => true);
        services.RemoveAll<NotificationOptions>();
        services.RemoveAll<NotificationStore>();
        services.RemoveAll<INotificationHost>();
        services.RemoveAll<IDiscordTransport>();
        services.AddSingleton<NotificationOptions>(sp => new() { DatabasePath=sp.GetRequiredService<Storage>().Database, RequireExistingDatabase=true });
        services.AddSingleton<NotificationStore>(sp => new(sp.GetRequiredService<NotificationOptions>(), sp.GetRequiredService<Storage>().Protection(), TimeProvider.System));
        services.AddSingleton<INotificationHost, NativeHost>();
        services.AddSingleton<IDiscordTransport, BridgeTransport>();
        services.TryAddSingleton<DiscordBridge.Api.DiscordBridgeClient>();
        services.AddAccountHubNotifications(sp => new() { DatabasePath=sp.GetRequiredService<Storage>().Database }, startWorker:false, registerProducer:false);
        services.AddHostedService<NotificationWorker>();
        services.AddKeyedSingleton<Func<string, CancellationToken, Task<string>>>(AccountHub.Api.NotificationIntegration.ServiceKey, (sp, _) => (json,ct)=>sp.GetRequiredService<NotificationService>().DispatchAsync(json,ct));
        services.AddSingleton<EndpointAdapter>();
        services.AddSingleton<IStartupFilter, WebInjection>();
    }
}

public sealed class NotificationWorker(IServiceProvider services, ILogger<NotificationWorker> logger):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken token)
    {
        // Resolve protected storage lazily: corrupt/missing plugin state must not
        // prevent Jellyfin or unrelated plugins from starting.
        while(!token.IsCancellationRequested)
        {
            try {
                using var worker=ActivatorUtilities.CreateInstance<DeliveryWorker>(services);
                await worker.StartAsync(token);
                try { if(worker.ExecuteTask is { } task)await task; }
                finally { await worker.StopAsync(CancellationToken.None); }
            }
            catch(OperationCanceledException)when(token.IsCancellationRequested){break;}
            catch { logger.LogWarning("Notifications unavailable; restore its original storage and keys. No state was reset."); }
            try{await Task.Delay(TimeSpan.FromSeconds(30),token);}catch(OperationCanceledException)when(token.IsCancellationRequested){break;}
        }
    }
}
