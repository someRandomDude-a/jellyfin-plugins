using System.Reflection;
using System.Security.Claims;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Jellyfin.Plugin.PrivateSyncPlay.Tests;

public sealed class SessionAccessTests
{
    public class Proxy : DispatchProxy
    {
        public User User = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name == "GetUserById" ? User : throw new NotSupportedException(method.Name);
    }

    [Theory]
    [InlineData(false, "false", false, false)]
    [InlineData(true, "true", false, false)]
    [InlineData(true, "", false, false)]
    [InlineData(true, "false", true, false)]
    [InlineData(true, "false", false, true)]
    public async Task UserEndpointsRejectMissingSessionApiKeysAndRevokedNativeAccessBeforeSessionActivity(bool authenticated, string apiKey, bool disabled, bool revoked)
    {
        var user = new User("member", "auth", "reset") { Id = Guid.NewGuid(), SyncPlayAccess = revoked ? SyncPlayUserAccessType.None : SyncPlayUserAccessType.CreateAndJoinGroups };
        if (disabled) user.Permissions.Add(new Permission(PermissionKind.IsDisabled, true));
        var users = DispatchProxy.Create<IUserManager, Proxy>(); ((Proxy)(object)users).User = user;
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new("Jellyfin-UserId", user.Id.ToString()), new("Jellyfin-DeviceId", "device"), new("Jellyfin-IsApiKey", apiKey)], authenticated ? "fixture" : null));
        var controller = new PartyController(null!, null!, null!, users) { ControllerContext = new() { HttpContext = new DefaultHttpContext { User = principal } } };
        Assert.Equal(403, (await Assert.ThrowsAsync<PartyException>(() => controller.Me())).Status);
    }
}
