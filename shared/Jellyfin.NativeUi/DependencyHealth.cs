using MediaBrowser.Common.Plugins;

namespace Jellyfin.PluginSupport;

public sealed record DependencyHealth(string Id, string Name, bool Available, string State, string Reason, string Fix);
public static class PluginDependency
{
    public static DependencyHealth Check(IPluginManager manager, string id, string name, Guid pluginId)
    {
        try
        {
            var installed = manager.Plugins.Where(plugin => plugin.Id == pluginId).ToArray();
            var available = installed.Any(plugin => plugin.IsEnabledAndSupported && plugin.Instance is not null);
            var state = available ? "available" : installed.Length == 0 ? "missing" : "disabled_or_incompatible";
            return new(id, name, available, state, available ? name + " is loaded." : name + " is " + state.Replace('_', ' ') + ". Only dependent features are disabled.",
                "Install a compatible " + name + " package, enable it, and restart Jellyfin. This plugin's own features do not require it.");
        }
        catch { return new(id, name, false, "unavailable", name + " health could not be verified.", "Check native plugin status and server logs, repair or enable the plugin, then restart Jellyfin."); }
    }
}
