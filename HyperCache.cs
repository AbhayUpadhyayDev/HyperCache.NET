using System;
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Metrics;

namespace HyperCache.NET.Cache
{
    /// <summary>
    /// High-performance in-memory cache with TTL, LRU eviction, and OpenTelemetry metrics.
    /// </summary>
    public class HyperCache<TKey, TValue> : ICache<TKey, TValue>, IAsyncDisposable
        where TKey : notnull
    {
        private readonly ConcurrentDictionary<TKey, CacheEntry<TValue>> _cache = new();
        private readonly ConcurrentQueue<TKey> _recencyQueue = new();
        private readonly TimeSpan _cleanupInterval;
        private readonly bool _enableCleanup;
        private readonly int _maxItems;
        private readonly ILogger? _logger;
        private readonly CancellationTokenSource _cts = new();

        private Task? _cleanupTask;
        private bool _disposed;

        // Metrics
        private long _hitCount;
        private long _missCount;
        private long _evictionCount;
        private long _cleanupRuns;

        public HyperCache(
            TimeSpan? cleanupInterval = null,
            bool enableCleanup = true,
            int maxItems = 100_000,
            ILogger? logger = null)
        {
            _cleanupInterval = cleanupInterval ?? TimeSpan.FromMinutes(1);
            _enableCleanup = enableCleanup;
            _maxItems = maxItems > 0 ? maxItems : 100_000;
            _logger = logger;

            if (_enableCleanup)
            {
                _cleanupTask = Task.Run(() => CleanupExpiredEntriesAsync(_cts.Token));
                _logger?.LogInformation("HyperCache cleanup started (interval: {interval})", _cleanupInterval);
            }
        }

        public ValueTask<TValue?> GetAsync(TKey key)
        {
            EnsureNotDisposed();

            if (_cache.TryGetValue(key, out var entry))
            {
                if (!entry.IsExpired)
                {
                    Interlocked.Increment(ref _hitCount);
                    RecordUsage(key);
                    return new(entry.Value);
                }

                // Remove expired entry
                _cache.TryRemove(key, out _);
            }

            Interlocked.Increment(ref _missCount);
            return new((TValue?)default);
        }

        public ValueTask SetAsync(TKey key, TValue value, TimeSpan? ttl = null)
        {
            EnsureNotDisposed();

            var entry = new CacheEntry<TValue>(value, ttl);
            _cache[key] = entry;
            RecordUsage(key);

            // Evict if over limit
            if (_cache.Count > _maxItems)
                _ = Task.Run(EvictLeastRecentlyUsed);

            return ValueTask.CompletedTask;
        }

        public ValueTask<bool> RemoveAsync(TKey key)
        {
            EnsureNotDisposed();
            var removed = _cache.TryRemove(key, out _);
            return new(removed);
        }

        public ValueTask<bool> ContainsAsync(TKey key)
        {
            EnsureNotDisposed();
            var exists = _cache.TryGetValue(key, out var entry) && !entry.IsExpired;
            return new(exists);
        }

        public ValueTask<long> CountAsync()
        {
            EnsureNotDisposed();
            return new(_cache.Count);
        }

        private void RecordUsage(TKey key)
        {
            _recencyQueue.Enqueue(key);

            // keep queue bounded to prevent unbounded memory growth
            while (_recencyQueue.Count > _maxItems * 2)
                _recencyQueue.TryDequeue(out _);
        }

        private async Task CleanupExpiredEntriesAsync(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    long expiredCount = 0;
                    foreach (var (key, entry) in _cache)
                    {
                        if (entry.IsExpired)
                        {
                            _cache.TryRemove(key, out _);
                            expiredCount++;
                        }
                    }

                    Interlocked.Increment(ref _cleanupRuns);

                    if (expiredCount > 0)
                        _logger?.LogDebug("Removed {count} expired entries.", expiredCount);

                    await Task.Delay(_cleanupInterval, token);
                }
            }
            catch (TaskCanceledException)
            {
                // graceful shutdown
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Cleanup loop error in HyperCache.");
            }
            finally
            {
                _logger?.LogInformation("Cache cleanup stopped.");
            }
        }

        private void EvictLeastRecentlyUsed()
        {
            var evicted = 0;

            while (_cache.Count > _maxItems && _recencyQueue.TryDequeue(out var oldestKey))
            {
                if (_cache.TryRemove(oldestKey, out _))
                    evicted++;
            }

            if (evicted > 0)
            {
                Interlocked.Add(ref _evictionCount, evicted);
                _logger?.LogWarning("Evicted {count} LRU entries (cache size limited to {limit})", evicted, _maxItems);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            _cts.Cancel();

            try
            {
                if (_cleanupTask != null)
                    await _cleanupTask.WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch { /* ignore */ }

            _cts.Dispose();
            _logger?.LogInformation("HyperCache disposed.");
        }

        private void EnsureNotDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(HyperCache<TKey, TValue>));
        }

        // Expose metrics (to OpenTelemetry)
        public void RegisterMetrics(Meter meter)
        {
            meter.CreateObservableGauge("hypercache_items_total", () => _cache.Count);
            meter.CreateObservableCounter("hypercache_hits_total", () => _hitCount);
            meter.CreateObservableCounter("hypercache_misses_total", () => _missCount);
            meter.CreateObservableCounter("hypercache_evictions_total", () => _evictionCount);
            meter.CreateObservableCounter("hypercache_cleanup_runs_total", () => _cleanupRuns);
        }
    }
}
