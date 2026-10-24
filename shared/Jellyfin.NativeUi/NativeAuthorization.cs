using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Data;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.PluginSupport;

// Shared source, not a second authentication system. Native Jellyfin remains authoritative.
public sealed class NativeAuthorizationFailure : Exception { }
public static class NativeAuthorization
{
    public static async Task<AuthorizationInfo> RequireAsync(HttpContext context, IServiceProvider services, bool administrator, bool elevated)
    {
        var info = await services.GetRequiredService<IAuthorizationContext>().GetAuthorizationInfo(context);
        var header = context.Request.Headers.Authorization.ToString();
        var supplied = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..] : System.Text.RegularExpressions.Regex.Match(header, "Token=\"([^\"]+)\"", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Groups[1].Value;
        if (context.Request.Headers.Authorization.Count != 1 || !info.IsAuthenticated || info.IsApiKey || info.UserId == Guid.Empty || string.IsNullOrEmpty(supplied) || !string.Equals(supplied, info.Token, StringComparison.Ordinal)) throw new NativeAuthorizationFailure();
        if (context.Request.Headers.TryGetValue("Origin", out var origin) && origin != $"{context.Request.Scheme}://{context.Request.Host}") throw new NativeAuthorizationFailure();
        var user = services.GetRequiredService<IUserManager>().GetUserById(info.UserId);
        if (user is null || user.HasPermission(PermissionKind.IsDisabled) || administrator && !user.HasPermission(PermissionKind.IsAdministrator)) throw new NativeAuthorizationFailure();
        if (elevated)
        {
            using var scope = services.CreateScope();
            if (!(await scope.ServiceProvider.GetRequiredService<IAuthorizationService>().AuthorizeAsync(context.User, null, Policies.RequiresElevation)).Succeeded) throw new NativeAuthorizationFailure();
        }
        return info;
    }
}
