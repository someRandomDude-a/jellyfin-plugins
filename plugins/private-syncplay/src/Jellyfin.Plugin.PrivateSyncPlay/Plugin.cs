using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.PrivateSyncPlay;

public sealed class Plugin : BasePlugin<Configuration>, IHasWebPages
{
    internal static readonly object SettingsGate = new();
    internal Action<Configuration, Configuration>? ProtectNativeDefaults;
    internal event Action? NativeSettingsCommitted;
    public static Plugin? Instance { get; private set; }
    public static readonly Guid PluginId = new("09514a9e-f7a0-4c5a-91bc-3a46338298ca");
    public Plugin(IApplicationPaths paths, IXmlSerializer serializer) : base(paths, serializer) => Instance = this;
    public override string Name => "Private SyncPlay";
    public override string Description => "Private parties, limited invites and isolated temporary guest viewing.";
    public override Guid Id => PluginId;
    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        lock (SettingsGate)
        {
            var next = HubWire.Clone((Configuration)configuration);
            if (!string.IsNullOrEmpty(next.PartyPolicyRevision) && next.PartyPolicyRevision != HubWire.Revision(Configuration))
                throw new PartyException(409, "Settings changed. Reload before saving.");
            next.HubCommands = Configuration.HubCommands;
            ProtectNativeDefaults?.Invoke(Configuration, next);
            next.Validate();
            next.PartyPolicyRevision = Guid.NewGuid().ToString("N");
            base.UpdateConfiguration(next);
            try { NativeSettingsCommitted?.Invoke(); } catch { }
        }
    }
    internal void SaveHubConfiguration(Configuration value) { lock (SettingsGate) { value.Validate();base.UpdateConfiguration(value); } }
    public IEnumerable<PluginPageInfo> GetPages() => [new()
    {
        Name = "privateSyncPlay",
        EmbeddedResourcePath = "Jellyfin.Plugin.PrivateSyncPlay.Web.settings.html"
    }, new()
    {
        Name = "privateSyncPlayPlayer",
        EmbeddedResourcePath = "Jellyfin.Plugin.PrivateSyncPlay.Web.player-settings.html"
    }];
}
