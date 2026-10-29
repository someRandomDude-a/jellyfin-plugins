namespace Jellyfin.Plugin.DiscordBridge;

public sealed record BridgePermission(string Id, string Name, string Description, bool DefaultAllowed);

/// <summary>Optional bot-feature ceilings. Never grant native media, guest or administrator authority.</summary>
public sealed class BridgePermissionSettings
{
    public Dictionary<string, bool> Defaults { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<Guid, Dictionary<string, bool>> Users { get; set; } = [];
}

public static class BridgePermissions
{
    public static readonly BridgePermission[] Catalog = [
        new("link", "Link account", "Begin linking; the signed-in account's override is checked at confirmation.", true),
        new("unlink", "Unlink own account", "Remove only the caller's verified link.", true),
        new("status", "View own account status", "Private status of the caller's account only.", true),
        new("reset", "Request own recovery link", "Native same-account verified-session recovery still applies.", true),
        new("invite.create", "Create account invitations", "Also requires Account Hub's creator delegation, template and quota.", false),
        new("party.invite", "Create watch-party invitations", "Native party/session/media permissions still apply.", true),
        new("party.guests", "Invite guest viewers", "Also requires administrator-enabled native guest sharing.", false),
        new("party.share.dm", "Send party invitation by DM", "Only the explicitly selected recipient; no automatic resend.", true),
        new("party.share.channel", "Share party invitation in channel", "The default /watchparty destination; requires an administrator-approved channel.", true)
    ];

    public static bool Allowed(Configuration settings, string action, Guid? user = null)
    {
        var definition = Catalog.SingleOrDefault(item => item.Id == action);
        if (definition is null || settings.Permissions is null) return false;
        if (user is { } id && id != Guid.Empty && settings.Permissions.Users.TryGetValue(id, out var rules) && rules.TryGetValue(action, out var allowed)) return allowed;
        return settings.Permissions.Defaults.TryGetValue(action, out var fallback) ? fallback : definition.DefaultAllowed;
    }

    public static void Require(Configuration settings, string action, Guid? user = null)
    {
        var enabled = action.StartsWith("party.", StringComparison.Ordinal) ? settings.EnableWatchPartyCommands : settings.EnableSelfServiceCommands;
        if (!enabled || !Allowed(settings, action, user)) throw new UnauthorizedAccessException("This Discord action is not permitted by the administrator.");
    }

    public static bool Permitted(Configuration settings, string action, Guid user)
    {
        // Persistent native-user policy must not depend on the bot being online.
        // Interaction admission/delivery separately enforce transport availability.
        var enabled = action.StartsWith("party.", StringComparison.Ordinal) ? settings.EnableWatchPartyCommands : settings.EnableSelfServiceCommands;
        return enabled && Allowed(settings, action, user);
    }

    public static void Validate(BridgePermissionSettings? value)
    {
        var names = Catalog.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        if (value is null || value.Defaults is null || value.Users is null || value.Users.Count > 100 ||
            value.Defaults.Keys.Any(key => !names.Contains(key)) || value.Users.Any(entry => entry.Key == Guid.Empty || entry.Value is null || entry.Value.Keys.Any(key => !names.Contains(key))))
            throw new ArgumentException("Invalid Discord permission policy.");
    }
}
