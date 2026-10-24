using AccountHub.Api.Contracts;
using AccountHub.Core;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using AccountHub.Notifications;
using AccountHub.Security;
using DiscordBridge.Api;
using Jellyfin.Plugin.UserManager.Data;
using Jellyfin.Plugin.UserManager.Services;
using Jellyfin.Plugin.UserManager.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AccountHub;

public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost applicationHost)
    {
        services.AddSingleton(sp => new HubPaths(Path.Combine(
            sp.GetRequiredService<IApplicationPaths>().PluginConfigurationsPath, "AccountHub")));
        services.AddSingleton<HubStore>(sp => new(sp.GetRequiredService<HubPaths>(), TimeProvider.System));
        services.AddSingleton<ProducerDispatcher>();
        services.AddSingleton<AccountHub.Api.NotificationEndpointAdapter>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<CommandGate>();
        services.AddSingleton<HubSettingsService>();
        services.AddSingleton<ExternalCommandGate>();
        services.AddSingleton<DiscordLinkService>();
        services.AddSingleton<IntegrationSecretService>();
        services.AddSingleton<HubHost>();
        services.AddSingleton<HubWorkerHealth>();
        services.AddSingleton<NativeUserWrites>();
        services.AddSingleton<UserCreationOwnership>();
        services.AddSingleton<NativeUserIntegrity>();
        services.AddHostedService(sp => sp.GetRequiredService<NativeUserIntegrity>());
        services.AddSingleton<IHubMaintenance, HubMaintenance>();
        AccountHub.Groups.GroupPolicyRegistration.AddHubGroupPolicy(services);
        AccountHub.Seerr.SeerrRegistration.AddHubSeerr(services);
        services.DeferHubWorker<AccountHub.Seerr.SeerrReconcileWorker>();
        services.AddSingleton<IInviteNativeProvisioner, InviteNativeProvisioner>();
        services.AddSingleton<InviteOnboardingService>();
        services.AddHostedService<InviteDeliveryWorker>();
        services.DeferHubWorker<InviteDeliveryWorker>();
        services.AddSingleton<Jellyfin.Plugin.PrivateSyncPlay.NativeWebStyles>();
        services.TryAddSingleton<INotificationHost>(sp => sp.GetRequiredService<HubHost>());
        services.AddSingleton<ISecurityHubHost>(sp => sp.GetRequiredService<HubHost>());
        services.AddAccountHubSecurity();
        services.AddSingleton<DiscordBridgeClient>();
        services.AddSingleton<Jellyfin.LibraryNotifications.LibraryNoticeStore>(sp => new(sp.GetRequiredService<HubPaths>().Database));
        services.AddSingleton<Jellyfin.LibraryNotifications.ILibraryNoticeHost, HubLibraryNotices>();
        services.AddHostedService<Jellyfin.LibraryNotifications.LibraryNoticeWorker>();
        services.TryAddSingleton<IDiscordTransport, DiscordTransport>();
        // Never replace Jellyfin's global Data Protection provider with plugin-owned keys.
        services.TryAddSingleton<NotificationStore>(sp => new(sp.GetRequiredService<NotificationOptions>(), sp.GetRequiredService<HubStore>().Protection, TimeProvider.System));
        services.AddAccountHubNotifications(sp => new NotificationOptions { DatabasePath = sp.GetRequiredService<HubPaths>().Database }, startWorker: false, registerProducer: false);
        services.AddKeyedSingleton<Func<AccountHub.Api.AuditEvent, CancellationToken, Task<object>>>("Notifications.HubAudit.1", (sp, _) => sp.GetRequiredService<HubHost>().AppendAuditAsync);
        services.AddKeyedSingleton<Func<Guid, CancellationToken, Task<string?>>>("Notifications.DiscordLink.1", (sp, _) => sp.GetRequiredService<HubHost>().DiscordLinkAsync);
        services.AddSingleton<ISecurityReceiptStore, SecurityReceiptAdapter>();
        services.AddSingleton<ISecurityIngressBudget, SecurityIngressBudget>();
        services.AddControllers().AddApplicationPart(typeof(SecurityController).Assembly);
        services.Configure<Microsoft.AspNetCore.Mvc.MvcOptions>(options => options.Filters.Add(new Microsoft.AspNetCore.Mvc.TypeFilterAttribute(typeof(AccountHub.Api.HubBodyFilter))));
        services.AddSingleton<PluginStore>(sp => new(sp.GetRequiredService<HubPaths>().DataDirectory, "account-hub.db"));
        services.AddSingleton<SecretProtector>(sp => new(sp.GetRequiredService<HubPaths>().DataDirectory));
        services.AddSingleton<PluginSettingsService>();
        services.AddSingleton<IIntegrationSecretReader, LifecycleSecretReader>();
        services.AddSingleton<JellyfinUserService>();
        services.AddSingleton<SeerrClient>();
        services.AddSingleton<AuditService>();
        services.AddSingleton<IAuditSink, LifecycleAuditSink>();
        services.AddSingleton<IAdminNoticeSink, LifecycleNoticeSink>();
        services.AddSingleton<PresetService>();
        services.AddSingleton<AccountService>();
        services.AddSingleton<InviteService>();
        services.AddSingleton<Jellyfin.Plugin.UserManager.Services.NotificationService>();
        services.AddSingleton<DiscordWebhookClient>();
        services.AddSingleton<IDiscordMessageSender, LifecycleDiscordSender>();
        services.AddSingleton(sp => new Lazy<IDiscordMessageSender>(() => sp.GetRequiredService<IDiscordMessageSender>()));
        services.AddHttpClient("UserManager", client => client.Timeout = TimeSpan.FromSeconds(20)).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }).RemoveAllLoggers();
        services.AddHttpClient("AccountHub.Local", client => client.Timeout = TimeSpan.FromSeconds(25)).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false }).RemoveAllLoggers();
        services.AddKeyedSingleton<Func<string, CancellationToken, Task<string>>>(Wire.ServiceKey,
            (sp, _) => (json, ct) => sp.GetRequiredService<HubStore>().Ready
                ? sp.GetRequiredService<AccountHub.Notifications.NotificationService>().DispatchAsync(json, ct)
                : sp.GetRequiredService<ProducerDispatcher>().DispatchAsync(json, ct));
        services.AddKeyedSingleton<Func<Microsoft.AspNetCore.Http.HttpContext, string, CancellationToken, Task<string>>>("AccountHub.ManagementAuthorization.1",
            (sp, _) => async (context, operation, ct) =>
            {
                try
                {
                    if (operation != "discord.settings") throw new SecurityFailure("invalid_request", 400);
                    await sp.GetRequiredService<HubHost>().AuthorizeAsync(context, CommandRisk.Operational, ct);
                    return System.Text.Json.JsonSerializer.Serialize(new { ok = true });
                }
                catch (SecurityFailure ex) { return System.Text.Json.JsonSerializer.Serialize(new { ok = false, code = ex.Code, status = ex.Status }); }
                catch (HubException) { return "{\"ok\":false,\"code\":\"unavailable\",\"status\":503}"; }
            });
        services.AddSingleton<DiscordIdentity>();
        services.AddKeyedSingleton<Func<string, CancellationToken, Task<string>>>(DiscordBridgeClient.IdentityKey,
            (sp, _) => (json, ct) => sp.GetRequiredService<DiscordIdentity>().DispatchAsync(json, ct));
        services.AddSingleton<IStartupFilter, HubStartup>();
        services.AddHostedService<FoundationWorker>();
        services.AddHostedService<CompositeWorker>();
    }
}
