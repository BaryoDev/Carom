// Copyright (c) BaryoDev. All rights reserved.
// Licensed under the MPL-2.0 license. See LICENSE file in the project root for full license information.

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
        /// <summary>
        /// Ceiling for any computed delay, jittered or fixed. Also clamps the floor,
        /// so a baseDelay above the cap cannot invert the jitter range.
        /// </summary>
        internal const double MaxDelayMilliseconds = 30000;

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
                // Fixed exponential backoff: base * 2^attempt, capped
                var multiplier = Math.Pow(2, attempt);
                var delayMs = baseDelay.TotalMilliseconds * multiplier;
                return TimeSpan.FromMilliseconds(Math.Min(delayMs, MaxDelayMilliseconds));
            }

            // Decorrelated jitter: rand(base, prev * 3)
            // This spreads retries across time, preventing synchronized retry storms
            // The floor is clamped too: a baseDelay above the cap would otherwise invert
            // the range and produce delays exceeding the 30-second ceiling.
            var minMs = Math.Min(baseDelay.TotalMilliseconds, MaxDelayMilliseconds);
            var maxMs = previousDelay.TotalMilliseconds * 3;

            // Ensure max is at least min
            if (maxMs < minMs)
            {
                maxMs = minMs * 3;
            }

            // Cap the maximum delay
            maxMs = Math.Min(maxMs, MaxDelayMilliseconds);

            var jitteredMs = minMs + (Random.NextDouble() * (maxMs - minMs));
            return TimeSpan.FromMilliseconds(jitteredMs);
        }

        /// <summary>
        /// Gets the default base delay (100ms).
        /// </summary>
        public static TimeSpan DefaultBaseDelay => TimeSpan.FromMilliseconds(100);
    }
}
