using System;
using System.Diagnostics;
using System.Threading;

namespace Carom.Extensions
{
    /// <summary>
    /// Token bucket implementation for rate limiting.
    /// Uses lock-free operations for high performance.
    /// </summary>
    internal class ThrottleState
    {
        private readonly int _maxRequests;
        private readonly int _burstSize;
        private readonly long _refillIntervalTicks;
        private readonly Stopwatch _stopwatch;

        private long _tokens; // Fixed-point: actual tokens * 1000
        private long _lastRefillTicks;

        public ThrottleState(int maxRequests, TimeSpan timeWindow, int burstSize)
        {
            _maxRequests = maxRequests;
            _burstSize = burstSize;
            _refillIntervalTicks = timeWindow.Ticks / maxRequests; // Ticks per token
            _stopwatch = Stopwatch.StartNew();

            // Start with full bucket
            _tokens = (long)burstSize * 1000;
            _lastRefillTicks = _stopwatch.Elapsed.Ticks;
        }

        /// <summary>
        /// Attempts to acquire a token from the bucket.
        /// Returns true if token acquired, false if rate limit exceeded.
        /// </summary>
        public bool TryAcquire()
        {
            RefillTokens();

            while (true)
            {
                var currentTokens = Volatile.Read(ref _tokens);

                // Check if we have at least one token
                if (currentTokens < 1000) // 1000 = 1 token in fixed-point
                {
                    return false;
                }

                // Try to consume one token
                var newTokens = currentTokens - 1000;
                if (Interlocked.CompareExchange(ref _tokens, newTokens, currentTokens) == currentTokens)
                {
                    return true;
                }

                // CAS failed, retry
            }
        }

        /// <summary>
        /// Refills tokens based on elapsed time.
        /// </summary>
        private void RefillTokens()
        {
            var currentTicks = _stopwatch.Elapsed.Ticks;
            var lastTicks = Volatile.Read(ref _lastRefillTicks);
            var elapsedTicks = currentTicks - lastTicks;

            if (elapsedTicks < _refillIntervalTicks)
            {
                return; // Not enough time has passed
            }

            // Calculate how many tokens to add
            var tokensToAdd = (elapsedTicks / _refillIntervalTicks) * 1000;

            if (tokensToAdd <= 0)
            {
                return;
            }

            // Calculate new last refill time (aligned to token intervals)
            var intervalsElapsed = elapsedTicks / _refillIntervalTicks;
            var newLastTicks = lastTicks + (intervalsElapsed * _refillIntervalTicks);

            // Try to update last refill time
            if (Interlocked.CompareExchange(ref _lastRefillTicks, newLastTicks, lastTicks) != lastTicks)
            {
                return; // Another thread is refilling
            }

            // Add tokens up to burst size
            while (true)
            {
                var currentTokens = Volatile.Read(ref _tokens);
                var maxTokens = (long)_burstSize * 1000;
                var newTokens = Math.Min(currentTokens + tokensToAdd, maxTokens);

                if (Interlocked.CompareExchange(ref _tokens, newTokens, currentTokens) == currentTokens)
                {
                    break;
                }
            }
        }

        /// <summary>
        /// Gets the current number of available tokens (for testing).
        /// </summary>
        public double AvailableTokens
        {
            get
            {
                RefillTokens();
                return Volatile.Read(ref _tokens) / 1000.0;
            }
        }
    }
}
