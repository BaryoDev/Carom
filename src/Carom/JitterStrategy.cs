using System;
using System.Threading;

namespace Carom
{
    /// <summary>
    /// Provides decorrelated jitter delay calculation for retry operations.
    /// Uses the AWS-recommended formula: next_delay = rand(base_delay, prev_delay * 3)
    /// </summary>
    internal static class JitterStrategy
    {
        [ThreadStatic]
        private static Random? _random;

        private static Random Random => _random ??= new Random(Guid.NewGuid().GetHashCode());

        /// <summary>
        /// Calculates the next delay using decorrelated jitter.
        /// </summary>
        /// <param name="baseDelay">The minimum delay floor.</param>
        /// <param name="previousDelay">The previous delay used (or baseDelay for first retry).</param>
        /// <param name="disableJitter">If true, returns a fixed exponential backoff instead.</param>
        /// <param name="attempt">The current attempt number (1-indexed).</param>
        /// <returns>The delay to wait before the next retry.</returns>
        public static TimeSpan CalculateDelay(
            TimeSpan baseDelay,
            TimeSpan previousDelay,
            bool disableJitter,
            int attempt)
        {
            if (disableJitter)
            {
                // Fixed exponential backoff: base * 2^attempt, capped at 30 seconds
                var multiplier = Math.Pow(2, attempt);
                var delayMs = baseDelay.TotalMilliseconds * multiplier;
                return TimeSpan.FromMilliseconds(Math.Min(delayMs, 30000));
            }

            // Decorrelated jitter: rand(base, prev * 3)
            // This spreads retries across time, preventing synchronized retry storms
            var minMs = baseDelay.TotalMilliseconds;
            var maxMs = previousDelay.TotalMilliseconds * 3;

            // Ensure max is at least min
            if (maxMs < minMs)
            {
                maxMs = minMs * 3;
            }

            // Cap the maximum delay at 30 seconds
            maxMs = Math.Min(maxMs, 30000);

            var jitteredMs = minMs + (Random.NextDouble() * (maxMs - minMs));
            return TimeSpan.FromMilliseconds(jitteredMs);
        }

        /// <summary>
        /// Gets the default base delay (100ms).
        /// </summary>
        public static TimeSpan DefaultBaseDelay => TimeSpan.FromMilliseconds(100);
    }
}
