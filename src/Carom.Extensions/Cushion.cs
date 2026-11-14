// Copyright (c) BaryoDev. All rights reserved.
// Licensed under the MPL-2.0 license. See LICENSE file in the project root for full license information.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Carom.Extensions
{
    /// <summary>
    /// Circuit breaker configuration for protecting failing services.
    /// The "cushion" absorbs repeated impacts before opening the circuit.
    /// </summary>
    public readonly struct Cushion
    {
        /// <summary>
        /// The unique identifier for this service (e.g., "payment-api", "db-primary").
        /// </summary>
        public string ServiceKey { get; }

        /// <summary>
        /// Number of failures required to open the circuit.
        /// </summary>
        public int FailureThreshold { get; }

        /// <summary>
        /// Size of the sliding window (number of recent calls to track).
        /// </summary>
        public int SamplingWindow { get; }

        /// <summary>
        /// Time to wait before transitioning from Open to HalfOpen.
        /// </summary>
        public TimeSpan HalfOpenDelay { get; }

        /// <summary>
        /// How long a recorded outcome keeps counting toward the failure threshold.
        /// Older outcomes are ignored, so a resolved incident ages out of the window.
        /// </summary>
        public TimeSpan SamplingDuration { get; }

        /// <summary>
        /// Predicate deciding whether an exception counts as a dependency failure.
        /// Null means the default: everything except OperationCanceledException,
        /// with one exception to that: TimeoutRejectedException (the Bounce timeout) counts as a failure.
        /// </summary>
        public Func<Exception, bool>? ShouldTrip { get; }

        // Caller-side exceptions must not open the circuit; plain cancellation is the
        // caller giving up, not the dependency failing. TimeoutRejectedException derives
        // from OperationCanceledException but is the library's own deadline firing on a
        // slow dependency, so it counts.
        internal static bool DefaultShouldTrip(Exception ex) =>
            ex is TimeoutRejectedException || ex is not OperationCanceledException;

        // Injectable monotonic clock, set through CushionBuilder.WithClock; null
        // means Stopwatch.GetTimestamp.
        internal Func<long>? Timestamp { get; }

        internal Cushion(string serviceKey, int failureThreshold, int samplingWindow, TimeSpan halfOpenDelay,
            TimeSpan samplingDuration, Func<Exception, bool>? shouldTrip, Func<long>? timestamp = null)
        {
            if (string.IsNullOrWhiteSpace(serviceKey))
                throw new ArgumentException("Service key cannot be null or empty", nameof(serviceKey));
            if (failureThreshold < 1)
                throw new ArgumentException("Failure threshold must be at least 1", nameof(failureThreshold));
            if (samplingWindow < failureThreshold)
                throw new ArgumentException("Sampling window must be >= failure threshold", nameof(samplingWindow));
            if (halfOpenDelay <= TimeSpan.Zero)
                throw new ArgumentException("Half-open delay must be positive", nameof(halfOpenDelay));
            if (samplingDuration <= TimeSpan.Zero)
                throw new ArgumentException("Sampling duration must be positive", nameof(samplingDuration));

            ServiceKey = serviceKey;
            FailureThreshold = failureThreshold;
            SamplingWindow = samplingWindow;
            HalfOpenDelay = halfOpenDelay;
            SamplingDuration = samplingDuration;
            ShouldTrip = shouldTrip;
            Timestamp = timestamp;
        }

        /// <summary>
        /// Creates a cushion builder for the specified service.
        /// </summary>
        public static CushionBuilder ForService(string serviceKey) =>
            new CushionBuilder(serviceKey);

        /// <summary>
        /// Gets the current circuit state for a service, or null if no circuit
        /// breaker has been created for that key yet. Read-only: does not create
        /// state or affect LRU tracking. Intended for health checks and monitoring.
        /// </summary>
        public static CircuitState? GetState(string serviceKey) =>
            CushionStore.TryGetState(serviceKey, out var state) ? state : (CircuitState?)null;

        /// <summary>
        /// Maximum number of circuit breakers kept in the process-wide store (default 1000).
        /// When a new key pushes the store over this size, the least recently used breakers
        /// are evicted, about 10% of the cap beyond the overflow. An evicted breaker is
        /// recreated Closed on its next call, so an open circuit that falls out of the store
        /// silently resets. Set this above the number of distinct keys you expect.
        /// Must be at least 1. Lowering it does not evict until the next new key arrives.
        /// </summary>
        public static int MaxBreakers
        {
            get => CushionStore.MaxSize;
            set
            {
                if (value < 1)
                    throw new ArgumentOutOfRangeException(nameof(value), "MaxBreakers must be at least 1");
                CushionStore.MaxSize = value;
            }
        }

        /// <summary>
        /// Forgets the breaker for a key. Its next call starts a new breaker, Closed and with
        /// an empty window, using whatever settings that call registers. Returns false when
        /// no breaker was stored for the key. Use it to close an open circuit by hand, or to
        /// change a key's settings.
        /// </summary>
        public static bool Reset(string serviceKey) => CushionStore.Remove(serviceKey);

        /// <summary>
        /// Executes a synchronous action with circuit breaker protection.
        /// Uses atomic state transitions to ensure only one thread executes the test request in half-open state.
        /// Does not retry: wrap the action in a Carom shot yourself if you want retries inside the breaker.
        /// </summary>
        public T Execute<T>(Func<T> action)
        {
            var state = CushionStore.GetOrCreate(ServiceKey, this);

            // Fast path: circuit closed
            if (state.State == CircuitState.Closed)
            {
                try
                {
                    var result = action();
                    state.RecordSuccess();
                    return result;
                }
                catch (Exception ex)
                {
                    // Only exceptions the predicate blames on the dependency count.
                    if ((ShouldTrip ?? DefaultShouldTrip)(ex) && state.RecordFailureAndTryOpen(FailureThreshold))
                    {
                        // Raised once per closed-to-open transition, not per failure.
                        var onOpened = CaromHooks.OnCircuitOpened;
                        if (onOpened != null) CaromHooks.Invoke(onOpened, new CircuitOpenedSignal(ServiceKey));
                    }
                    throw;
                }
            }

            // Circuit open: check if we can attempt reset
            if (state.State == CircuitState.Open)
            {
                if (state.CanAttemptReset(HalfOpenDelay))
                {
                    // Atomically try to transition to half-open
                    // Only one thread will succeed and execute the test request
                    if (state.TryTransitionToHalfOpen())
                    {
                        // This thread won the race - execute test request
                        return ExecuteHalfOpenTest(state, action);
                    }
                    // Lost the race - another thread is testing
                    // Fall through to check if still open
                }

                // Still open or lost the race
                if (state.State == CircuitState.Open)
                {
                    throw new CircuitOpenException(ServiceKey);
                }
            }

            // Half-open: only the thread that transitioned should execute
            // Other threads arriving here should be rejected
            if (state.State == CircuitState.HalfOpen)
            {
                // If we're here without having won the transition, reject
                throw new CircuitOpenException(ServiceKey);
            }

            throw new InvalidOperationException($"Invalid circuit state: {state.State}");
        }

        /// <summary>
        /// Executes the test request in half-open state.
        /// </summary>
        private T ExecuteHalfOpenTest<T>(CushionState state, Func<T> action)
        {
            try
            {
                var result = action();
                state.Close(); // Success! Close circuit
                return result;
            }
            catch (Exception ex)
            {
                if ((ShouldTrip ?? DefaultShouldTrip)(ex))
                {
                    state.Open(); // Failed probe: dependency still bad, reopen
                }
                else
                {
                    // Excluded exception: inconclusive, so neither close nor count
                    // a failure; back to Open with a restarted delay, never wedged.
                    state.AbandonProbe();
                }
                throw;
            }
        }

        /// <summary>
        /// Executes an asynchronous action with circuit breaker protection.
        /// Uses atomic state transitions to ensure only one thread executes the test request in half-open state.
        /// Does not retry: use CaromCushionExtensions.ShotAsync for retries inside the breaker.
        /// </summary>
        public async Task<T> ExecuteAsync<T>(Func<Task<T>> action)
        {
            var state = CushionStore.GetOrCreate(ServiceKey, this);

            // Fast path: circuit closed
            if (state.State == CircuitState.Closed)
            {
                try
                {
                    var result = await action().ConfigureAwait(false);
                    state.RecordSuccess();
                    return result;
                }
                catch (Exception ex)
                {
                    // Only exceptions the predicate blames on the dependency count.
                    if ((ShouldTrip ?? DefaultShouldTrip)(ex) && state.RecordFailureAndTryOpen(FailureThreshold))
                    {
                        // Raised once per closed-to-open transition, not per failure.
                        var onOpened = CaromHooks.OnCircuitOpened;
                        if (onOpened != null) CaromHooks.Invoke(onOpened, new CircuitOpenedSignal(ServiceKey));
                    }
                    throw;
                }
            }

            // Circuit open: check if we can attempt reset
            if (state.State == CircuitState.Open)
            {
                if (state.CanAttemptReset(HalfOpenDelay))
                {
                    // Atomically try to transition to half-open
                    // Only one thread will succeed and execute the test request
                    if (state.TryTransitionToHalfOpen())
                    {
                        // This thread won the race - execute test request
                        return await ExecuteHalfOpenTestAsync(state, action).ConfigureAwait(false);
                    }
                    // Lost the race - another thread is testing
                    // Fall through to check if still open
                }

                // Still open or lost the race
                if (state.State == CircuitState.Open)
                {
                    throw new CircuitOpenException(ServiceKey);
                }
            }

            // Half-open: only the thread that transitioned should execute
            // Other threads arriving here should be rejected
            if (state.State == CircuitState.HalfOpen)
            {
                // If we're here without having won the transition, reject
                throw new CircuitOpenException(ServiceKey);
            }

            throw new InvalidOperationException($"Invalid circuit state: {state.State}");
        }

        /// <summary>
        /// Executes an asynchronous action with circuit breaker protection.
        /// The action receives <paramref name="ct"/> unchanged. The breaker itself never
        /// cancels it, so the action must honour the token for a timeout to stop the call.
        /// Does not retry: use CaromCushionExtensions.ShotAsync for retries inside the breaker.
        /// </summary>
        public Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct = default)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            return ExecuteAsync(() => action(ct));
        }

        /// <summary>
        /// Executes the test request in half-open state asynchronously.
        /// </summary>
        private async Task<T> ExecuteHalfOpenTestAsync<T>(CushionState state, Func<Task<T>> action)
        {
            try
            {
                var result = await action().ConfigureAwait(false);
                state.Close(); // Success! Close circuit
                return result;
            }
            catch (Exception ex)
            {
                if ((ShouldTrip ?? DefaultShouldTrip)(ex))
                {
                    state.Open(); // Failed probe: dependency still bad, reopen
                }
                else
                {
                    // Excluded exception: inconclusive, see the sync path above.
                    state.AbandonProbe();
                }
                throw;
            }
        }
    }

    /// <summary>
    /// Fluent builder for Cushion configuration.
    /// </summary>
    public class CushionBuilder
    {
        private readonly string _serviceKey;
        private int _failureThreshold = 5;
        private int _samplingWindow = 10;
        private TimeSpan _halfOpenDelay = TimeSpan.FromSeconds(30);
        private TimeSpan _samplingDuration = TimeSpan.FromMinutes(1);
        private Func<Exception, bool>? _shouldTrip;
        private Func<long>? _timestamp;

        internal CushionBuilder(string serviceKey)
        {
            _serviceKey = serviceKey;
        }

        /// <summary>
        /// Sets the failure threshold and sampling window, both counts of calls.
        /// The failure count is absolute, not a ratio: the circuit opens as soon as
        /// <paramref name="failures"/> failures are recorded within the tracking
        /// window, with no minimum call volume required. The window only bounds how
        /// far back failures are counted; it has no say in when the circuit opens.
        /// </summary>
        /// <param name="failures">Number of failures to trigger circuit open.</param>
        /// <param name="trackingLast">Number of most recent calls the failures are counted over.</param>
        public CushionBuilder OpenAfter(int failures, int trackingLast)
        {
            _failureThreshold = failures;
            _samplingWindow = trackingLast;
            return this;
        }

        /// <summary>
        /// Sets how long a recorded outcome keeps counting toward the threshold.
        /// Default: one minute. Older outcomes are ignored when counting failures.
        /// </summary>
        public CushionBuilder WithinLast(TimeSpan duration)
        {
            _samplingDuration = duration;
            return this;
        }

        /// <summary>
        /// Sets which exceptions count as dependency failures. Exceptions the
        /// predicate rejects are rethrown without touching the circuit, so a bug in
        /// calling code cannot open the circuit for a healthy dependency.
        /// Default: everything except OperationCanceledException, but a Bounce
        /// timeout (TimeoutRejectedException) counts as a failure.
        /// </summary>
        public CushionBuilder When(Func<Exception, bool> predicate)
        {
            _shouldTrip = predicate ?? throw new ArgumentNullException(nameof(predicate));
            return this;
        }

        /// <summary>
        /// Replaces the clock the breaker reads, so half-open and window expiry can be tested
        /// without sleeping. The function returns a monotonic timestamp in
        /// <see cref="System.Diagnostics.Stopwatch.Frequency"/> units per second, the same
        /// units as <see cref="System.Diagnostics.Stopwatch.GetTimestamp"/>, which is the default.
        /// </summary>
        public CushionBuilder WithClock(Func<long> timestamp)
        {
            _timestamp = timestamp ?? throw new ArgumentNullException(nameof(timestamp));
            return this;
        }

        // Kept for the existing tests.
        internal CushionBuilder WithTimestamp(Func<long> timestamp) => WithClock(timestamp);

        /// <summary>
        /// Sets the half-open delay and builds the Cushion.
        /// </summary>
        public Cushion HalfOpenAfter(TimeSpan delay)
        {
            _halfOpenDelay = delay;
            return new Cushion(_serviceKey, _failureThreshold, _samplingWindow, _halfOpenDelay,
                _samplingDuration, _shouldTrip, _timestamp);
        }
    }
}
