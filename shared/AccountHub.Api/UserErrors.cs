namespace AccountHub.Api;

/// <summary>Safe user-facing error categories. Technical codes and context belong in administrator diagnostics.</summary>
public static class UserErrors
{
    public static string Nature(string? code,int status=0)
    {
        var key=(code??"").ToLowerInvariant();
        var compact=key.Replace("_","",StringComparison.Ordinal).Replace("-","",StringComparison.Ordinal);
        if(key.Contains("uncertain",StringComparison.Ordinal)||key is "remediation_pending" or "account_timeout" or "command_outcome_unknown")return "uncertain";
        if(key is "invalid_username" or "username_unavailable")return "username";
        if(key is "active_device_required")return "playback_required";
        if(key is "link_required")return "link_required";
        if(compact is "seerrnotconfigured" or "notificationsrequired" or "upstreamunavailable" or "integrityunavailable")return "unavailable";
        if(compact is "targetunavailable" or "targeteditionunverified" or "seerrtargetnotmanaged" or "seerrserverassociationunverified")return "action_unavailable";
        if(compact is "proposalnotpermitted")return "authorization";
        if(key is "step_up_required" or "two_factor_required" or "reset_session_required" or "unauthorized")return "authentication";
        if(key is "rate_limited" or "native_rate_limited")return "rate_limited";
        if(key is "link_expired" or "invalid_or_expired" or "invite_unavailable" or "not_found")return "expired";
        if(key is "revision_changed" or "snapshot_changed" or "action_changed" or "command_conflict")return "changed";
        if(key is "forbidden" or "permission_denied" or "party_permission_denied" or "native_party_denied" or "native_permission_denied")return "authorization";
        if(key is "invite_limits" or "invalid_request" or "invalid_party_recipient" or "native_invalid_request")return "validation";
        if(key.Contains("unavailable",StringComparison.Ordinal)||key.EndsWith("_missing",StringComparison.Ordinal)||key.EndsWith("_disabled",StringComparison.Ordinal)||key is "public_url_required" or "party_public_url_required"||status>=500)return "unavailable";
        return status switch{401=>"authentication",403=>"authorization",404 or 410=>"expired",409=>"changed",429=>"rate_limited",400 or 413 or 422=>"validation",_=>"request_failed"};
    }
    public static string Message(string? code,int status=0)=>Nature(code,status) switch {
        "username"=>"Invalid or unavailable username. Choose another username.",
        "validation"=>"Some details are invalid. Check your entries and try again.",
        "authentication"=>"Your session needs attention. Complete Jellyfin sign-in and verification to continue.",
        "authorization"=>"You do not have permission to perform this action.",
        "expired"=>"This item or link is unavailable or has expired.",
        "changed"=>"This item changed. Refresh and review it before trying again.",
        "rate_limited"=>"Too many attempts. Please wait before trying again.",
        "unavailable"=>"This service is currently unavailable. Contact an administrator if the problem continues.",
        "action_unavailable"=>"This action is unavailable for this item. Contact an administrator if you need help.",
        "uncertain"=>"We could not confirm the result. Check the current state or contact an administrator before trying again.",
        "playback_required"=>"Open Jellyfin on your playback device and start playing before trying again.",
        "link_required"=>"Use /link to review and confirm your Jellyfin account connection.",
        _=>"Unable to complete this request. Contact an administrator if the problem continues."
    };
}
