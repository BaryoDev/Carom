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
    /// Issue #8: time-dependent decisions read DateTime.UtcNow, which moves under NTP
    /// corrections, and could only be tested by sleeping, which caps every assertion
    /// at "at least one" rather than "exactly one". With an injected monotonic source
    /// these tests advance a fake clock and assert exact counts, with no Task.Delay
    /// anywhere.
    /// </summary>
    public class MonotonicClockTests
    {
        private static long Seconds(double s) => (long)(s * Stopwatch.Frequency);

        [Fact]
        public void Circuit_admits_exactly_one_test_request_after_exactly_the_half_open_delay()
        {
            var now = Seconds(1000);
            var state = new CushionState(samplingWindow: 1, () => now);

            state.Open();
            var delay = TimeSpan.FromSeconds(30);

            Assert.False(state.CanAttemptReset(delay));

            now = Seconds(1000) + Seconds(29.9);
            Assert.False(state.CanAttemptReset(delay), "reset allowed before the delay elapsed");

            now = Seconds(1000) + Seconds(30);
            Assert.True(state.CanAttemptReset(delay), "reset refused after the delay elapsed");

            // Exactly one caller wins the half-open transition
            Assert.True(state.TryTransitionToHalfOpen());
            Assert.False(state.TryTransitionToHalfOpen());
        }

        [Fact]
        public void A_circuit_that_never_opened_never_allows_reset()
        {
            // A fake clock legitimately starting at zero must not read as "opened at
            // zero"; the opened flag, not a zero sentinel, carries that state.
            var now = 0L;
            var state = new CushionState(samplingWindow: 1, () => now);

            Assert.False(state.CanAttemptReset(TimeSpan.Zero));
            now = Seconds(3600);
            Assert.False(state.CanAttemptReset(TimeSpan.FromSeconds(1)));
        }

        [Fact]
        public void An_opened_circuit_at_clock_zero_still_resets()
        {
            var now = 0L;
            var state = new CushionState(samplingWindow: 1, () => now);
            state.Open();

            Assert.False(state.CanAttemptReset(TimeSpan.FromSeconds(5)));
            now = Seconds(5);
            Assert.True(state.CanAttemptReset(TimeSpan.FromSeconds(5)));
        }

        [Fact]
        public void Bucket_grants_exactly_one_token_per_refill_interval()
        {
            var now = Seconds(500);
            // 10 requests per 10 seconds: one token per second, burst of 2
            var state = new ThrottleState(maxRequests: 10, TimeSpan.FromSeconds(10), burstSize: 2, () => now);

            Assert.True(state.TryAcquire());
            Assert.True(state.TryAcquire());
            Assert.False(state.TryAcquire()); // burst drained

            now += Seconds(1);
            Assert.True(state.TryAcquire());  // exactly one token was granted
            Assert.False(state.TryAcquire()); // and no more than one
        }

        [Fact]
        public void Bucket_refill_accumulates_whole_intervals_only()
        {
            var now = Seconds(500);
            var state = new ThrottleState(maxRequests: 10, TimeSpan.FromSeconds(10), burstSize: 5, () => now);

            for (int i = 0; i < 5; i++) Assert.True(state.TryAcquire());
            Assert.False(state.TryAcquire());

            now += Seconds(3.5); // 3 whole intervals, half an interval discarded
            for (int i = 0; i < 3; i++) Assert.True(state.TryAcquire());
            Assert.False(state.TryAcquire());
        }

        [Fact]
        public void Bucket_never_refills_past_the_burst_size()
        {
            var now = Seconds(500);
            var state = new ThrottleState(maxRequests: 10, TimeSpan.FromSeconds(10), burstSize: 3, () => now);

            Assert.True(state.TryAcquire()); // 2 left

            now += Seconds(3600); // an hour of intervals
            for (int i = 0; i < 3; i++) Assert.True(state.TryAcquire());
            Assert.False(state.TryAcquire()); // burst size caps the refill
        }
    }
}
