// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Diagnostics;
using Carom.Extensions;
using Xunit;

namespace Carom.Extensions.Tests
{
    /// <summary>
    /// CAR-10: the sampling window had no time dimension, so failures from a long
    /// resolved incident kept counting until enough traffic flushed them out of the
    /// ring buffer. Outcomes older than the sampling duration must stop counting.
    /// Uses the injectable clock, no sleeping.
    /// </summary>
    public class SamplingDurationTests
    {
        private static long Seconds(double s) => (long)(s * Stopwatch.Frequency);

        [Fact]
        public void Failures_older_than_the_sampling_duration_stop_counting()
        {
            var now = Seconds(100);
            var state = new CushionState(
                samplingWindow: 5, () => now, samplingDuration: TimeSpan.FromSeconds(2));

            // Three failures during an incident.
            for (int i = 0; i < 3; i++)
                Assert.False(state.RecordFailureAndTryOpen(failureThreshold: 5));

            // The incident is resolved; three idle seconds age those out.
            now += Seconds(3);

            // Two fresh failures alone must not open a circuit sized for five.
            Assert.False(state.RecordFailureAndTryOpen(failureThreshold: 5));
            Assert.False(state.RecordFailureAndTryOpen(failureThreshold: 5));
            Assert.Equal(CircuitState.Closed, state.State);
        }

        [Fact]
        public void Fresh_failures_within_the_duration_still_open_the_circuit()
        {
            var now = Seconds(100);
            var state = new CushionState(
                samplingWindow: 5, () => now, samplingDuration: TimeSpan.FromSeconds(2));

            for (int i = 0; i < 4; i++)
            {
                Assert.False(state.RecordFailureAndTryOpen(failureThreshold: 5));
                now += Seconds(0.1);
            }

            Assert.True(state.RecordFailureAndTryOpen(failureThreshold: 5));
            Assert.Equal(CircuitState.Open, state.State);
        }

        [Fact]
        public void Without_a_sampling_duration_old_failures_never_expire()
        {
            var now = Seconds(100);
            var state = new CushionState(samplingWindow: 3, () => now);

            Assert.False(state.RecordFailureAndTryOpen(failureThreshold: 3));
            Assert.False(state.RecordFailureAndTryOpen(failureThreshold: 3));

            now += Seconds(3600);

            Assert.True(state.RecordFailureAndTryOpen(failureThreshold: 3));
            Assert.Equal(CircuitState.Open, state.State);
        }

        [Fact]
        public void Builder_threads_the_sampling_duration_through()
        {
            var cushion = Cushion.ForService("car10-thread-" + Guid.NewGuid())
                .OpenAfter(failures: 2, trackingLast: 5)
                .WithinLast(TimeSpan.FromSeconds(7))
                .HalfOpenAfter(TimeSpan.FromSeconds(30));

            Assert.Equal(TimeSpan.FromSeconds(7), cushion.SamplingDuration);
        }

        [Fact]
        public void Builder_defaults_the_sampling_duration_to_one_minute()
        {
            var cushion = Cushion.ForService("car10-default-" + Guid.NewGuid())
                .OpenAfter(failures: 2, trackingLast: 5)
                .HalfOpenAfter(TimeSpan.FromSeconds(30));

            Assert.Equal(TimeSpan.FromMinutes(1), cushion.SamplingDuration);
        }

        [Fact]
        public void Builder_rejects_a_non_positive_sampling_duration()
        {
            Assert.Throws<ArgumentException>(() =>
                Cushion.ForService("car10-invalid-" + Guid.NewGuid())
                    .OpenAfter(failures: 2, trackingLast: 5)
                    .WithinLast(TimeSpan.Zero)
                    .HalfOpenAfter(TimeSpan.FromSeconds(30)));
        }
    }
}
