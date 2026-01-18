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

        internal Cushion(string serviceKey, int failureThreshold, int samplingWindow, TimeSpan halfOpenDelay)
        {
            if (string.IsNullOrWhiteSpace(serviceKey))
                throw new ArgumentException("Service key cannot be null or empty", nameof(serviceKey));
            if (failureThreshold < 1)
                throw new ArgumentException("Failure threshold must be at least 1", nameof(failureThreshold));
            if (samplingWindow < failureThreshold)
                throw new ArgumentException("Sampling window must be >= failure threshold", nameof(samplingWindow));
            if (halfOpenDelay <= TimeSpan.Zero)
                throw new ArgumentException("Half-open delay must be positive", nameof(halfOpenDelay));

            ServiceKey = serviceKey;
            FailureThreshold = failureThreshold;
            SamplingWindow = samplingWindow;
            HalfOpenDelay = halfOpenDelay;
        }

        /// <summary>
        /// Creates a cushion builder for the specified service.
        /// </summary>
        public static CushionBuilder ForService(string serviceKey) =>
            new CushionBuilder(serviceKey);

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
                catch
                {
                    state.RecordFailure();

                    if (state.ShouldOpen(FailureThreshold, SamplingWindow))
                    {
                        state.Open();
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
                catch
                {
                    state.RecordFailure();

                    if (state.ShouldOpen(FailureThreshold, SamplingWindow))
                    {
                        state.Open();
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

        internal CushionBuilder(string serviceKey)
        {
            _serviceKey = serviceKey;
        }

        /// <summary>
        /// Sets the failure threshold and sampling window.
        /// </summary>
        /// <param name="failures">Number of failures to trigger circuit open.</param>
        /// <param name="within">Size of sliding window to track.</param>
        public CushionBuilder OpenAfter(int failures, int within)
        {
            _failureThreshold = failures;
            _samplingWindow = within;
            return this;
        }

        /// <summary>
        /// Sets the half-open delay and builds the Cushion.
        /// </summary>
        public Cushion HalfOpenAfter(TimeSpan delay)
        {
            _halfOpenDelay = delay;
            return new Cushion(_serviceKey, _failureThreshold, _samplingWindow, _halfOpenDelay);
        }
    }
}
