namespace AccountHub.Security;

public static class PasswordLogin
{
    // Browser hint is denial-only UX evidence, never grants server authorization.
    // The caller still validates invite/reset tokens and server-controlled local-flow policy.
    public static string Evaluate(SecuritySettings settings, bool compatible, bool? browserDisabled,
        bool serverAllowsLocalFlows)
    {
        settings.Validate();
        if (!serverAllowsLocalFlows || settings.PasswordLoginPolicy == "local_disabled") return "disabled";
        if (settings.PasswordLoginPolicy == "allow_local") return "allowed_with_warning";
        if (!compatible || browserDisabled is null) return "unknown";
        return browserDisabled == true ? "disabled" : "not_reported_disabled";
    }
}
