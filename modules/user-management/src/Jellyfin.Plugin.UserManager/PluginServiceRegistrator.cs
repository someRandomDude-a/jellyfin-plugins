using Jellyfin.Plugin.UserManager.Api;
using Jellyfin.Plugin.UserManager.Discord;
using Jellyfin.Plugin.UserManager.FileTransformation;
using Jellyfin.Plugin.UserManager.Security;
using Jellyfin.Plugin.UserManager.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.UserManager;

/// <summary>
/// Registers plugin services with Jellyfin's dependency injection container.
/// </summary>
public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost applicationHost)
    {
        // Service registration runs before Jellyfin constructs the plugin instance, and hosted
        // services are resolved during host.Build. Never touch Plugin.Instance here; depend on
        // Jellyfin's already registered IApplicationPaths instead.
        var version = typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "1.0.0";

        services.AddHttpClient("UserManager", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(20);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Jellyfin-UserManager/" + version);
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
        });

        services.AddSingleton(sp =>
        {
            var paths = sp.GetRequiredService<IApplicationPaths>();
            return new SecretProtector(Plugin.EnsureDataFolder(paths));
        });
        services.AddSingleton(sp =>
        {
            var paths = sp.GetRequiredService<IApplicationPaths>();
            return new PluginStore(Plugin.EnsureDataFolder(paths));
        });
        services.AddSingleton<PluginSettingsService>();
        services.AddSingleton<RateLimiter>();
        services.AddSingleton<DiscordWebhookClient>();
        services.AddSingleton<AuditService>();
        services.AddSingleton<JellyfinUserService>();
        services.AddSingleton<SeerrClient>();
        services.AddSingleton<NotificationService>();
        services.AddSingleton<PresetService>();
        services.AddSingleton<AccountService>();
        services.AddSingleton<InviteService>();

        services.AddSingleton<Lazy<IDiscordMessageSender>>(sp =>
            new Lazy<IDiscordMessageSender>(() => sp.GetRequiredService<DiscordBotService>()));
        services.AddSingleton<DiscordBotService>();
        services.AddSingleton<IDiscordMessageSender>(sp => sp.GetRequiredService<DiscordBotService>());
        services.AddHostedService<DiscordBotHostedService>();

        services.AddSingleton<FileTransformationBridge>();
        services.AddHostedService(sp => sp.GetRequiredService<FileTransformationBridge>());
        services.AddHostedService<StartupInitializer>();

        services.AddSingleton<DiscordPasswordResetProvider>();
        services.AddSingleton<IPasswordResetProvider>(sp => sp.GetRequiredService<DiscordPasswordResetProvider>());

        services.AddSingleton<ApiExceptionFilter>();
        services.AddTransient<AdminController>();
        services.AddTransient<PresetsController>();
        services.AddTransient<InvitesController>();
        services.AddTransient<AuditController>();
        services.AddTransient<SettingsController>();
        services.AddTransient<AccountController>();
        services.AddTransient<PublicController>();
        services.AddTransient<PagesController>();
    }
}

/// <summary>
/// Initializes the database and seeds the default permission presets.
/// </summary>
public sealed class StartupInitializer : IHostedService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<StartupInitializer> _logger;

    public StartupInitializer(IServiceProvider services, ILogger<StartupInitializer> logger)
    {
        _services = services;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Resolve lazily: hosted services are constructed during host.Build, before
            // Jellyfin's database-backed singletons are safe to use.
            var store = _services.GetRequiredService<PluginStore>();
            var presets = _services.GetRequiredService<PresetService>();
            var settings = _services.GetRequiredService<PluginSettingsService>();

            await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
            await presets.SeedDefaultsAsync(cancellationToken).ConfigureAwait(false);
            await settings.ReloadAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("User Manager database ready at {Path}", store.DatabasePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "User Manager failed to initialize its database");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
