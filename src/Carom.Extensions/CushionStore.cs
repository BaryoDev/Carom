using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Carom.Extensions
{
    /// <summary>
    /// Static store for circuit breaker states, keyed by service name.
    /// Implements LRU eviction to prevent unbounded memory growth.
    /// </summary>
    internal static class CushionStore
    {
        private static readonly ConcurrentDictionary<string, CushionStateEntry> States = new ConcurrentDictionary<string, CushionStateEntry>();
        private static readonly object EvictionLock = new object();

        /// <summary>
        /// Maximum number of circuit breaker states to keep in memory.
        /// Can be configured at startup.
        /// </summary>
        public static int MaxSize { get; set; } = 1000;

        /// <summary>
        /// Gets or creates a circuit breaker state for the given service key.
        /// Updates last access time for LRU tracking.
        /// </summary>
        public static CushionState GetOrCreate(string serviceKey, Cushion config)
        {
            // Try to get existing entry first
            if (States.TryGetValue(serviceKey, out var existingEntry))
            {
                if (existingEntry.SamplingWindow != config.SamplingWindow)
                    throw new InvalidOperationException(
                        $"Service '{serviceKey}' already registered with SamplingWindow={existingEntry.SamplingWindow}, " +
                        $"but requested SamplingWindow={config.SamplingWindow}. Configuration changes for existing keys are not supported.");
                existingEntry.Touch();
                return existingEntry.State;
            }

            // Create new entry
            var newState = new CushionState(config.SamplingWindow);
            var newEntry = new CushionStateEntry(newState, config.SamplingWindow);

            // Try to add, handling race condition
            var entry = States.GetOrAdd(serviceKey, newEntry);

            // Check if we need to evict
            if (States.Count > MaxSize)
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
                if (States.Count <= MaxSize)
                {
                    return;
                }

                // Calculate how many to remove (remove 10% to avoid frequent eviction)
                var toRemove = Math.Max(1, States.Count - MaxSize + MaxSize / 10);

                var scanStartTicks = DateTime.UtcNow.Ticks;

                // Get the oldest entries using allocation-free helper
                var actualCount = LruEvictionHelper.FindLeastRecentlyUsed(
                    States,
                    entry => entry.LastAccessTicks,
                    toRemove,
                    out var keysToEvict);

                for (int i = 0; i < actualCount; i++)
                {
                    // Skip entries touched after the scan snapshot: evicting a hot
                    // entry would recreate its circuit breaker in the Closed state,
                    // discarding failure history for a service under active load.
                    if (States.TryGetValue(keysToEvict[i], out var candidate) &&
                        Volatile.Read(ref candidate.LastAccessTicks) >= scanStartTicks)
                    {
                        continue;
                    }

                    States.TryRemove(keysToEvict[i], out _);
                }
            }
            finally
            {
                Monitor.Exit(EvictionLock);
            }
        }

        /// <summary>
        /// Gets the current circuit state for a service without creating state
        /// or updating LRU access time (read-only, for monitoring).
        /// </summary>
        public static bool TryGetState(string serviceKey, out CircuitState state)
        {
            if (States.TryGetValue(serviceKey, out var entry))
            {
                state = entry.State.State;
                return true;
            }

            state = CircuitState.Closed;
            return false;
        }

        /// <summary>
        /// Removes a specific circuit breaker state.
        /// </summary>
        public static bool Remove(string serviceKey)
        {
            return States.TryRemove(serviceKey, out _);
        }

        /// <summary>
        /// Clears all circuit breaker states (useful for testing).
        /// </summary>
        internal static void Clear()
        {
            States.Clear();
        }

        /// <summary>
        /// Gets the current number of states stored.
        /// </summary>
        public static int Count => States.Count;

        /// <summary>
        /// Wrapper to track last access time for LRU eviction.
        /// </summary>
        private class CushionStateEntry
        {
            public CushionState State { get; }
            public int SamplingWindow { get; }
            public long LastAccessTicks;

            public CushionStateEntry(CushionState state, int samplingWindow)
            {
                State = state;
                SamplingWindow = samplingWindow;
                LastAccessTicks = DateTime.UtcNow.Ticks;
            }

            public void Touch()
            {
                Interlocked.Exchange(ref LastAccessTicks, DateTime.UtcNow.Ticks);
            }
        }
    }
}
