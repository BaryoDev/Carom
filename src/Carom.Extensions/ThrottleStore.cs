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
                ThrowIfConflicting(serviceKey, existingEntry, config);
                existingEntry.Touch();
                return existingEntry.State;
            }

            // Create new entry
            var newState = new ThrottleState(config.MaxRequests, config.TimeWindow, config.BurstSize);
            var newEntry = new ThrottleStateEntry(newState, config.MaxRequests, config.BurstSize, config.TimeWindow);

            // Try to add, handling race condition
            var entry = _states.GetOrAdd(serviceKey, newEntry);

            // Checked again, because losing this race is indistinguishable from finding an existing
            // entry above. Two callers can both miss the TryGetValue and only one of them adds; the
            // other is handed the winner's entry, and without this it would silently run on a refill
            // interval it never asked for. Validating only the TryGetValue branch would make a
            // conflicting registration throw when it happens sequentially and pass under load, which
            // reads as an intermittent fault rather than a configuration error.
            if (!ReferenceEquals(entry, newEntry))
            {
                ThrowIfConflicting(serviceKey, entry, config);
            }

            // Check if we need to evict
            if (_states.Count > MaxSize)
            {
                EvictLeastRecentlyUsed();
            }

            entry.Touch();
            return entry.State;
        }

        /// <summary>
        /// Rejects a registration that disagrees with the entry already holding the key.
        /// </summary>
        /// <remarks>
        /// One method rather than two copies on purpose: the sequential path and the lost-race path
        /// have to enforce the same rule, and a rule written twice is a rule that drifts.
        /// </remarks>
        private static void ThrowIfConflicting(string serviceKey, ThrottleStateEntry entry, Throttle config)
        {
            if (entry.MaxRequests == config.MaxRequests
                && entry.BurstSize == config.BurstSize
                && entry.TimeWindow == config.TimeWindow)
            {
                return;
            }

            throw new InvalidOperationException(
                $"Service '{serviceKey}' already registered with MaxRequests={entry.MaxRequests}, " +
                $"BurstSize={entry.BurstSize}, TimeWindow={entry.TimeWindow}, " +
                $"but requested MaxRequests={config.MaxRequests}, BurstSize={config.BurstSize}, " +
                $"TimeWindow={config.TimeWindow}. Configuration changes for existing keys are not supported.");
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

                var scanStartTicks = DateTime.UtcNow.Ticks;

                // Get the oldest entries using allocation-free helper
                var actualCount = LruEvictionHelper.FindLeastRecentlyUsed(
                    _states,
                    entry => entry.LastAccessTicks,
                    toRemove,
                    out var keysToEvict);

                for (int i = 0; i < actualCount; i++)
                {
                    // Skip entries touched after the scan snapshot: evicting a hot
                    // entry would recreate it with a full token bucket, silently
                    // bypassing the rate limit.
                    if (_states.TryGetValue(keysToEvict[i], out var candidate) &&
                        Volatile.Read(ref candidate.LastAccessTicks) >= scanStartTicks)
                    {
                        continue;
                    }

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
            public TimeSpan TimeWindow { get; }
            public long LastAccessTicks;

            public ThrottleStateEntry(ThrottleState state, int maxRequests, int burstSize, TimeSpan timeWindow)
            {
                State = state;
                MaxRequests = maxRequests;
                BurstSize = burstSize;
                TimeWindow = timeWindow;
                LastAccessTicks = DateTime.UtcNow.Ticks;
            }

            public void Touch()
            {
                Interlocked.Exchange(ref LastAccessTicks, DateTime.UtcNow.Ticks);
            }
        }
    }
}
