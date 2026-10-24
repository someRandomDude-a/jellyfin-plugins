using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Configuration;

namespace Jellyfin.Plugin.DiscordBridge;

public sealed class Configuration : BasePluginConfiguration
{
    public bool Enabled { get; set; }
    public string PublicServerUrl { get; set; } = "";
    public string GuildId { get; set; } = "";
    public string[] AllowedChannelIds { get; set; } = [];
    public bool EnableSelfServiceCommands { get; set; } = true;
    public bool EnableWatchPartyCommands { get; set; } = true;
    public bool LibraryAnnouncementsEnabled { get; set; }
    public bool LibraryEpisodes { get; set; } = true;
    public bool LibraryMovies { get; set; } = true;
    public string[] LibraryChannelIds { get; set; } = [];
    public Guid[] LibraryIds { get; set; } = [];
    public bool LibraryDisclosureAcknowledged { get; set; }
    public DateTimeOffset? LibraryEnabledSince { get; set; }
}

public sealed class Plugin(IApplicationPaths paths, IXmlSerializer serializer) : BasePlugin<Configuration>(paths, serializer), IHasWebPages
{
    public const string PluginId = "4b047046-a550-4415-b204-4206f1f18201";
    public override Guid Id => Guid.Parse(PluginId);
    public override string Name => "Discord Bridge";
    public override string Description => "One Discord bot and transport for Account Hub and domain self-service commands.";
    public IEnumerable<PluginPageInfo> GetPages() => [new() { Name = "DiscordBridge", EmbeddedResourcePath = "Jellyfin.Plugin.DiscordBridge.Web.settings.html", EnableInMainMenu = true, MenuSection = "Plugins", MenuIcon = "chat" }, new() { Name = "DiscordBridgeAdminUI.js", EmbeddedResourcePath = "Suite.Web.admin-ui.js" }];
}
