using System.Reflection;
using MediaBrowser.Common.Plugins;

namespace AccountHub.Security;

/// <summary>Native Security's existing session verdict, including its configured bypasses.</summary>
public interface ISecuritySessionVerification
{
    // Null means the pinned native integration cannot be read, never permission to proceed.
    bool? IsVerified(CallerSession caller);
}

public sealed class JellyfinSecuritySessionVerification(IServiceProvider services, IPluginManager plugins, ISecurityCompatibility compatibility) : ISecuritySessionVerification
{
    public bool? IsVerified(CallerSession caller)
    {
        if (string.IsNullOrWhiteSpace(caller.AccessToken) || caller.UserId == Guid.Empty) return false;
        try
        {
            if (!compatibility.Check().IsOk) return null;
            var plugin = plugins.Plugins.SingleOrDefault(p => p.Id == SupportedSecurity.PluginId && p.IsEnabledAndSupported && p.Instance is not null);
            if (plugin?.Instance is null || !SupportedSecurity.Matches(plugin.Instance.Version.ToString())) return null;
            // Resolve the running plugin's own public service type across its ALC.
            // No factor models, private fields, files, keys or write methods are accessed.
            var type = plugin.Instance.GetType().Assembly.GetType("Jellyfin.Plugin.TwoFactorAuth.Services.ChallengeStore");
            var native = type is null ? null : services.GetService(type);
            return native is null ? null : ReadVerdict(native, caller.AccessToken);
        }
        catch { return null; }
    }

    internal static bool? ReadVerdict(object native, string token)
    {
        try
        {
            var type = native.GetType();
            MethodInfo? Find(string name) => type.GetMethod(name, BindingFlags.Public | BindingFlags.Instance, null, [typeof(string)], null);
            var verified = Find("IsTokenVerified");
            var blocked = Find("IsTokenBlocked");
            if (verified?.ReturnType != typeof(bool) || blocked?.ReturnType != typeof(bool)) return null;
            if (blocked.Invoke(native, [token]) is not false) return false;
            return verified.Invoke(native, [token]) is true && blocked.Invoke(native, [token]) is false;
        }
        catch { return null; }
    }
}
