using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;

namespace Fantahelp.API.Services;

public interface IOptimalScenarioCache
{
    SuggestionResult? Get(string key);
    void Set(string key, SuggestionResult result);
    Task<SuggestionResult> GetOrCreateAsync(string key, Func<Task<SuggestionResult>> factory);
}

public sealed class OptimalScenarioCache : IOptimalScenarioCache, IDisposable
{
    private const int CacheCapacity = 100;
    private static readonly TimeSpan ResultTtl = TimeSpan.FromMinutes(10);

    private readonly MemoryCache _cache = new(new MemoryCacheOptions
    {
        SizeLimit = CacheCapacity
    });
    private readonly ConcurrentDictionary<string, Lazy<Task<SuggestionResult>>> _inflight = new();

    public SuggestionResult? Get(string key)
        => _cache.TryGetValue<SuggestionResult>(key, out var result) ? result : null;

    public void Set(string key, SuggestionResult result)
    {
        _cache.Set(key, result, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = ResultTtl,
            Size = 1
        });
    }

    public async Task<SuggestionResult> GetOrCreateAsync(string key, Func<Task<SuggestionResult>> factory)
    {
        var cached = Get(key);
        if (cached != null)
            return cached;

        var candidate = new Lazy<Task<SuggestionResult>>(
            factory,
            LazyThreadSafetyMode.ExecutionAndPublication);
        var entry = _inflight.GetOrAdd(key, candidate);

        try
        {
            var result = await entry.Value.ConfigureAwait(false);
            Set(key, result);
            return result;
        }
        finally
        {
            if (_inflight.TryGetValue(key, out var current) && ReferenceEquals(current, entry))
                _inflight.TryRemove(key, out _);
        }
    }

    public void Dispose()
    {
        _cache.Dispose();
    }
}
