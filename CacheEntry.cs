using System;

namespace HyperCache.NET.Cache
{
    internal class CacheEntry<TValue>
    {
        public TValue Value { get; }
        public DateTimeOffset? Expiration { get; }

        public bool IsExpired => Expiration.HasValue && DateTimeOffset.UtcNow > Expiration.Value;

        public CacheEntry(TValue value, TimeSpan? ttl)
        {
            Value = value;
            Expiration = ttl.HasValue ? DateTimeOffset.UtcNow.Add(ttl.Value) : null;
        }
    }
}
