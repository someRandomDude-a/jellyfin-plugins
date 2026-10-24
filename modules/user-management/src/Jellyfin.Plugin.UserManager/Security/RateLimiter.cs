using System.Collections.Concurrent;

namespace Jellyfin.Plugin.UserManager.Security;

/// <summary>
/// Small in-process fixed-window limiter for anonymous endpoints (invite signup, reset requests).
/// </summary>
public sealed class RateLimiter
{
    private readonly ConcurrentDictionary<string, Window> _windows = new(StringComparer.Ordinal);
    private DateTimeOffset _lastSweep = DateTimeOffset.UtcNow;

    public bool IsAllowed(string key, int limit, TimeSpan window)
    {
        if (limit <= 0)
        {
            return false;
        }

        SweepIfNeeded();

        var now = DateTimeOffset.UtcNow;
        var bucket = _windows.GetOrAdd(key, _ => new Window(now + window));
        lock (bucket)
        {
            if (bucket.ResetAt <= now)
            {
                bucket.Count = 0;
                bucket.ResetAt = now + window;
            }

            bucket.Count++;
            return bucket.Count <= limit;
        }
    }

    private void SweepIfNeeded()
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastSweep < TimeSpan.FromMinutes(5))
        {
            return;
        }

        _lastSweep = now;
        foreach (var pair in _windows)
        {
            if (pair.Value.ResetAt <= now)
            {
                _windows.TryRemove(pair.Key, out _);
            }
        }
    }

    private sealed class Window
    {
        public Window(DateTimeOffset resetAt)
        {
            ResetAt = resetAt;
        }

        public int Count { get; set; }

        public DateTimeOffset ResetAt { get; set; }
    }
}
