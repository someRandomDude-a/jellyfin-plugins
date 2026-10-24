using MediaBrowser.Common.Plugins;

namespace AccountHub.Security;

public static class SupportedSecurity
{
    public static readonly Guid PluginId = new("94879a0c-da24-4eb1-aa06-f28b4b9333b1");
    public const string Version = "2.6.4";
    public const string SourceCommit = "2c02686fcec8b15ea56235de8d196460711befaa";
    public const string Asset = "Jellyfin.Plugin.TwoFactorAuthv2.6.4.0-jf12.zip";
    public const string AssetSha256 = "6c1ae5e41cc0bc7f168025d9b941578f13115cd73cac7a72496888c46f64a998";

    public static bool Matches(string? version) => System.Version.TryParse(version, out var v)
        // The SHA-256-pinned v2.6.4 jf12 archive actually reports assembly 2.6.4.1.
        && v.Major == 2 && v.Minor == 6 && v.Build == 4 && v.Revision is -1 or 0 or 1;
}

public sealed record Compatibility(string State, string? InstalledVersion, DateTimeOffset CheckedAt)
{
    public bool IsOk => State == "ok";
    public string SupportedVersion => SupportedSecurity.Version;
    public bool SecurityTabVisible => IsOk;
    public bool SecurityWritesEnabled => IsOk;
    public bool StepUpAvailable => IsOk;
}

public interface ISecurityCompatibility
{
    Compatibility Check();
}

// Reads manager metadata on each operation: no stale grant, no HTTP version probe,
// and no dependency on another plugin's implementation types or private files.
public sealed class JellyfinSecurityCompatibility(IPluginManager manager, TimeProvider clock) : ISecurityCompatibility
{
    public Compatibility Check()
    {
        try
        {
            var candidates = manager.Plugins.Where(p => p.Id == SupportedSecurity.PluginId).ToArray();
            if (candidates.Length == 0) return Result("missing", null);
            var running = candidates.Where(p => p.Instance is not null).ToArray();
            if (running.Length != 1)
                return Result(candidates.All(p => !p.IsEnabledAndSupported) ? "disabled" : "unavailable", null);
            var plugin = running[0];
            var version = plugin.Instance!.Version.ToString();
            if (!plugin.IsEnabledAndSupported) return Result("disabled", version);
            return Result(SupportedSecurity.Matches(version) ? "ok" : "incompatible", version);
        }
        catch (Exception) { return Result("unavailable", null); }
    }

    private Compatibility Result(string state, string? version) => new(state, version, clock.GetUtcNow());
}
