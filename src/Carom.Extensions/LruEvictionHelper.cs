// Copyright (c) BaryoDev. All rights reserved.
// Licensed under the MPL-2.0 license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;

namespace Carom.Extensions
{
    /// <summary>
    /// Allocation-free helper for LRU eviction.
    /// Uses manual min-tracking instead of LINQ to avoid allocations.
    /// </summary>
    internal static class LruEvictionHelper
    {
        /// <summary>
        /// Thread-local buffer for collecting keys to evict.
        /// Avoids allocation on each eviction call.
        /// </summary>
        [ThreadStatic]
        private static string[]? _evictionBuffer;

        /// <summary>
        /// Thread-local buffer for tracking access times.
        /// </summary>
        [ThreadStatic]
        private static long[]? _ticksBuffer;

        /// <summary>
        /// Finds the keys with the smallest LastAccessTicks values.
        /// Returns the keys to evict without any LINQ allocations.
        /// </summary>
        /// <typeparam name="TEntry">The entry type containing LastAccessTicks.</typeparam>
        /// <param name="entries">The dictionary entries to scan.</param>
        /// <param name="getLastAccessTicks">Function to get LastAccessTicks from an entry.</param>
        /// <param name="toRemove">Number of entries to evict.</param>
        /// <param name="keysToEvict">Output span for keys to evict. Must be at least toRemove in length.</param>
        /// <returns>The actual number of keys to evict (may be less than toRemove if dictionary is smaller).</returns>
        public static int FindLeastRecentlyUsed<TEntry>(
            IEnumerable<KeyValuePair<string, TEntry>> entries,
            Func<TEntry, long> getLastAccessTicks,
            int toRemove,
            out string[] keysToEvict)
        {
            // Ensure buffers are large enough
            EnsureBufferCapacity(toRemove);

            var buffer = _evictionBuffer!;
            var ticksBuffer = _ticksBuffer!;

            // Initialize with max values
            var foundCount = 0;

            foreach (var kvp in entries)
            {
                var ticks = getLastAccessTicks(kvp.Value);

                if (foundCount < toRemove)
                {
                    // Still filling the buffer
                    InsertSorted(buffer, ticksBuffer, foundCount, kvp.Key, ticks);
                    foundCount++;
                }
                else if (ticks < ticksBuffer[toRemove - 1])
                {
                    // This entry is older than the newest in our eviction list
                    // Find the right position and insert
                    InsertSorted(buffer, ticksBuffer, toRemove, kvp.Key, ticks);
                }
            }

            keysToEvict = buffer;
            return Math.Min(foundCount, toRemove);
        }

        /// <summary>
        /// Inserts a key into the sorted buffer, maintaining ascending order by ticks.
        /// </summary>
        private static void InsertSorted(string[] keys, long[] ticks, int currentCount, string newKey, long newTicks)
        {
            // Find insertion point (ascending order - smallest ticks first)
            var insertPos = currentCount;
            for (int i = 0; i < currentCount && i < keys.Length; i++)
            {
                if (newTicks < ticks[i])
                {
                    insertPos = i;
                    break;
                }
            }

            // Shift elements to make room (up to buffer capacity - 1)
            var shiftEnd = Math.Min(currentCount, keys.Length - 1);
            for (int i = shiftEnd; i > insertPos; i--)
            {
                keys[i] = keys[i - 1];
                ticks[i] = ticks[i - 1];
            }

            // Insert the new element
            if (insertPos < keys.Length)
            {
                keys[insertPos] = newKey;
                ticks[insertPos] = newTicks;
            }
        }

        /// <summary>
        /// Ensures thread-local buffers are large enough.
        /// </summary>
        private static void EnsureBufferCapacity(int required)
        {
            if (_evictionBuffer == null || _evictionBuffer.Length < required)
            {
                // Allocate with some extra room to avoid frequent reallocations
                var size = Math.Max(required, 32);
                _evictionBuffer = new string[size];
                _ticksBuffer = new long[size];
            }
        }
    }
}
