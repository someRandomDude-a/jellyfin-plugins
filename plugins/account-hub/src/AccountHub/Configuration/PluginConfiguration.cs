using MediaBrowser.Model.Plugins;

namespace AccountHub.Configuration;

// Sensitive settings live in the revisioned database, not Jellyfin's generic XML config API.
public sealed class PluginConfiguration : BasePluginConfiguration;
