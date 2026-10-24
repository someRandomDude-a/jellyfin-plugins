using AccountHub.Notifications;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace AccountHub;

public sealed class HubStartup : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.UseMiddleware<HostedWebBootstrap>();
        app.UseMiddleware<NativeUserCreationMiddleware>();
        // Jellyfin owns routing inside its BaseUrl branch. Never append a second router here.
        next(app);
    };
}
