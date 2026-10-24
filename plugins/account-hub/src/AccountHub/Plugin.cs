using AccountHub.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace AccountHub;

public sealed class Plugin(IApplicationPaths paths, IXmlSerializer serializer)
    : BasePlugin<PluginConfiguration>(paths, serializer), IHasWebPages
{
    public const string PluginId = "bc9c3d3e-6da3-4ee5-91cd-c429d58ad901";
    public override string Name => "Account Hub";
    public override Guid Id => Guid.Parse(PluginId);
    public override string Description => "Unified account workspace with embedded Security and Notifications (integration preview).";

    public IEnumerable<PluginPageInfo> GetPages() =>
    [
        new PluginPageInfo
        {
            Name = "AccountHub",
            EmbeddedResourcePath = "AccountHub.Configuration.configPage.html",
            EnableInMainMenu = true
        }
    ];
}
