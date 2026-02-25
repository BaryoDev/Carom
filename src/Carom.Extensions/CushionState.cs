using System;
using System.Threading;

namespace Carom.Extensions
{
    /// <summary>
    /// Internal state for a circuit breaker instance.
    /// Uses lock-free operations for thread safety.
    /// </summary>
    internal class CushionState
    {
        private int _state; // 0=Closed, 1=Open, 2=HalfOpen
        private int _failureCount;
        private int _successCount;
        private long _lastFailureTicks;
        private long _openedAtTicks;
        private readonly RingBuffer<bool> _recentResults;

        public CircuitState State => (CircuitState)Volatile.Read(ref _state);

        public CushionState(int samplingWindow)
        {
            _recentResults = new RingBuffer<bool>(samplingWindow);
            _state = (int)CircuitState.Closed;
        }

        /// <summary>
        /// Records a successful operation.
        /// </summary>
        public void RecordSuccess()
        {
            _recentResults.Add(true);
            Interlocked.Increment(ref _successCount);
        }

        /// <summary>
        /// Records a failed operation.
        /// </summary>
        public void RecordFailure()
        {
            _recentResults.Add(false);
            Interlocked.Increment(ref _failureCount);
            Interlocked.Exchange(ref _lastFailureTicks, DateTime.UtcNow.Ticks);
        }

        /// <summary>
        /// Records a failure and atomically transitions to Open if the threshold is met.
        /// Combines record + check + transition to avoid race with concurrent Close/Reset.
        /// Returns true if the circuit was opened by this call.
        /// </summary>
        public bool RecordFailureAndTryOpen(int failureThreshold, int samplingWindow)
        {
            RecordFailure();

            // Only attempt to open if currently Closed
            if (State != CircuitState.Closed)
                return false;

            var failures = _recentResults.CountWhere(x => !x);
            var total = _recentResults.Count;

            if (total >= samplingWindow && failures >= failureThreshold)
            {
                // Atomically transition from Closed to Open only
                if (Interlocked.CompareExchange(ref _state, (int)CircuitState.Open, (int)CircuitState.Closed) == (int)CircuitState.Closed)
                {
                    Interlocked.Exchange(ref _openedAtTicks, DateTime.UtcNow.Ticks);
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Determines if the circuit should open based on failure threshold.
        /// </summary>
        public bool ShouldOpen(int failureThreshold, int samplingWindow)
        {
            var failures = _recentResults.CountWhere(x => !x);
            var total = _recentResults.Count;

            // Need enough samples AND enough failures
            return total >= samplingWindow && failures >= failureThreshold;
        }

        /// <summary>
        /// Opens the circuit.
        /// </summary>
        public void Open()
        {
            Interlocked.Exchange(ref _state, (int)CircuitState.Open);
            Interlocked.Exchange(ref _openedAtTicks, DateTime.UtcNow.Ticks);
        }

        /// <summary>
        /// Closes the circuit (reset to normal operation).
        /// </summary>
        public void Close()
        {
            Interlocked.Exchange(ref _state, (int)CircuitState.Closed);
            Interlocked.Exchange(ref _failureCount, 0);
            _recentResults.Reset();
        }

        /// <summary>
        /// Checks if enough time has passed to attempt reset (transition to half-open).
        /// </summary>
        public bool CanAttemptReset(TimeSpan halfOpenDelay)
        {
            var openedAtTicks = Volatile.Read(ref _openedAtTicks);
            if (openedAtTicks == 0) return false;

            var elapsed = DateTime.UtcNow.Ticks - openedAtTicks;
            return elapsed >= halfOpenDelay.Ticks;
        }

        /// <summary>
        /// Attempts to atomically transition circuit from Open to HalfOpen state.
        /// Returns true if this thread successfully transitioned, false otherwise.
        /// Only the thread that returns true should execute the test request.
        /// </summary>
        public bool TryTransitionToHalfOpen()
        {
            // Atomically try to change from Open to HalfOpen
            // Only one thread will succeed
            return Interlocked.CompareExchange(
                ref _state,
                (int)CircuitState.HalfOpen,
                (int)CircuitState.Open) == (int)CircuitState.Open;
        }

        /// <summary>
        /// Transitions circuit to half-open state (legacy method for compatibility).
        /// </summary>
        [Obsolete("Use TryTransitionToHalfOpen() instead for proper atomic behavior")]
        public void TransitionToHalfOpen()
        {
            Interlocked.CompareExchange(ref _state, (int)CircuitState.HalfOpen, (int)CircuitState.Open);
        }
    }
}
