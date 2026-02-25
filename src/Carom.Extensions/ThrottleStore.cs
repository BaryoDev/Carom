using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Carom.Extensions
{
    /// <summary>
    /// Static store for throttle states, ensuring one state per service.
    /// Implements LRU eviction to prevent unbounded memory growth.
    /// </summary>
    internal static class ThrottleStore
    {
        private static readonly ConcurrentDictionary<string, ThrottleStateEntry> _states = new();
        private static readonly object EvictionLock = new object();

        /// <summary>
        /// Maximum number of throttle states to keep in memory.
        /// Can be configured at startup.
        /// </summary>
        public static int MaxSize { get; set; } = 1000;

        /// <summary>
        /// Gets or creates a throttle state for the specified service.
        /// Updates last access time for LRU tracking.
        /// </summary>
        public static ThrottleState GetOrCreate(string serviceKey, Throttle config)
        {
            // Try to get existing entry first
            if (_states.TryGetValue(serviceKey, out var existingEntry))
            {
                if (existingEntry.MaxRequests != config.MaxRequests || existingEntry.BurstSize != config.BurstSize)
                    throw new InvalidOperationException(
                        $"Service '{serviceKey}' already registered with MaxRequests={existingEntry.MaxRequests}, BurstSize={existingEntry.BurstSize}, " +
                        $"but requested MaxRequests={config.MaxRequests}, BurstSize={config.BurstSize}. Configuration changes for existing keys are not supported.");
                existingEntry.Touch();
                return existingEntry.State;
            }

            // Create new entry
            var newState = new ThrottleState(config.MaxRequests, config.TimeWindow, config.BurstSize);
            var newEntry = new ThrottleStateEntry(newState, config.MaxRequests, config.BurstSize);

            // Try to add, handling race condition
            var entry = _states.GetOrAdd(serviceKey, newEntry);

            // Check if we need to evict
            if (_states.Count > MaxSize)
            {
                EvictLeastRecentlyUsed();
            }

            entry.Touch();
            return entry.State;
        }

        /// <summary>
        /// Removes the least recently used entries when over capacity.
        /// Uses allocation-free LruEvictionHelper instead of LINQ.
        /// </summary>
        private static void EvictLeastRecentlyUsed()
        {
            // Only one thread should perform eviction at a time
            if (!Monitor.TryEnter(EvictionLock))
            {
                return;
            }

            try
            {
                // Check again under lock
                if (_states.Count <= MaxSize)
                {
                    return;
                }

                // Calculate how many to remove (remove 10% to avoid frequent eviction)
                var toRemove = Math.Max(1, _states.Count - MaxSize + MaxSize / 10);

                // Get the oldest entries using allocation-free helper
                var actualCount = LruEvictionHelper.FindLeastRecentlyUsed(
                    _states,
                    entry => entry.LastAccessTicks,
                    toRemove,
                    out var keysToEvict);

                for (int i = 0; i < actualCount; i++)
                {
                    _states.TryRemove(keysToEvict[i], out _);
                }
            }
            finally
            {
                Monitor.Exit(EvictionLock);
            }
        }

        /// <summary>
        /// Removes a specific throttle state.
        /// </summary>
        public static bool Remove(string serviceKey)
        {
            return _states.TryRemove(serviceKey, out _);
        }

        /// <summary>
        /// Clears all throttle states (for testing).
        /// </summary>
        public static void Clear() => _states.Clear();

        /// <summary>
        /// Gets the current number of states stored.
        /// </summary>
        public static int Count => _states.Count;

        /// <summary>
        /// Wrapper to track last access time for LRU eviction.
        /// </summary>
        private class ThrottleStateEntry
        {
            public ThrottleState State { get; }
            public int MaxRequests { get; }
            public int BurstSize { get; }
            public long LastAccessTicks;

            public ThrottleStateEntry(ThrottleState state, int maxRequests, int burstSize)
            {
                State = state;
                MaxRequests = maxRequests;
                BurstSize = burstSize;
                LastAccessTicks = DateTime.UtcNow.Ticks;
            }

            public void Touch()
            {
                Interlocked.Exchange(ref LastAccessTicks, DateTime.UtcNow.Ticks);
            }
        }
    }
}
