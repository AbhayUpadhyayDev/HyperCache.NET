using System;
using System.Threading.Tasks;

namespace HyperCache.NET.Cache
{
    public interface ICache<TKey, TValue>
    {
        ValueTask<TValue?> GetAsync(TKey key);
        ValueTask SetAsync(TKey key, TValue value, TimeSpan? ttl = null);
        ValueTask<bool> RemoveAsync(TKey key);
        ValueTask<bool> ContainsAsync(TKey key);
        ValueTask<long> CountAsync();
    }
}
