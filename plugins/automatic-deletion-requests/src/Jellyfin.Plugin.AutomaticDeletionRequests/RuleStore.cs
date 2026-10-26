using System.Text.Json;
using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.AutomaticDeletionRequests;

public sealed class RuleState
{
    public Configuration Configuration { get; set; } = new(0, false, Guid.Empty, Guid.Empty, 24, []);
    public Dictionary<string, HashSet<Guid>> Submitted { get; set; } = [];
    public DateTimeOffset? LastRun { get; set; }
}
public sealed class RuleStore(IApplicationPaths paths)
{
    private const int MaximumBytes = 2_000_000;
    private readonly object gate = new();
    private readonly string file = Path.Combine(paths.DataPath, "automatic-deletion-requests", "rules.json");
    public T Read<T>(Func<RuleState, T> read) { lock (gate) return read(Load()); }
    public T Write<T>(Func<RuleState, T> change)
    {
        lock (gate) {
            var state = Load(); var result = change(state); Validate(state);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(state);
            if (bytes.Length > MaximumBytes) throw new InvalidOperationException("Rule storage capacity reached");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            MarkInstalled(); // A crash or missing state must not silently reset submission receipts.
            var pending = file + ".pending";
            using (var output = new FileStream(pending, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) {
                output.Write(bytes); output.Flush(true);
            }
            File.Move(pending, file, true); return result;
        }
    }
    private RuleState Load()
    {
        if (!File.Exists(file)) {
            if (File.Exists(file + ".installed") || File.Exists(file + ".pending")) throw new InvalidOperationException("Rule storage unavailable");
            return new();
        }
        using var stream = File.OpenRead(file);
        if (stream.Length > MaximumBytes) throw new InvalidOperationException("Rule storage unavailable");
        var state = JsonSerializer.Deserialize<RuleState>(stream) ?? throw new InvalidOperationException("Rule storage unavailable");
        Validate(state); MarkInstalled(); return state; // Adopt legacy state without losing receipts on later file loss.
    }
    private void MarkInstalled()
    {
        if (File.Exists(file + ".installed")) return;
        using var marker = new FileStream(file + ".installed", FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        marker.WriteByte(1); marker.Flush(true);
    }
    private static void Validate(RuleState state)
    {
        RuleEngine.Validate(state.Configuration);
        if (state.Submitted is null || state.Submitted.Count > 1000 || state.Submitted.Any(pair =>
                pair.Key is not { Length: > 0 and <= 100 } || !pair.Key.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') ||
                pair.Value is null || pair.Value.Count > 10000 || pair.Value.Contains(Guid.Empty)) ||
            state.Submitted.Sum(pair => (long)pair.Value.Count) > 40000)
            throw new InvalidOperationException("Invalid rule submission history");
    }
}
