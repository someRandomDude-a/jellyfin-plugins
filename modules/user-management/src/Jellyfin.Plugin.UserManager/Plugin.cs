using System.Globalization;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Jellyfin.Plugin.UserManager.Configuration;

namespace Jellyfin.Plugin.UserManager;

/// <summary>
/// Jellyfin User Manager: identity, permissions, invites and Discord self-service for Jellyfin + Seerr.
/// </summary>
public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Stable plugin identifier. Must match manifest.json and meta.json.
    /// </summary>
    public static readonly Guid PluginId = Guid.Parse("ba6561b1-5df1-445a-9f8d-e6127f143c97");

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>
    /// Gets the active plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "User Manager";

    /// <inheritdoc />
    public override string Description =>
        "Identity and permission manager for Jellyfin and Seerr with invite links, Discord password resets, audit logs and admin notifications.";

    /// <inheritdoc />
    public override Guid Id => PluginId;

    /// <summary>
    /// Gets the stable plugin data directory, creating it when required.
    /// </summary>
    /// <remarks>
    /// Jellyfin's default plugin data folder lives inside the installed plugin directory, which
    /// can be replaced during an upgrade. Use a dedicated folder under the server data directory
    /// so the database and encryption key survive plugin updates.
    /// </remarks>
    public string EnsureDataFolder() => EnsureDataFolder(ApplicationPaths);

    /// <summary>
    /// Gets the stable plugin data directory for the given Jellyfin application paths, creating it when required.
    /// </summary>
    public static string EnsureDataFolder(IApplicationPaths applicationPaths)
    {
        var folder = GetDataFolder(applicationPaths);
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>
    /// Gets the stable plugin data directory for the given Jellyfin application paths.
    /// </summary>
    public static string GetDataFolder(IApplicationPaths applicationPaths)
    {
        ArgumentNullException.ThrowIfNull(applicationPaths);
        return Path.Combine(applicationPaths.DataPath, "Jellyfin.Plugin.UserManager");
    }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = "usermanager",
                DisplayName = "User Manager",
                EmbeddedResourcePath = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}.Web.admin.html",
                    GetType().Namespace),
                EnableInMainMenu = true,
                MenuSection = "Administration",
                MenuIcon = "manage_accounts"
            }
        };
    }
}
