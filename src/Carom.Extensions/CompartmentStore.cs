// Copyright (c) BaryoDev. All rights reserved.
// Licensed under the MPL-2.0 license. See LICENSE file in the project root for full license information.

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
                ThrowIfConflicting(resourceKey, existingEntry, config);
                existingEntry.Touch();
                return existingEntry.State;
            }

            // Create new entry
            var newState = new CompartmentState(config.MaxConcurrency, config.QueueDepth);
            var newEntry = new CompartmentStateEntry(newState, config.MaxConcurrency, config.QueueDepth);

            // Try to add, handling race condition
            var entry = _states.GetOrAdd(resourceKey, newEntry);

            // If we lost the race, dispose the state we created
            if (entry != newEntry)
            {
                newState.Dispose();

                // A lost GetOrAdd race hands back someone else's entry, so the loser must be
                // validated too or a conflicting registration passes silently under load.
                ThrowIfConflicting(resourceKey, entry, config);
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
        /// Called from both the sequential and the lost-race path so the rule cannot drift.
        /// </summary>
        private static void ThrowIfConflicting(string resourceKey, CompartmentStateEntry entry, Compartment config)
        {
            StoreConflictHelper.ThrowIfConflicting("Resource", resourceKey,
                new ConfigField("MaxConcurrency", entry.MaxConcurrency, config.MaxConcurrency),
                new ConfigField("QueueDepth", entry.QueueDepth, config.QueueDepth));
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

                var scanStartTicks = DateTime.UtcNow.Ticks;

                // Get the oldest entries using allocation-free helper
                var actualCount = LruEvictionHelper.FindLeastRecentlyUsed(
                    _states,
                    entry => entry.LastAccessTicks,
                    toRemove,
                    out var keysToEvict);

                for (int i = 0; i < actualCount; i++)
                {
                    // Skip entries touched after the scan snapshot (evicting a hot
                    // entry would reset its state) or still holding bulkhead slots
                    // (disposing a SemaphoreSlim with pending waiters/holders is
                    // undefined behavior and would let the limit be exceeded).
                    if (!_states.TryGetValue(keysToEvict[i], out var candidate) ||
                        Volatile.Read(ref candidate.LastAccessTicks) >= scanStartTicks ||
                        candidate.State.ActiveCount > 0)
                    {
                        continue;
                    }

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
            public int MaxConcurrency { get; }
            public int QueueDepth { get; }
            public long LastAccessTicks;

            public CompartmentStateEntry(CompartmentState state, int maxConcurrency, int queueDepth)
            {
                State = state;
                MaxConcurrency = maxConcurrency;
                QueueDepth = queueDepth;
                LastAccessTicks = DateTime.UtcNow.Ticks;
            }

            public void Touch()
            {
                Interlocked.Exchange(ref LastAccessTicks, DateTime.UtcNow.Ticks);
            }
        }
    }
}
