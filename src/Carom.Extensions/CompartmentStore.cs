using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Carom.Extensions
{
    /// <summary>
    /// Static store for compartment states, ensuring one state per resource.
    /// Implements LRU eviction to prevent unbounded memory growth.
    /// Properly disposes evicted CompartmentState instances.
    /// </summary>
    internal static class CompartmentStore
    {
        private static readonly ConcurrentDictionary<string, CompartmentStateEntry> _states = new();
        private static readonly object EvictionLock = new object();

        /// <summary>
        /// Maximum number of compartment states to keep in memory.
        /// Can be configured at startup.
        /// </summary>
        public static int MaxSize { get; set; } = 1000;

        /// <summary>
        /// Gets or creates a compartment state for the specified resource.
        /// Updates last access time for LRU tracking.
        /// </summary>
        public static CompartmentState GetOrCreate(string resourceKey, Compartment config)
        {
            // Try to get existing entry first
            if (_states.TryGetValue(resourceKey, out var existingEntry))
            {
                existingEntry.Touch();
                return existingEntry.State;
            }

            // Create new entry
            var newState = new CompartmentState(config.MaxConcurrency, config.QueueDepth);
            var newEntry = new CompartmentStateEntry(newState);

            // Try to add, handling race condition
            var entry = _states.GetOrAdd(resourceKey, newEntry);

            // If we lost the race, dispose the state we created
            if (entry != newEntry)
            {
                newState.Dispose();
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
        /// Removes the least recently used entries when over capacity.
        /// Disposes evicted CompartmentState instances.
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
                    if (_states.TryRemove(keysToEvict[i], out var entry))
                    {
                        // Dispose the CompartmentState to release the semaphore
                        entry.State.Dispose();
                    }
                }
            }
            finally
            {
                Monitor.Exit(EvictionLock);
            }
        }

        /// <summary>
        /// Removes a specific compartment state.
        /// </summary>
        public static bool Remove(string resourceKey)
        {
            if (_states.TryRemove(resourceKey, out var entry))
            {
                entry.State.Dispose();
                return true;
            }
            return false;
        }

        /// <summary>
        /// Clears all compartment states (for testing).
        /// Disposes all CompartmentState instances.
        /// </summary>
        public static void Clear()
        {
            foreach (var entry in _states.Values)
            {
                entry.State.Dispose();
            }
            _states.Clear();
        }

        /// <summary>
        /// Gets the current number of states stored.
        /// </summary>
        public static int Count => _states.Count;

        /// <summary>
        /// Wrapper to track last access time for LRU eviction.
        /// </summary>
        private class CompartmentStateEntry
        {
            public CompartmentState State { get; }
            public long LastAccessTicks;

            public CompartmentStateEntry(CompartmentState state)
            {
                State = state;
                LastAccessTicks = DateTime.UtcNow.Ticks;
            }

            public void Touch()
            {
                Interlocked.Exchange(ref LastAccessTicks, DateTime.UtcNow.Ticks);
            }
        }
    }
}
