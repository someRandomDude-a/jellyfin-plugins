using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AccountHub.Security;

public static class ServiceCollectionExtensions
{
    // Host must provide ISecurityHubHost, ISecurityReceiptStore and ISecurityIngressBudget.
    // No permissive default implementations: unresolved foundation dependencies fail closed.
    public static IServiceCollection AddAccountHubSecurity(this IServiceCollection services)
    {
        services.AddJellyfinSecurityClient();
        services.AddSingleton<SecurityWebhook>();
        return services;
    }
}
