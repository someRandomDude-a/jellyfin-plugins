using Microsoft.AspNetCore.DataProtection;

namespace AccountHub.Notifications;

/// <summary>
/// Dedicated short-lived secret path. The lifecycle owner stores this encrypted capsule in its
/// issuance transaction, NEVER in a notification/event/audit row. Erase on success/expiry.
/// </summary>
public sealed class SecretDelivery(IDataProtectionProvider protection, IDiscordTransport bot, TimeProvider time)
{
    private readonly ITimeLimitedDataProtector protector = protection.CreateProtector("AccountHub.SecretDelivery.v1").ToTimeLimitedDataProtector();
    public string Seal(string discordRecipient, Uri fragmentLink, TimeSpan lifetime)
    {
        Validation.Require(discordRecipient.Length is >= 1 and <= 30 && discordRecipient.All(char.IsAsciiDigit), "Invalid Discord recipient");
        Validation.Require(fragmentLink.Scheme == "https" && !string.IsNullOrEmpty(fragmentLink.Fragment) && string.IsNullOrEmpty(fragmentLink.Query) && string.IsNullOrEmpty(fragmentLink.UserInfo), "Secret links require HTTPS and fragment tokens");
        Validation.Require(lifetime > TimeSpan.Zero && lifetime <= TimeSpan.FromHours(6), "Invalid capsule lifetime");
        return protector.Protect(discordRecipient + "\n" + fragmentLink.AbsoluteUri, time.GetUtcNow().Add(lifetime));
    }
    public async Task<SendResult> SendAsync(string capsule, Guid deliveryId, CancellationToken ct)
    {
        try
        {
            var clear = protector.Unprotect(capsule, out var expiration);
            if (expiration <= time.GetUtcNow()) return new(false, false, "capsule_expired");
            var separator = clear.IndexOf('\n');
            return await bot.SendAsync(clear[..separator], true, "Your private Account Hub link: " + clear[(separator + 1)..], deliveryId.ToString(), ct);
        }
        catch (System.Security.Cryptography.CryptographicException) { return new(false, false, "capsule_expired_or_invalid"); }
    }
}
