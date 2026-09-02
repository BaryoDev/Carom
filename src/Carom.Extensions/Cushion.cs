// Copyright (c) BaryoDev. All rights reserved.
// Licensed under the MPL-2.0 license. See LICENSE file in the project root for full license information.

using System;
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
        /// Null means the default: everything except OperationCanceledException.
        /// </summary>
        public Func<Exception, bool>? ShouldTrip { get; }

        // Caller-side exceptions must not open the circuit; cancellation is the
        // caller giving up, not the dependency failing.
        internal static bool DefaultShouldTrip(Exception ex) => ex is not OperationCanceledException;

        internal Cushion(string serviceKey, int failureThreshold, int samplingWindow, TimeSpan halfOpenDelay,
            TimeSpan samplingDuration, Func<Exception, bool>? shouldTrip)
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
        /// Executes a synchronous action with circuit breaker protection.
        /// Uses atomic state transitions to ensure only one thread executes the test request in half-open state.
        /// </summary>
        internal T Execute<T>(Func<T> action)
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
                    if ((ShouldTrip ?? DefaultShouldTrip)(ex))
                        state.RecordFailureAndTryOpen(FailureThreshold);
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
            catch
            {
                state.Open(); // Failed, reopen
                throw;
            }
        }

        /// <summary>
        /// Executes an asynchronous action with circuit breaker protection.
        /// Uses atomic state transitions to ensure only one thread executes the test request in half-open state.
        /// </summary>
        internal async Task<T> ExecuteAsync<T>(Func<Task<T>> action)
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
                    if ((ShouldTrip ?? DefaultShouldTrip)(ex))
                        state.RecordFailureAndTryOpen(FailureThreshold);
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
            catch
            {
                state.Open(); // Failed, reopen
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

        internal CushionBuilder(string serviceKey)
        {
            _serviceKey = serviceKey;
        }

        /// <summary>
        /// Sets the failure threshold and sampling window, both counts of calls.
        /// The circuit opens as soon as the last <paramref name="outOf"/> recorded
        /// calls contain <paramref name="failures"/> failures. The window does not
        /// need to fill first: that many consecutive failures open it immediately.
        /// </summary>
        /// <param name="failures">Number of failures to trigger circuit open.</param>
        /// <param name="outOf">Number of most recent calls the failures are counted over.</param>
        public CushionBuilder OpenAfter(int failures, int outOf)
        {
            _failureThreshold = failures;
            _samplingWindow = outOf;
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
        /// Default: everything except OperationCanceledException.
        /// </summary>
        public CushionBuilder When(Func<Exception, bool> predicate)
        {
            _shouldTrip = predicate ?? throw new ArgumentNullException(nameof(predicate));
            return this;
        }

        /// <summary>
        /// Sets the half-open delay and builds the Cushion.
        /// </summary>
        public Cushion HalfOpenAfter(TimeSpan delay)
        {
            _halfOpenDelay = delay;
            return new Cushion(_serviceKey, _failureThreshold, _samplingWindow, _halfOpenDelay,
                _samplingDuration, _shouldTrip);
        }
    }
}
