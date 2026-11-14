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
    /// unsubscribe with -=. The retry hook is the <see cref="Retrying"/> event, whose add and
    /// remove are atomic and which no consumer can overwrite. The other hooks are still plain
    /// settable delegates, so a consumer or a test can read, replace or clear them.
    /// Nothing is computed or allocated when a hook is null, so an unsubscribed
    /// process pays nothing. Handlers run synchronously on the calling thread and
    /// must be fast and must not throw.
    /// </summary>
    public static class CaromHooks
    {
        private static Action<RetrySignal>? _onRetry;

        /// <summary>
        /// Raised after the backoff delay is computed, just before a retry runs. Add and remove
        /// are atomic, and the event cannot be assigned from outside, so one subscriber cannot
        /// remove another. Each subscriber is called on its own: one that throws does not stop
        /// the ones after it.
        /// </summary>
        public static event Action<RetrySignal>? Retrying;

        /// <summary>
        /// The old settable retry hook, still raised. Assigning it replaces only what was
        /// assigned to this property, never a <see cref="Retrying"/> subscriber, but += on it is
        /// still a non-atomic read, combine and write, so two threads can lose a subscriber.
        /// </summary>
        [Obsolete("Use Retrying.")]
        public static Action<RetrySignal>? OnRetry
        {
            get => _onRetry;
            set => _onRetry = value;
        }

        /// <summary>
        /// Raises the retry hooks. Each handler runs on its own so a throwing or
        /// misbehaving one cannot skip the rest. Allocates only while a subscriber exists.
        /// </summary>
        internal static void Raise(int attempt, TimeSpan delay, Exception? cause)
        {
            var legacy = _onRetry;
            var handlers = Retrying;
            if (legacy == null && handlers == null) return;

            var signal = new RetrySignal(attempt, delay, cause?.GetType().Name);
            if (legacy != null) Invoke(legacy, signal);
            if (handlers == null) return;

            var list = handlers.GetInvocationList();
            for (int i = 0; i < list.Length; i++)
            {
                Invoke((Action<RetrySignal>)list[i], signal);
            }
        }

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
