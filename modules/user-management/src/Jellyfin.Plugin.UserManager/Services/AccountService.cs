using Jellyfin.Plugin.UserManager.Data;
using Jellyfin.Plugin.UserManager.Security;

namespace Jellyfin.Plugin.UserManager.Services;

public sealed record LinkCodeResult(string Code, DateTimeOffset ExpiresAt);

public sealed record ResetTokenResult(string Token, DateTimeOffset ExpiresAt, string Url);

public sealed class AccountService
{
    private readonly PluginStore _store;
    private readonly JellyfinUserService _users;
    private readonly AuditService _audit;
    private readonly PluginSettingsService _settings;
    private readonly NotificationService _notifications;
    private readonly ILogger<AccountService> _logger;

    public AccountService(
        PluginStore store,
        JellyfinUserService users,
        AuditService audit,
        PluginSettingsService settings,
        NotificationService notifications,
        ILogger<AccountService> logger)
    {
        _store = store;
        _users = users;
        _audit = audit;
        _settings = settings;
        _notifications = notifications;
        _logger = logger;
    }

    public async Task<LinkedAccountRecord?> GetLinkAsync(Guid userId, CancellationToken cancellationToken = default) =>
        await _store.GetLinkByJellyfinUserIdAsync(userId.ToString("N"), cancellationToken).ConfigureAwait(false);

    public async Task<LinkedAccountRecord?> GetLinkByDiscordIdAsync(string discordUserId, CancellationToken cancellationToken = default) =>
        await _store.GetLinkByDiscordUserIdAsync(discordUserId, cancellationToken).ConfigureAwait(false);

    public async Task<LinkCodeResult> CreateLinkCodeAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var code = TokenTools.CreateLinkCode();
        var now = DateTimeOffset.UtcNow;
        var expires = now.AddMinutes(10);
        await _store.SetLinkCodeAsync(new LinkCodeRecord
        {
            CodeHash = TokenTools.HashCode(code),
            JellyfinUserId = userId.ToString("N"),
            CreatedAt = now.ToUnixTimeMilliseconds(),
            ExpiresAt = expires.ToUnixTimeMilliseconds()
        }, cancellationToken).ConfigureAwait(false);

