using Microsoft.Extensions.Caching.Memory;

namespace AccountHub.Security;

// Safe display models only; bounded and actor/origin scoped. Not used by proof/target checks.
public sealed class ReadModelCache : IDisposable
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 8 * 1024 * 1024 });
    public T? Get<T>(string key) where T : class => _cache.Get<T>(key);
    public void Put<T>(string key, T value, long size) where T : class
    {
        if (size <= 4 * 1024 * 1024)
            _cache.Set(key, value, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(10), Size = Math.Max(1, size) });
    }
    public void Invalidate() => _cache.Clear();
    public void Dispose() => _cache.Dispose();
}
