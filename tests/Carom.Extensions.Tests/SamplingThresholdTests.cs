// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using Carom.Extensions;
using Xunit;

namespace Carom.Extensions.Tests
{
    /// <summary>
    /// CAR-09: OpenAfter(failures: 3, outOf: 5) required five failures, not three,
    /// because the window had to be full before the threshold was consulted. The
    /// threshold must trip as soon as the last calls contain that many failures.
    /// </summary>
    public class SamplingThresholdTests
    {
        private static void Fail(Cushion cushion)
        {
            Assert.Throws<InvalidOperationException>(() =>
                CaromCushionExtensions.Shot<int>(
                    () => throw new InvalidOperationException("boom"), cushion, retries: 0));
        }

        private static void Succeed(Cushion cushion)
        {
            Assert.Equal(1, CaromCushionExtensions.Shot(() => 1, cushion, retries: 0));
        }

        [Fact]
        public void Circuit_opens_after_exactly_the_failure_threshold()
        {
            var key = "car09-exact-" + Guid.NewGuid();
            var cushion = Cushion.ForService(key)
                .OpenAfter(failures: 3, outOf: 5)
                .HalfOpenAfter(TimeSpan.FromMinutes(10));

            Fail(cushion);
            Assert.Equal(CircuitState.Closed, Cushion.GetState(key));
            Fail(cushion);
            Assert.Equal(CircuitState.Closed, Cushion.GetState(key));
            Fail(cushion);

            Assert.Equal(CircuitState.Open, Cushion.GetState(key));
        }

        [Fact]
        public void Successes_still_push_failures_out_of_the_window()
        {
            var key = "car09-window-" + Guid.NewGuid();
            var cushion = Cushion.ForService(key)
                .OpenAfter(failures: 3, outOf: 5)
                .HalfOpenAfter(TimeSpan.FromMinutes(10));

            // Last five calls after this sequence are F S S S F: two failures only.
            Fail(cushion);
            Fail(cushion);
            Succeed(cushion);
            Succeed(cushion);
            Succeed(cushion);
            Fail(cushion);

            Assert.Equal(CircuitState.Closed, Cushion.GetState(key));

            // A third failure inside the window trips it.
            Fail(cushion);
            Fail(cushion);
            Assert.Equal(CircuitState.Open, Cushion.GetState(key));
        }
    }
}
