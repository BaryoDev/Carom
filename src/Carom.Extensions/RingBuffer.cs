using System;
using System.Threading;

namespace Carom.Extensions
{
    /// <summary>
    /// Lock-free ring buffer for tracking recent operation results.
    /// Used by circuit breaker to maintain sliding window of success/failure.
    /// Thread-safe for concurrent Add and CountWhere operations.
    /// </summary>
    internal class RingBuffer<T>
    {
        private readonly T[] _buffer;
        private readonly object _snapshotLock = new object();
        private long _index; // Use long to avoid overflow for much longer

        public RingBuffer(int capacity)
        {
            if (capacity <= 0)
                throw new ArgumentException("Capacity must be positive", nameof(capacity));

            _buffer = new T[capacity];
        }

        /// <summary>
        /// Adds an item to the buffer (thread-safe).
        /// Uses modulo arithmetic to handle wrap-around correctly.
        /// </summary>
        public void Add(T item)
        {
            // Increment and get the previous value
            var idx = Interlocked.Increment(ref _index) - 1;
            // Use positive modulo to get correct index even after overflow
            var bufferIndex = (int)((idx % _buffer.Length + _buffer.Length) % _buffer.Length);
            _buffer[bufferIndex] = item;
        }

        /// <summary>
        /// Gets the current count of items in the buffer (up to capacity).
        /// </summary>
        public int Count
        {
            get
            {
                var idx = Volatile.Read(ref _index);
                // Handle potential negative values after overflow
                if (idx < 0) idx = long.MaxValue + idx + 1;
                return (int)Math.Min(idx, _buffer.Length);
            }
        }

        /// <summary>
        /// Counts items matching the predicate.
        /// Takes a snapshot to ensure consistent reads during enumeration.
        /// </summary>
        public int CountWhere(Func<T, bool> predicate)
        {
            // Take a snapshot under lock to ensure consistency
            T[] snapshot;
            int count;

            lock (_snapshotLock)
            {
                count = Count;
                if (count == 0) return 0;

                snapshot = new T[count];
                var startIdx = Volatile.Read(ref _index);

                // Copy the relevant portion of the buffer
                for (int i = 0; i < count; i++)
                {
                    // Calculate the index going backwards from current position
                    var bufferIdx = (int)(((startIdx - count + i) % _buffer.Length + _buffer.Length) % _buffer.Length);
                    snapshot[i] = _buffer[bufferIdx];
                }
            }

            // Count matches outside the lock
            var matched = 0;
            for (int i = 0; i < count; i++)
            {
                if (predicate(snapshot[i]))
                    matched++;
            }

            return matched;
        }

        /// <summary>
        /// Resets the buffer including clearing all stored values.
        /// Thread-safe when no concurrent Add operations are expected.
        /// </summary>
        public void Reset()
        {
            lock (_snapshotLock)
            {
                Interlocked.Exchange(ref _index, 0);
                // Clear the buffer to prevent stale reads
                Array.Clear(_buffer, 0, _buffer.Length);
            }
        }
    }
}
