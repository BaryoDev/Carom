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
    /// CAR-01 follow-up: the trip predicate must govern the half-open probe too.
    /// An excluded exception during the probe is inconclusive: it is not evidence
    /// about the dependency, so it neither closes the circuit nor counts as a
    /// failure. The circuit returns to Open and the half-open delay restarts, so
    /// a real probe gets its turn after the next delay. Driven entirely by the
    /// injectable clock, no sleeping.
    /// </summary>
    public class HalfOpenProbePredicateTests
    {
        private static long Seconds(double s) => (long)(s * Stopwatch.Frequency);

        private static readonly TimeSpan Delay = TimeSpan.FromSeconds(30);

        // A cushion driven by the fake clock, tripped open by one included failure.
        private static Cushion OpenedCushion(Func<long> clock, out string key)
        {
            key = "half-open-predicate-" + Guid.NewGuid();
            var cushion = Cushion.ForService(key)
                .OpenAfter(failures: 1, trackingLast: 1)
                .When(ex => ex is InvalidOperationException)
                .WithTimestamp(clock)
                .HalfOpenAfter(Delay);

            Assert.Throws<InvalidOperationException>(() =>
                CaromCushionExtensions.Shot<int>(
                    () => throw new InvalidOperationException("down"), cushion, retries: 0));
            Assert.Equal(CircuitState.Open, Cushion.GetState(key));
            return cushion;
        }

        [Fact]
        public void An_excluded_exception_during_the_probe_reopens_and_does_not_close()
        {
            var now = Seconds(100);
            var cushion = OpenedCushion(() => now, out var key);

            now += Seconds(31); // delay elapses, next call becomes the probe

            Assert.Throws<ArgumentException>(() =>
                CaromCushionExtensions.Shot<int>(
                    () => throw new ArgumentException("caller bug"), cushion, retries: 0));

            // Inconclusive: back to Open, not Closed, not wedged in HalfOpen.
            Assert.Equal(CircuitState.Open, Cushion.GetState(key));

            // And the delay restarted: an immediate call is rejected unprobed.
            var probed = false;
            Assert.Throws<CircuitOpenException>(() =>
                CaromCushionExtensions.Shot(() => { probed = true; return 1; }, cushion, retries: 0));
            Assert.False(probed, "a second probe ran before the restarted delay elapsed");
        }

        [Fact]
        public void After_an_abandoned_probe_the_next_delay_still_yields_a_real_probe()
        {
            var now = Seconds(100);
            var cushion = OpenedCushion(() => now, out var key);

            now += Seconds(31);
            Assert.Throws<ArgumentException>(() =>
                CaromCushionExtensions.Shot<int>(
                    () => throw new ArgumentException("caller bug"), cushion, retries: 0));
            Assert.Equal(CircuitState.Open, Cushion.GetState(key));

            // The restarted delay elapses; a genuine probe runs and closes it.
            now += Seconds(31);
            Assert.Equal(42, CaromCushionExtensions.Shot(() => 42, cushion, retries: 0));
            Assert.Equal(CircuitState.Closed, Cushion.GetState(key));
        }

        [Fact]
        public void An_included_exception_during_the_probe_still_reopens()
        {
            var now = Seconds(100);
            var cushion = OpenedCushion(() => now, out var key);

            now += Seconds(31);
            Assert.Throws<InvalidOperationException>(() =>
                CaromCushionExtensions.Shot<int>(
                    () => throw new InvalidOperationException("still down"), cushion, retries: 0));

            Assert.Equal(CircuitState.Open, Cushion.GetState(key));

            // Reopened with a fresh delay: an immediate call is rejected unprobed.
            var probed = false;
            Assert.Throws<CircuitOpenException>(() =>
                CaromCushionExtensions.Shot(() => { probed = true; return 1; }, cushion, retries: 0));
            Assert.False(probed, "a second probe ran before the restarted delay elapsed");
        }
    }
}