        return new LinkCodeResult(code, expires);
    }

    public async Task<LinkedAccountRecord?> CompleteLinkAsync(
        string code,
        string discordUserId,
        string? discordUsername,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var redeemed = await _store.ConsumeLinkCodeAsync(
            TokenTools.HashCode(code),
            discordUserId,
            discordUsername,
            now,
            cancellationToken).ConfigureAwait(false);

        if (redeemed is null)
        {
            return null;
        }

        var user = Guid.TryParse(redeemed.JellyfinUserId, out var userId) ? _users.GetUser(userId) : null;
        await _audit.LogAsync(
            AuditService.ActionDiscordLinked,
            user?.Id.ToString("N") ?? redeemed.JellyfinUserId,
            user?.Username ?? "unknown",
            user?.Id.ToString("N"),
            user?.Username,
            $"Discord account {discordUsername ?? discordUserId} linked.",
            null,
            cancellationToken).ConfigureAwait(false);

        return await _store.GetLinkByJellyfinUserIdAsync(redeemed.JellyfinUserId, cancellationToken).ConfigureAwait(false);
    }

    public async Task UnlinkAsync(Guid userId, string actorId, string actorName, string? ipAddress, CancellationToken cancellationToken = default)
    {
        var user = _users.GetUser(userId);
        await _store.DeleteLinkAsync(userId.ToString("N"), cancellationToken).ConfigureAwait(false);
        await _audit.LogAsync(
            AuditService.ActionDiscordUnlinked,
            actorId,
            actorName,
            userId.ToString("N"),
            user?.Username,
            "Discord account unlinked.",
            ipAddress,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ResetTokenResult> CreateResetTokenAsync(
        Guid userId,
        Guid? createdByUserId,
        string baseUrl,
        TimeSpan? lifetime = null,
        CancellationToken cancellationToken = default)
    {
        var user = _users.GetUser(userId) ?? throw new UserManagerException(404, "User not found.");
        if (_users.IsDisabled(userId)) throw new UserManagerException(403, "Disabled accounts cannot request password recovery.");
        var token = TokenTools.CreateUrlToken();
        var now = DateTimeOffset.UtcNow;
        var expires = now.Add(lifetime ?? TimeSpan.FromMinutes(30));

        await _store.CreateResetTokenAsync(new ResetTokenRecord
        {
            TokenHash = TokenTools.HashToken(token),
            JellyfinUserId = userId.ToString("N"),
            CreatedByUserId = createdByUserId?.ToString("N") ?? "system",
            CreatedAt = now.ToUnixTimeMilliseconds(),
            ExpiresAt = expires.ToUnixTimeMilliseconds()
        }, cancellationToken).ConfigureAwait(false);

        var url = BuildResetUrl(baseUrl, token);
        return new ResetTokenResult(token, expires, url);
    }

    public async Task<(Guid UserId, string Username)> ResetWithTokenAsync(
        string token,
        string newPassword,
        string? ipAddress,
        CancellationToken cancellationToken = default)
    {
        var hash = TokenTools.HashToken(token);
        var record = await _store.GetResetTokenAsync(hash, cancellationToken).ConfigureAwait(false);
        if (record is null
            || record.UsedAt is not null
            || record.ExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
        {
            throw new UserManagerException(400, "This reset link is invalid or has expired.");
        }

        if (!Guid.TryParse(record.JellyfinUserId, out var userId))
        {
            throw new UserManagerException(400, "This reset link is invalid.");
        }

        var user = _users.GetUser(userId) ?? throw new UserManagerException(404, "User not found.");
        if (_users.IsDisabled(userId)) throw new UserManagerException(403, "Disabled accounts cannot reset their password.");
        PasswordPolicy.ThrowIfInvalid(newPassword, user.Username);

        await new ResetOperations(_store).CompleteAsync(hash,
            () => _users.ChangePasswordAsync(userId, newPassword, cancellationToken),
            () => _users.RevokeSessionsAsync(userId), cancellationToken).ConfigureAwait(false);
        await _audit.LogAsync(
            AuditService.ActionPasswordReset,
            "self-service",
            "Discord reset link",
            userId.ToString("N"),
            user.Username,
            "Password reset with a one-time link.",
            ipAddress,
            cancellationToken).ConfigureAwait(false);

        var settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
        if (settings.NotifyAdminsOnPasswordReset)
        {
            await _notifications.NotifyAdminsAsync(
                "Password reset",
                $"{user.Username} reset their password using a Discord link.",
                cancellationToken).ConfigureAwait(false);
        }

        return (userId, user.Username);
    }

    /// <summary>
    /// Starts a reset without revealing whether the username exists. Returns true only for internal logging.
    /// </summary>
    public async Task<bool> RequestResetAsync(string username, string baseUrl, string? ipAddress, CancellationToken cancellationToken = default)
    {
        var settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
        if (!settings.DiscordConfigured)
        {
            return false;
        }

        var user = _users.GetUser(username?.Trim() ?? string.Empty);
        if (user is null)
        {
            return false;
        }

        var link = await GetLinkAsync(user.Id, cancellationToken).ConfigureAwait(false);
        if (link is null)
        {
            return false;
        }

        var reset = await CreateResetTokenAsync(user.Id, null, baseUrl, cancellationToken: cancellationToken).ConfigureAwait(false);
        var sent = await TrySendResetAsync(user.Id, reset, cancellationToken).ConfigureAwait(false);
        if (!sent)
        {
            return false;
        }

        await _audit.LogAsync(
            AuditService.ActionPasswordReset,
            "system",
            "forgot password",
            user.Id.ToString("N"),
            user.Username,
            "Reset link requested and delivered over Discord.",
            ipAddress,
            cancellationToken).ConfigureAwait(false);

        if (settings.NotifyAdminsOnPasswordReset)
        {
            await _notifications.NotifyAdminsAsync(
                "Password reset requested",
                $"{user.Username} requested a password reset link via Discord.",
                cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    public async Task<bool> TrySendResetAsync(Guid userId, ResetTokenResult reset, CancellationToken cancellationToken = default)
    {
        var user = _users.GetUser(userId);
        var message =
            $"**Jellyfin password reset**\nHi {user?.Username ?? "there"}, use this one-time link within 30 minutes:\n{reset.Url}\n\nIf you did not request this, ignore this message.";
        try
        {
            return await _notifications.SendToUserAsync(userId, message, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not deliver reset link to user {UserId}", userId);
            return false;
        }
    }

    public static string BuildResetUrl(string baseUrl, string token) =>
        $"{baseUrl.TrimEnd('/')}/UserManager/reset#token={Uri.EscapeDataString(token)}";
}
