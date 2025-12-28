using System;
using System.Threading;

namespace Carom.Extensions
{
    /// <summary>
    /// Lock-free ring buffer for tracking recent operation results.
    /// Used by circuit breaker to maintain sliding window of success/failure.
    /// </summary>
    internal class RingBuffer<T>
    {
        private readonly T[] _buffer;
        private int _index;

        public RingBuffer(int capacity)
        {
            if (capacity <= 0)
                throw new ArgumentException("Capacity must be positive", nameof(capacity));

            _buffer = new T[capacity];
        }

        /// <summary>
        /// Adds an item to the buffer (thread-safe).
        /// </summary>
        public void Add(T item)
        {
            var idx = Interlocked.Increment(ref _index) - 1;
            _buffer[idx % _buffer.Length] = item;
        }

        /// <summary>
        /// Gets the current count of items in the buffer (up to capacity).
        /// </summary>
        public int Count => Math.Min(Volatile.Read(ref _index), _buffer.Length);

        /// <summary>
        /// Counts items matching the predicate (thread-safe read).
        /// </summary>
        public int CountWhere(Func<T, bool> predicate)
        {
            var count = Count;
            var matched = 0;

            for (int i = 0; i < count; i++)
            {
                if (predicate(_buffer[i]))
                    matched++;
            }

            return matched;
        }

        /// <summary>
        /// Resets the buffer (NOT thread-safe - use only when no concurrent access).
        /// </summary>
        public void Reset()
        {
            Interlocked.Exchange(ref _index, 0);
        }
    }
}
