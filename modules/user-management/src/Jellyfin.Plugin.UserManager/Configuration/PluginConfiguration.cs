using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.UserManager.Configuration;

/// <summary>
/// Bootstrap configuration.
/// </summary>
/// <remarks>
/// Mutable plugin settings are stored in the plugin database so secrets can be encrypted at rest.
/// This type is intentionally empty but is kept because Jellyfin requires a configuration type,
/// and it leaves room for future startup options.
/// </remarks>
public sealed class PluginConfiguration : BasePluginConfiguration
{
}
