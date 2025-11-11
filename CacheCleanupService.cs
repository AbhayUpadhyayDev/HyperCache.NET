using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace HyperCache.NET.Cache
{
    /// <summary>
    /// Responsible for cleaning expired cache entries in the background.
    /// Designed to be robust, secure, and CPU-efficient.
    /// </summary>
    internal sealed class CacheCleanupService<TKey, TValue> : IAsyncDisposable
        where TKey : notnull
    {
        private readonly ConcurrentDictionary<TKey, CacheEntry<TValue>> _cache;
        private readonly ILogger? _logger;
        private readonly TimeSpan _cleanupInterval;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _cleanupTask;

        private bool _disposed;

        public CacheCleanupService(
            ConcurrentDictionary<TKey, CacheEntry<TValue>> cache,
            TimeSpan cleanupInterval,
            ILogger? logger = null)
        {
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _cleanupInterval = cleanupInterval > TimeSpan.Zero
                ? cleanupInterval
                : TimeSpan.FromMinutes(1);

            _logger = logger;
            _cleanupTask = Task.Run(CleanupLoopAsync);
        }

        /// <summary>
        /// Main cleanup loop — runs periodically, removes expired entries.
        /// </summary>
        private async Task CleanupLoopAsync()
        {
            _logger?.LogInformation("Cache cleanup service started with interval {interval}", _cleanupInterval);

            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var startTime = DateTime.UtcNow;
                    var expiredCount = 0L;

                    foreach (var (key, entry) in _cache)
                    {
                        if (entry.IsExpired)
                        {
                            _cache.TryRemove(key, out _);
                            expiredCount++;
                        }
                    }

                    if (expiredCount > 0)
                    {
                        _logger?.LogDebug("Cache cleanup removed {count} expired entries.", expiredCount);
                    }

                    // Adaptive sleep: sleep longer if few entries expired
                    var elapsed = DateTime.UtcNow - startTime;
                    var delay = expiredCount == 0
                        ? _cleanupInterval + TimeSpan.FromSeconds(10)
                        : _cleanupInterval;

                    await Task.Delay(delay, _cts.Token);
                }
            }
            catch (TaskCanceledException)
            {
                // Expected on shutdown
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error occurred in cache cleanup loop.");
                // Retry after small backoff to avoid crash loops
                await Task.Delay(TimeSpan.FromSeconds(10));
                if (!_cts.IsCancellationRequested)
                    _ = Task.Run(CleanupLoopAsync);
            }
            finally
            {
                _logger?.LogInformation("Cache cleanup service stopped.");
            }
        }

        /// <summary>
        /// Stops the cleanup loop gracefully.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;

            _disposed = true;
            _cts.Cancel();

            try
            {
                await _cleanupTask.WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch (TimeoutException)
            {
                _logger?.LogWarning("Cache cleanup did not complete in time. Forcing shutdown.");
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error disposing cleanup service.");
            }

            _cts.Dispose();
        }
    }
}
