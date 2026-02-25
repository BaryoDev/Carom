using System;
using System.Threading;

namespace Carom.Extensions
{
    /// <summary>
    /// Lock-free ring buffer for tracking recent operation results.
    /// Used by circuit breaker to maintain sliding window of success/failure.
    /// Thread-safe for concurrent Add and CountWhere operations.
    /// Uses seqlock pattern for efficient lock-free reads.
    /// </summary>
    internal class RingBuffer<T>
    {
        private readonly T[] _buffer;
        private readonly object _writeLock = new object();
        private readonly object _fallbackLock = new object();
        private long _index; // Use long to avoid overflow for much longer
        private long _version; // Seqlock version: odd = write in progress, even = stable

        /// <summary>
        /// Maximum retries before falling back to lock.
        /// </summary>
        private const int MaxSeqlockRetries = 5;

        public RingBuffer(int capacity)
        {
            if (capacity <= 0)
                throw new ArgumentException("Capacity must be positive", nameof(capacity));

            _buffer = new T[capacity];
        }

        /// <summary>
        /// Adds an item to the buffer (thread-safe).
        /// Serializes writers with a lock and uses seqlock versioning for readers.
        /// </summary>
        public void Add(T item)
        {
            lock (_writeLock)
            {
                // Increment version to odd (write in progress)
                Interlocked.Increment(ref _version);

                try
                {
                    // Increment and get the previous value
                    var idx = Interlocked.Increment(ref _index) - 1;
                    // Use positive modulo to get correct index even after overflow
                    var bufferIndex = (int)((idx % _buffer.Length + _buffer.Length) % _buffer.Length);
                    _buffer[bufferIndex] = item;
                    // Ensure the write is visible to readers before version is incremented
                    Thread.MemoryBarrier();
                }
                finally
                {
                    // Increment version to even (write complete)
                    Interlocked.Increment(ref _version);
                }
            }
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
        /// Uses seqlock pattern: retries if version changed during read.
        /// Falls back to lock after MaxSeqlockRetries attempts.
        /// </summary>
        public int CountWhere(Func<T, bool> predicate)
        {
            // Try seqlock-based read first
            for (int retry = 0; retry < MaxSeqlockRetries; retry++)
            {
                // Wait for any write to complete (version must be even)
                long versionBefore;
                var spinCount = 0;
                while (true)
                {
                    versionBefore = Volatile.Read(ref _version);
                    if ((versionBefore & 1) == 0) break; // Even = no write in progress

                    // Spin briefly waiting for write to complete
                    if (++spinCount > 100)
                    {
                        Thread.Yield();
                        spinCount = 0;
                    }
                }

                // Derive count from a single atomic read of _index to avoid TOCTOU
                var startIdx = Volatile.Read(ref _index);
                var count = (int)Math.Min(startIdx < 0 ? long.MaxValue + startIdx + 1 : startIdx, _buffer.Length);
                if (count == 0) return 0;

                var matched = 0;

                // Read directly from buffer (may be inconsistent if concurrent write)
                for (int i = 0; i < count; i++)
                {
                    var bufferIdx = (int)(((startIdx - count + i) % _buffer.Length + _buffer.Length) % _buffer.Length);
                    var item = _buffer[bufferIdx];
                    if (predicate(item))
                        matched++;
                }

                // Check if version changed during read
                var versionAfter = Volatile.Read(ref _version);
                if (versionBefore == versionAfter)
                {
                    // Version unchanged - read was consistent
                    return matched;
                }
                // Version changed - retry
            }

            // Fallback to lock after max retries
            return CountWhereWithLock(predicate);
        }

        /// <summary>
        /// Fallback implementation using lock for high contention scenarios.
        /// </summary>
        private int CountWhereWithLock(Func<T, bool> predicate)
        {
            lock (_fallbackLock)
            {
                // Derive count from a single atomic read of _index to avoid TOCTOU
                var startIdx = Volatile.Read(ref _index);
                var count = (int)Math.Min(startIdx < 0 ? long.MaxValue + startIdx + 1 : startIdx, _buffer.Length);
                if (count == 0) return 0;

                var matched = 0;

                for (int i = 0; i < count; i++)
                {
                    var bufferIdx = (int)(((startIdx - count + i) % _buffer.Length + _buffer.Length) % _buffer.Length);
                    if (predicate(_buffer[bufferIdx]))
                        matched++;
                }

                return matched;
            }
        }

        /// <summary>
        /// Resets the buffer including clearing all stored values.
        /// Thread-safe when no concurrent Add operations are expected.
        /// </summary>
        public void Reset()
        {
            lock (_writeLock)
            {
                // Increment version to odd (modification in progress)
                Interlocked.Increment(ref _version);

                try
                {
                    Interlocked.Exchange(ref _index, 0);
                    // Clear the buffer to prevent stale reads
                    Array.Clear(_buffer, 0, _buffer.Length);
                }
                finally
                {
                    // Increment version to even (modification complete)
                    Interlocked.Increment(ref _version);
                }
            }
        }
    }
}
