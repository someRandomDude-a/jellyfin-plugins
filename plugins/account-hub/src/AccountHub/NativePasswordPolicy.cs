using AccountHub.Core;

namespace AccountHub;

public static class NativePasswordPolicy
{
    public static bool Disabled(object configuration)
    {
        try
        {
            var property = configuration.GetType().GetProperty("DisablePasswordLogin", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (property?.PropertyType == typeof(bool) && property.GetIndexParameters().Length == 0 && property.GetValue(configuration) is bool disabled) return disabled;
        }
        catch { /* Unknown/failed public policy reads fail closed without logging configuration. */ }
        throw new HubException("password_policy_unavailable", "Native password policy cannot be verified", true);
    }
}
