using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Model.Users;

namespace Jellyfin.Plugin.UserManager.Services;

/// <summary>
/// Integrates the built-in "forgot password" dialog with the Discord reset flow.
/// The user always sees a generic "contact your administrator" result, and a linked user
/// receives a one-time reset link in Discord instead.
/// </summary>
public sealed class DiscordPasswordResetProvider : IPasswordResetProvider
{
    public const string ProviderId = "Jellyfin.Plugin.UserManager.Services.DiscordPasswordResetProvider";

    private readonly AccountService _accounts;
    private readonly PluginSettingsService _settings;
    private readonly ILogger<DiscordPasswordResetProvider> _logger;

    public DiscordPasswordResetProvider(
        AccountService accounts,
        PluginSettingsService settings,
        ILogger<DiscordPasswordResetProvider> logger)
    {
        _accounts = accounts;
        _settings = settings;
        _logger = logger;
    }

    public string Name => "Discord";

    public bool IsEnabled => true;

#pragma warning disable CS0618 // ContactAdmin is the only non-leaking result
    public async Task<ForgotPasswordResult> StartForgotPasswordProcess(User? user, string enteredUsername, bool isInNetwork)
    {
        try
        {
            if (user is not null)
            {
                var settings = await _settings.GetAsync().ConfigureAwait(false);
                var link = await _accounts.GetLinkAsync(user.Id).ConfigureAwait(false);
                if (settings.DiscordConfigured
                    && link is not null
                    && !string.IsNullOrWhiteSpace(settings.PublicBaseUrl))
                {
                    var reset = await _accounts.CreateResetTokenAsync(user.Id, null, settings.PublicBaseUrl).ConfigureAwait(false);
                    await _accounts.TrySendResetAsync(user.Id, reset).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Discord forgot-password flow failed");
        }

        return new ForgotPasswordResult
        {
            Action = ForgotPasswordAction.ContactAdmin,
            PinFile = string.Empty,
            PinExpirationDate = null
        };
    }
#pragma warning restore CS0618

    public Task<PinRedeemResult> RedeemPasswordResetPin(string pin) =>
        Task.FromResult(new PinRedeemResult { Success = false, UsersReset = [] });
}
