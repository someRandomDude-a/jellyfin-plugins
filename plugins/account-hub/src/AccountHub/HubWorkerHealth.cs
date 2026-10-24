using System.Collections.Concurrent;

namespace AccountHub;

/// <summary>Worker availability only; running does not certify external delivery or upstream health.</summary>
public sealed class HubWorkerHealth
{
    private readonly ConcurrentDictionary<string, string> states = new(StringComparer.Ordinal);
    public void Set(string worker, string state) => states[worker] = state;
    public bool IsRunning(string worker) => states.TryGetValue(worker, out var state) && state == "running";
    public bool Healthy => !states.IsEmpty && states.Values.All(state => state is "running" or "waiting");
    public IReadOnlyDictionary<string, string> Snapshot() => new SortedDictionary<string, string>(states, StringComparer.Ordinal);
}
