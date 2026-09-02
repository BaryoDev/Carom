// Copyright (c) BaryoDev. All rights reserved.
// Licensed under the MPL-2.0 license. See LICENSE file in the project root for full license information.

using System;
using System.Diagnostics;
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
        private long _openedAtTimestamp;
        private int _hasOpened; // 0 = never opened, 1 = opened at least once
        private readonly RingBuffer<Outcome> _recentResults;

        // Sampling duration in Stopwatch.Frequency units; long.MaxValue = no expiry.
        private readonly long _samplingDurationTimestamps;

        // One recorded call outcome. Wider than a machine word; a torn read in the
        // ring buffer's seqlock path is discarded by its version check.
        private readonly struct Outcome
        {
            public readonly bool Success;
            public readonly long Timestamp;

            public Outcome(bool success, long timestamp)
            {
                Success = success;
                Timestamp = timestamp;
            }
        }

        // Monotonic timestamp source in Stopwatch.Frequency units. DateTime.UtcNow
        // moves backwards on NTP corrections and jumps forward on VM resume; a
        // backwards step used to extend the open period by the size of the step.
        // Injectable so tests can drive the clock instead of sleeping.
        private readonly Func<long> _timestamp;

        public CircuitState State => (CircuitState)Volatile.Read(ref _state);

        public CushionState(int samplingWindow, Func<long>? timestamp = null, TimeSpan? samplingDuration = null)
        {
            _recentResults = new RingBuffer<Outcome>(samplingWindow);
            _state = (int)CircuitState.Closed;
            _timestamp = timestamp ?? Stopwatch.GetTimestamp;
            _samplingDurationTimestamps = samplingDuration.HasValue
                ? ToTimestampUnits(samplingDuration.Value)
                : long.MaxValue;
        }

        // Out of range double to long casts are unspecified before .NET 9 and can
        // go negative, so saturate at the no-expiry sentinel instead.
        private static long ToTimestampUnits(TimeSpan duration)
        {
            double units = duration.TotalSeconds * Stopwatch.Frequency;
            return units >= long.MaxValue ? long.MaxValue : (long)units;
        }

        /// <summary>
        /// Records a successful operation.
        /// </summary>
        public void RecordSuccess()
        {
            _recentResults.Add(new Outcome(true, _timestamp()));
            Interlocked.Increment(ref _successCount);
        }

        /// <summary>
        /// Records a failed operation.
        /// </summary>
        public void RecordFailure()
        {
            _recentResults.Add(new Outcome(false, _timestamp()));
            Interlocked.Increment(ref _failureCount);
            Interlocked.Exchange(ref _lastFailureTicks, DateTime.UtcNow.Ticks);
        }

        // Counts failures no older than the sampling duration. Compared as
        // now - timestamp to stay overflow-safe with the MaxValue sentinel.
        private int CountFreshFailures()
        {
            return _recentResults.CountWhere(
                (Now: _timestamp(), Duration: _samplingDurationTimestamps),
                static (o, s) => !o.Success && s.Now - o.Timestamp <= s.Duration);
        }

        /// <summary>
        /// Records a failure and atomically transitions to Open if the threshold is met.
        /// Combines record + check + transition to avoid race with concurrent Close/Reset.
        /// The window does not need to be full: the threshold trips on its own.
        /// Returns true if the circuit was opened by this call.
        /// </summary>
        public bool RecordFailureAndTryOpen(int failureThreshold)
        {
            RecordFailure();

            // Only attempt to open if currently Closed
            if (State != CircuitState.Closed)
                return false;

            var failures = CountFreshFailures();

            if (failures >= failureThreshold)
            {
                // Atomically transition from Closed to Open only
                if (Interlocked.CompareExchange(ref _state, (int)CircuitState.Open, (int)CircuitState.Closed) == (int)CircuitState.Closed)
                {
                    MarkOpened();
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Opens the circuit.
        /// </summary>
        public void Open()
        {
            Interlocked.Exchange(ref _state, (int)CircuitState.Open);
            MarkOpened();
        }

        // The timestamp is written before the flag: a reader that sees the flag is
        // guaranteed a valid timestamp. A raw zero sentinel would misread a fake
        // clock that legitimately starts at zero.
        private void MarkOpened()
        {
            Interlocked.Exchange(ref _openedAtTimestamp, _timestamp());
            Interlocked.Exchange(ref _hasOpened, 1);
        }

        /// <summary>
        /// Returns an inconclusive half-open probe to Open. Same transition as
        /// Open: MarkOpened restarts the delay so the next probe waits the full
        /// delay, and leaving HalfOpen keeps the single-winner property sound.
        /// Named apart because nothing failed; the probe just did not run.
        /// </summary>
        public void AbandonProbe() => Open();

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
            if (Volatile.Read(ref _hasOpened) == 0) return false;
            var openedAt = Volatile.Read(ref _openedAtTimestamp);

            var elapsedSeconds = (double)(_timestamp() - openedAt) / Stopwatch.Frequency;
            return elapsedSeconds >= halfOpenDelay.TotalSeconds;
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
