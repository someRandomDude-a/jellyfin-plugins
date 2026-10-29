using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AccountHub.Security;

public static class SecurityClientRegistration
{
    // Jellyfin loads each plugin's copy in a separate ALC. The default typed-client name
    // collides even when both copies have the same qualified type name. Scope it per copy.
    private static readonly string ClientName = "Jellyfin.Security." + Guid.NewGuid().ToString("N");
    public static IServiceCollection AddJellyfinSecurityClient(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ISecurityCompatibility, JellyfinSecurityCompatibility>();
        services.TryAddSingleton<ISecuritySessionVerification, JellyfinSecuritySessionVerification>();
        services.TryAddSingleton<ReadModelCache>();
        services.AddHttpClient<SecurityClient>(ClientName, client => client.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(5) }).RemoveAllLoggers();
        services.TryAddTransient<CommandAuthorization>();
        return services;
    }
}
