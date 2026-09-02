// Copyright (c) BaryoDev. All rights reserved.
// Licensed under the MPL-2.0 license. See LICENSE file in the project root for full license information.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Carom.Extensions
{
    /// <summary>
    /// Extension methods for integrating Circuit Breaker (Cushion) with Carom retry logic.
    /// Retry runs inside the circuit breaker: one logical call records one outcome,
    /// the retry chain's final result, however many attempts it took. A call that
    /// arrives at an open circuit fails fast and is never retried.
    /// </summary>
    public static class CaromCushionExtensions
    {
        // Kept for nested breakers: an inner circuit opening mid-chain must stop
        // the retries instead of backing off against it.
        internal static bool DefaultShouldBounce(Exception ex) => ex is not CircuitOpenException;

        /// <summary>
        /// Executes a synchronous shot with circuit breaker protection.
        /// Circuit breaker logic wraps retry logic, so the sampling window sees
        /// one entry per logical call, not one per attempt.
        /// </summary>
        public static T Shot<T>(
            Func<T> action,
            Cushion cushion,
            int retries = 3,
            TimeSpan? baseDelay = null,
            Func<Exception, bool>? shouldBounce = null,
            bool disableJitter = false)
        {
            return cushion.Execute(() => global::Carom.Carom.Shot(
                action,
                retries,
                baseDelay,
                shouldBounce ?? DefaultShouldBounce,
                disableJitter));
        }

        /// <summary>
        /// Executes a synchronous shot with circuit breaker and Bounce configuration.
        /// </summary>
        public static T Shot<T>(Func<T> action, Cushion cushion, Bounce bounce)
        {
            return cushion.Execute(() => global::Carom.Carom.Shot(
                action,
                bounce.Retries,
                bounce.BaseDelay,
                bounce.ShouldBounce ?? DefaultShouldBounce,
                shouldRetryResult: null,
                bounce.DisableJitter));
        }

        /// <summary>
        /// Executes an asynchronous shot with circuit breaker protection.
        /// Circuit breaker logic wraps retry logic, so the sampling window sees
        /// one entry per logical call, not one per attempt.
        /// </summary>
        public static async Task<T> ShotAsync<T>(
            Func<Task<T>> action,
            Cushion cushion,
            int retries = 3,
            TimeSpan? baseDelay = null,
            Func<Exception, bool>? shouldBounce = null,
            bool disableJitter = false,
            CancellationToken ct = default)
        {
            return await cushion.ExecuteAsync(() => global::Carom.Carom.ShotAsync(
                action,
                retries,
                baseDelay,
                timeout: null,
                shouldBounce ?? DefaultShouldBounce,
                disableJitter,
                ct)).ConfigureAwait(false);
        }

        /// <summary>
        /// Executes an asynchronous shot with circuit breaker and Bounce configuration.
        /// </summary>
        public static Task<T> ShotAsync<T>(
            Func<Task<T>> action,
            Cushion cushion,
            Bounce bounce,
            CancellationToken ct = default)
        {
            return cushion.ExecuteAsync(() => global::Carom.Carom.ShotAsync(
                action,
                bounce.Retries,
                bounce.BaseDelay,
                bounce.Timeout,
                bounce.ShouldBounce ?? DefaultShouldBounce,
                shouldRetryResult: null,
                bounce.DisableJitter,
                ct));
        }
    }
}
