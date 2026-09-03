// Copyright (c) BaryoDev. All rights reserved.
// Licensed under the MPL-2.0 license. See LICENSE file in the project root for full license information.

using System;

namespace Carom
{
    /// <summary>
    /// Payload for a retry about to happen. Attempt is 1 for the first retry.
    /// ExceptionTypeName is null when the retry was triggered by a result predicate.
    /// </summary>
    public readonly struct RetrySignal
    {
        /// <summary>The number of the retry about to run, starting at 1.</summary>
        public int Attempt { get; }

        /// <summary>The backoff delay waited before this retry.</summary>
        public TimeSpan Delay { get; }

        /// <summary>Type name of the exception that triggered the retry, or null for a result retry.</summary>
        public string? ExceptionTypeName { get; }

        public RetrySignal(int attempt, TimeSpan delay, string? exceptionTypeName)
        {
            Attempt = attempt;
            Delay = delay;
            ExceptionTypeName = exceptionTypeName;
        }
    }

    /// <summary>
    /// Payload for a circuit breaker transitioning from closed to open.
    /// Raised once per transition, not once per failure.
    /// </summary>
    public readonly struct CircuitOpenedSignal
    {
        /// <summary>The service key of the circuit that opened.</summary>
        public string ServiceKey { get; }

        public CircuitOpenedSignal(string serviceKey)
        {
            ServiceKey = serviceKey;
        }
    }

    /// <summary>
    /// Payload for a bulkhead rejecting a call because the compartment is full.
    /// </summary>
    public readonly struct BulkheadRejectedSignal
    {
        /// <summary>The resource key of the full compartment.</summary>
        public string ResourceKey { get; }

        public BulkheadRejectedSignal(string resourceKey)
        {
            ResourceKey = resourceKey;
        }
    }

    /// <summary>
    /// Payload for a rate limiter rejecting a call.
    /// </summary>
    public readonly struct RateLimitRejectedSignal
    {
        /// <summary>The service key of the throttle that rejected the call.</summary>
        public string ServiceKey { get; }

        public RateLimitRejectedSignal(string serviceKey)
        {
            ServiceKey = serviceKey;
        }
    }

    /// <summary>
    /// Process-wide hooks raised by Carom and Carom.Extensions. Subscribe with += and
    /// unsubscribe with -=. Plain settable delegates, not events, so a consumer or a
    /// test can read, replace or clear a hook; an event could never be reset.
    /// Nothing is computed or allocated when a hook is null, so an unsubscribed
    /// process pays nothing. Handlers run synchronously on the calling thread and
    /// must be fast and must not throw.
    /// </summary>
    public static class CaromHooks
    {
        /// <summary>Raised after the backoff delay is computed, just before a retry runs.</summary>
        public static Action<RetrySignal>? OnRetry { get; set; }

        /// <summary>Raised when a circuit breaker transitions from closed to open.</summary>
        public static Action<CircuitOpenedSignal>? OnCircuitOpened { get; set; }

        /// <summary>Raised when a bulkhead rejects a call.</summary>
        public static Action<BulkheadRejectedSignal>? OnBulkheadRejected { get; set; }

        /// <summary>Raised when a rate limiter rejects a call.</summary>
        public static Action<RateLimitRejectedSignal>? OnRateLimitRejected { get; set; }

        /// <summary>
        /// Invokes a hook without letting it break the path it observes. Raises happen inside
        /// catch blocks, so a subscriber that throws would replace the caller's exception and
        /// skip the retry entirely. That is the same failure this release fixed for a negative
        /// retry delay, and a diagnostics hook must not reintroduce it.
        /// </summary>
        internal static void Invoke<T>(Action<T> handler, T signal)
        {
            try
            {
                handler(signal);
            }
            catch
            {
                // A hook cannot change what the caller sees. Swallowed on purpose.
            }
        }
    }
}
