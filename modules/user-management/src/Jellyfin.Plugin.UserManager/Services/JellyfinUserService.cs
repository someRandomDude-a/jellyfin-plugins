using System.Security.Claims;
using System.Text.RegularExpressions;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.UserManager.Security;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Users;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.UserManager.Services;

/// <summary>
/// Wraps Jellyfin's user manager with the safer create/apply flows used by this plugin.
/// </summary>
public sealed class JellyfinUserService
{
    public const string UserIdClaim = "Jellyfin-UserId";

    private static readonly Regex ControlCharacters = new(@"[\u0000-\u001F\u007F]", RegexOptions.Compiled);

    private readonly IServiceProvider _services;

    public JellyfinUserService(IServiceProvider services)
    {
        // Resolve IUserManager lazily. Plugins are loaded during host.Build, before Jellyfin's
        // user manager is safe to construct; keeping this as a dependency would deadlock startup.
        _services = services;
    }

    private IUserManager UserManager => _services.GetRequiredService<IUserManager>();

    public IEnumerable<User> GetUsers() =>
        UserManager.GetUsers().OrderBy(u => u.Username, StringComparer.OrdinalIgnoreCase);

    public User? GetUser(Guid id) => UserManager.GetUserById(id);

    public User? GetUser(string username) => UserManager.GetUserByName(username);

    public bool IsAdministrator(Guid userId) =>
        UserManager.GetUserById(userId)?.HasPermission(PermissionKind.IsAdministrator) ?? false;

    public bool IsDisabled(Guid userId) =>
        UserManager.GetUserById(userId)?.HasPermission(PermissionKind.IsDisabled) ?? true;

    public static Guid? GetUserId(ClaimsPrincipal principal)
    {
        var value = principal.FindFirst(UserIdClaim)?.Value ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(value, out var id) ? id : null;
    }

    public bool IsAdministrator(ClaimsPrincipal principal) =>
        principal.IsInRole("Administrator")
        || (GetUserId(principal) is Guid id && IsAdministrator(id));

    public MediaBrowser.Model.Dto.UserDto GetDto(Guid userId)
    {
        var user = UserManager.GetUserById(userId)
            ?? throw new UserManagerException(404, "User not found.");
        return UserManager.GetUserDto(user);
    }

    public UserPolicy GetPolicy(Guid userId)
    {
        var user = UserManager.GetUserById(userId)
            ?? throw new UserManagerException(404, "User not found.");
        return PolicyTools.Normalize(UserManager.GetUserDto(user).Policy);
    }

    /// <summary>
    /// Creates a user with the chosen password before enabling the account. No placeholder password is ever set.
    /// </summary>
    public async Task<User> CreateUserAsync(
        string username,
        string password,
        UserPolicy? presetPolicy = null,
        bool isAdministrator = false,
        string? passwordResetProviderId = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var name = ValidateUsername(username);
        PasswordPolicy.ThrowIfInvalid(password, name);

        if (UserManager.GetUserByName(name) is not null)
        {
            throw new UserManagerException(409, "That username is already taken.");
        }

        var user = await UserManager.CreateUserAsync(name).ConfigureAwait(false);
        try
        {
            var policy = GetPolicy(user.Id);
            policy.IsDisabled = true;
            policy.IsHidden = true;
            policy.AuthenticationProviderId = user.AuthenticationProviderId;
            if (!string.IsNullOrWhiteSpace(passwordResetProviderId))
            {
                policy.PasswordResetProviderId = passwordResetProviderId;
            }

            await UserManager.UpdatePolicyAsync(user.Id, policy).ConfigureAwait(false);

            // Set the real password while the account is disabled; there is no empty-password window.
            await UserManager.ChangePassword(user.Id, password).ConfigureAwait(false);

            if (presetPolicy is not null)
            {
                await ApplyPresetAsync(user.Id, presetPolicy).ConfigureAwait(false);
            }

            if (isAdministrator)
            {
                await SetAdministratorAsync(user.Id, true).ConfigureAwait(false);
            }

            await SetDisabledAsync(user.Id, false, makeVisible: true).ConfigureAwait(false);

            return UserManager.GetUserById(user.Id)
                ?? throw new UserManagerException(500, "The user was created but could not be reloaded.");
        }
        catch
        {
            try
            {
                await UserManager.DeleteUserAsync(user.Id).ConfigureAwait(false);
            }
            catch
            {
                // Preserve the original failure.
            }

            throw;
        }
    }

    public async Task ApplyPresetAsync(Guid userId, UserPolicy presetPolicy, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var current = GetPolicy(userId);
        var next = PolicyTools.ApplyPreset(current, presetPolicy);
        await UserManager.UpdatePolicyAsync(userId, next).ConfigureAwait(false);
    }

    public async Task SetAdministratorAsync(Guid userId, bool isAdmin, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var policy = GetPolicy(userId);
        if (policy.IsAdministrator == isAdmin)
        {
            return;
        }

        policy.IsAdministrator = isAdmin;
        await UserManager.UpdatePolicyAsync(userId, policy).ConfigureAwait(false);
    }

    public async Task SetDisabledAsync(Guid userId, bool disabled, bool makeVisible = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var policy = GetPolicy(userId);
        policy.IsDisabled = disabled;
        if (makeVisible && disabled == false)
        {
            policy.IsHidden = false;
        }

        await UserManager.UpdatePolicyAsync(userId, policy).ConfigureAwait(false);
    }

    public async Task ChangePasswordAsync(Guid userId, string newPassword, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var user = UserManager.GetUserById(userId)
            ?? throw new UserManagerException(404, "User not found.");
        PasswordPolicy.ThrowIfInvalid(newPassword, user.Username);
        await UserManager.ChangePassword(userId, newPassword).ConfigureAwait(false);
    }

    public Task RevokeSessionsAsync(Guid userId) => _services.GetRequiredService<MediaBrowser.Controller.Session.ISessionManager>().RevokeUserTokens(userId, string.Empty);

    public async Task<bool> VerifyPasswordAsync(Guid userId, string password, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var user = UserManager.GetUserById(userId);
        if (user is null)
        {
            return false;
        }

        try
        {
            var authenticated = await UserManager
                .AuthenticateUser(user.Username, password, "127.0.0.1", false)
                .ConfigureAwait(false);
            return authenticated?.Id == userId;
        }
        catch
        {
            return false;
        }
    }

    public async Task DeleteUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await UserManager.DeleteUserAsync(userId).ConfigureAwait(false);
    }

    public int CountAdministrators() =>
        UserManager.GetUsers().Count(u => u.HasPermission(PermissionKind.IsAdministrator));

    private static string ValidateUsername(string username)
    {
        var name = username?.Trim() ?? string.Empty;
        if (name.Length is < 1 or > 32)
        {
            throw new UserManagerException(400, "Usernames must be between 1 and 32 characters.");
        }

        if (ControlCharacters.IsMatch(name))
        {
            throw new UserManagerException(400, "The username contains invalid characters.");
        }

        return name;
    }
}

public sealed class UserManagerException : Exception
{
    public UserManagerException(int statusCode, string message)
        : base(message)
    {
        StatusCode = statusCode;
    }

    public int StatusCode { get; }
}
