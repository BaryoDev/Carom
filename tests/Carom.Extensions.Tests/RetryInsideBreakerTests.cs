// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Threading.Tasks;
using Carom;
using Carom.Extensions;
using Xunit;

namespace Carom.Extensions.Tests
{
    /// <summary>
    /// CAR-03: retry used to wrap the breaker, so one logical call with four
    /// retries wrote five entries into the sampling window and opened a circuit
    /// sized for five independent failures. Retry now runs inside the breaker and
    /// the circuit records the retry chain's final result only.
    /// </summary>
    public class RetryInsideBreakerTests
    {
        private static readonly TimeSpan Tiny = TimeSpan.FromMilliseconds(1);

        [Fact]
        public void A_single_retried_call_records_one_outcome_not_one_per_attempt()
        {
            var key = "car03-depth-" + Guid.NewGuid();
            var cushion = Cushion.ForService(key)
                .OpenAfter(failures: 2, trackingLast: 5)
                .HalfOpenAfter(TimeSpan.FromMinutes(10));

            Assert.Throws<InvalidOperationException>(() =>
                CaromCushionExtensions.Shot<int>(
                    () => throw new InvalidOperationException("down"),
                    cushion, retries: 4, baseDelay: Tiny, disableJitter: true));

            // Five attempts, one logical call, one recorded failure.
            Assert.Equal(CircuitState.Closed, Cushion.GetState(key));

            // The second logical failure reaches the threshold of two.
            Assert.Throws<InvalidOperationException>(() =>
                CaromCushionExtensions.Shot<int>(
                    () => throw new InvalidOperationException("down"),
                    cushion, retries: 0));
            Assert.Equal(CircuitState.Open, Cushion.GetState(key));
        }

        [Fact]
        public async Task A_single_retried_call_records_one_outcome_async()
        {
            var key = "car03-depth-async-" + Guid.NewGuid();
            var cushion = Cushion.ForService(key)
                .OpenAfter(failures: 2, trackingLast: 5)
                .HalfOpenAfter(TimeSpan.FromMinutes(10));

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                CaromCushionExtensions.ShotAsync<int>(
                    () => throw new InvalidOperationException("down"),
                    cushion, retries: 4, baseDelay: Tiny, disableJitter: true));

            Assert.Equal(CircuitState.Closed, Cushion.GetState(key));
        }

        [Fact]
        public void A_single_retried_call_records_one_outcome_with_the_Bounce_overload()
        {
            var key = "car03-bounce-" + Guid.NewGuid();
            var cushion = Cushion.ForService(key)
                .OpenAfter(failures: 2, trackingLast: 5)
                .HalfOpenAfter(TimeSpan.FromMinutes(10));

            Assert.Throws<InvalidOperationException>(() =>
                CaromCushionExtensions.Shot<int>(
                    () => throw new InvalidOperationException("down"),
                    cushion, Bounce.Times(4).WithDelay(Tiny).WithoutJitter()));

            Assert.Equal(CircuitState.Closed, Cushion.GetState(key));
        }

        [Fact]
        public void A_failure_recovered_by_retry_counts_as_a_success()
        {
            var key = "car03-recovered-" + Guid.NewGuid();
            var cushion = Cushion.ForService(key)
                .OpenAfter(failures: 1, trackingLast: 1)
                .HalfOpenAfter(TimeSpan.FromMinutes(10));

            var attempts = 0;
            var result = CaromCushionExtensions.Shot(() =>
            {
                attempts++;
                if (attempts == 1) throw new InvalidOperationException("blip");
                return 42;
            }, cushion, retries: 1, baseDelay: Tiny, disableJitter: true);

            Assert.Equal(42, result);
            Assert.Equal(2, attempts);
            Assert.Equal(CircuitState.Closed, Cushion.GetState(key));
        }
    }
}
