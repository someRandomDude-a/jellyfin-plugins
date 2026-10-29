namespace Jellyfin.Plugin.DiscordBridge;

// Only fixed, privacy-safe guidance crosses into Discord replies. Never forward
// provider exception text, response bodies, credentials or another user's state.
public sealed class AccountCommandException(string message,string code="account_unavailable") : Exception(message)
{
    private static readonly HashSet<string> Codes=["link_required","forbidden","permission_denied","rate_limited","invite_limits","password_login_disabled","public_url_required","delivery_unavailable","not_found","revision_changed","account_commands_disabled","account_provider_missing","account_unavailable","party_commands_disabled","active_device_required","party_provider_missing","party_permission_denied","invalid_party_recipient","party_public_url_required","native_party_denied"];
    public string Code { get; }=Codes.Contains(code)?code:"account_unavailable";
    public string? ErrorId { get; init; }
    public static AccountCommandException FromCode(string? code,string? errorId=null) => new(code switch
    {
        "link_required" => "Use /link to review and confirm your Jellyfin account connection, then try again.",
        "forbidden" or "permission_denied" => "This Discord action is not permitted for your account. Ask the Jellyfin administrator to review Discord permissions.",
        "rate_limited" => "Request limit reached. Try again later.",
        "invite_limits" => "The signup owner's invitation limits are tighter than these options. Choose fewer uses or a shorter expiry, or ask the administrator to align Account Hub and Discord Bridge limits.",
        "party_commands_disabled" => "Watch-party commands are disabled. Ask an administrator to enable them in Discord Bridge settings.",
        "active_device_required" => "Open Jellyfin on your playback device and start the movie or episode, then run /watchparty again. No invitation was created.",
        "party_provider_missing" => "Private SyncPlay is unavailable. Ask an administrator to install or enable it and restart Jellyfin.",
        "party_permission_denied" => "This watch-party action is not permitted. Ask an administrator to review your Discord and native guest permissions.",
        "invalid_party_recipient" => "Choose a real consenting user, not a bot. No invitation was created.",
        "party_public_url_required" => "An administrator must configure a public HTTPS URL in Discord Bridge or Account Hub, including the server BaseUrl.",
        "native_party_denied" => "Native Private SyncPlay permissions, limits or current party state rejected this request. Check the native party before trying again; ask an administrator to review the configured party defaults. Nothing was automatically resent.",
        "password_login_disabled" => "The sign-in policy does not allow this. Contact an administrator.",
        "public_url_required" => "An administrator must configure the server's public HTTPS URL in Accounts settings first.",
        "delivery_unavailable" => "Unable to send a DM. The link was revoked. Enable server DMs and request a new link.",
        "not_found" => "The invitation template is unavailable. Ask an administrator to select a current default template.",
        "revision_changed" => "Settings changed. Review them and try again.",
        _ => "Account service unavailable. Ask an administrator to check integration and storage health."
    },code??"account_unavailable"){ErrorId=Guid.TryParseExact(errorId,"D",out _)?errorId:null};
}
