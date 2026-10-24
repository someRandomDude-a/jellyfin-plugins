using System.Collections.Concurrent;

namespace Jellyfin.Plugin.DiscordBridge;

/// <summary>Bounded, process-local operational counters. No actor, channel, message, token or payload retention.</summary>
public sealed class InteractionHealth
{
    private static readonly HashSet<string> Commands = ["help", "status", "link.prepare", "unlink", "reset", "watchparty.devices", "watchparty.invite"];
    private static readonly HashSet<string> Outcomes = ["completed", "unavailable", "delivered_dm", "delivered_channel", "created_private"];
    private readonly ConcurrentDictionary<string, long> counts = new(StringComparer.Ordinal);
    public void Record(string command, string outcome)
    {
        if (!Commands.Contains(command) || !Outcomes.Contains(outcome)) throw new ArgumentException("Unknown interaction metric");
        counts.AddOrUpdate(command + ":" + outcome, 1, (_, count) => count == long.MaxValue ? count : count + 1);
    }
    public IReadOnlyDictionary<string, long> Snapshot() => new SortedDictionary<string, long>(counts, StringComparer.Ordinal);
}
