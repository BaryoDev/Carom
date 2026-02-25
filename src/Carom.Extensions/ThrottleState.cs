using System;
using System.Diagnostics;
using System.Threading;

namespace Carom.Extensions
{
    /// <summary>
    /// Token bucket implementation for rate limiting.
    /// Uses lock-free operations with bounded spin and backoff for high performance.
    /// </summary>
    internal class ThrottleState
    {
        private const int MaxSpinIterations = 10;
        private const int SpinWaitIterations = 4;

        private readonly int _maxRequests;
        private readonly int _burstSize;
        private readonly long _refillIntervalTicks;
        private readonly long _startTicks; // Use DateTime.UtcNow.Ticks instead of Stopwatch for zero allocation

        private long _tokens; // Fixed-point: actual tokens * 1000
        private long _lastRefillTicks;

        public ThrottleState(int maxRequests, TimeSpan timeWindow, int burstSize)
        {
            _maxRequests = maxRequests;
            _burstSize = burstSize;

            // Prevent division by zero
            if (timeWindow.Ticks == 0 || maxRequests == 0)
            {
                _refillIntervalTicks = 1;
            }
            else
            {
                _refillIntervalTicks = Math.Max(1, timeWindow.Ticks / maxRequests);
            }

            _startTicks = DateTime.UtcNow.Ticks;

            // Start with full bucket
            _tokens = (long)burstSize * 1000;
            _lastRefillTicks = 0;
        }

        /// <summary>
        /// Attempts to acquire a token from the bucket.
        /// Returns true if token acquired, false if rate limit exceeded.
        /// Uses bounded spin with backoff to prevent CPU starvation.
        /// </summary>
        public bool TryAcquire()
        {
            RefillTokens();

            for (int spin = 0; spin < MaxSpinIterations; spin++)
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

                // CAS failed, apply backoff before retry
                if (spin < SpinWaitIterations)
                {
                    Thread.SpinWait(1 << spin);
                }
                else
                {
                    Thread.Yield();
                }
            }

            // Exceeded max spin iterations, treat as throttled
            return false;
        }

        /// <summary>
        /// Refills tokens based on elapsed time.
        /// Uses bounded spin to prevent CPU starvation.
        /// </summary>
        private void RefillTokens()
        {
            var currentTicks = DateTime.UtcNow.Ticks - _startTicks;
            var lastTicks = Volatile.Read(ref _lastRefillTicks);
            var elapsedTicks = currentTicks - lastTicks;

            if (elapsedTicks < _refillIntervalTicks)
            {
                return; // Not enough time has passed
            }

            // Calculate how many tokens to add (with overflow protection)
            var intervalsElapsed = elapsedTicks / _refillIntervalTicks;
            if (intervalsElapsed > int.MaxValue)
            {
                intervalsElapsed = int.MaxValue;
            }

            var tokensToAdd = intervalsElapsed * 1000;
            if (tokensToAdd <= 0)
            {
                return;
            }

            // Calculate new last refill time (aligned to token intervals)
            var newLastTicks = lastTicks + (intervalsElapsed * _refillIntervalTicks);

            // Try to update last refill time with bounded spin
            for (int spin = 0; spin < MaxSpinIterations; spin++)
            {
                if (Interlocked.CompareExchange(ref _lastRefillTicks, newLastTicks, lastTicks) == lastTicks)
                {
                    // Won the race, add tokens
                    AddTokensWithSpin(tokensToAdd);
                    return;
                }

                // Lost the race, re-read and check if still needed
                lastTicks = Volatile.Read(ref _lastRefillTicks);
                if (currentTicks - lastTicks < _refillIntervalTicks)
                {
                    return; // Someone else already refilled
                }

                // Recalculate with same overflow protection as the original path
                elapsedTicks = currentTicks - lastTicks;
                intervalsElapsed = elapsedTicks / _refillIntervalTicks;
                if (intervalsElapsed > int.MaxValue)
                {
                    intervalsElapsed = int.MaxValue;
                }
                tokensToAdd = intervalsElapsed * 1000;
                if (tokensToAdd <= 0)
                {
                    return;
                }
                newLastTicks = lastTicks + (intervalsElapsed * _refillIntervalTicks);

                if (spin >= SpinWaitIterations)
                {
                    Thread.Yield();
                }
            }
        }

        /// <summary>
        /// Adds tokens to the bucket with bounded spin.
        /// </summary>
        private void AddTokensWithSpin(long tokensToAdd)
        {
            var maxTokens = (long)_burstSize * 1000;

            for (int spin = 0; spin < MaxSpinIterations; spin++)
            {
                var currentTokens = Volatile.Read(ref _tokens);
                var newTokens = Math.Min(currentTokens + tokensToAdd, maxTokens);

                if (newTokens == currentTokens)
                {
                    return; // Already at max
                }

                if (Interlocked.CompareExchange(ref _tokens, newTokens, currentTokens) == currentTokens)
                {
                    return;
                }

                if (spin >= SpinWaitIterations)
                {
                    Thread.Yield();
                }
            }
        }

        /// <summary>
        /// Gets the current number of available tokens (for testing/monitoring).
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
