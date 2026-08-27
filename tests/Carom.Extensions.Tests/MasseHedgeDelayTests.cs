// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Threading;
using System.Threading.Tasks;
using Carom.Extensions;
using Xunit;

namespace Carom.Extensions.Tests
{
    /// <summary>
    /// Issue #4: an unsatisfactory result (ShouldHedge returns true) left the completed
    /// task in the candidate list, so Task.WhenAny returned instantly on every later
    /// iteration and the configured hedge delay never gated another launch. And when
    /// every attempt succeeded but none satisfied ShouldHedge, the caller got an
    /// AggregateException with zero inner exceptions instead of any of its results.
    /// </summary>
    public class MasseHedgeDelayTests
    {
        [Fact]
        public async Task Hedging_waits_the_hedge_delay_between_unsatisfactory_results()
        {
            var started = 0;
            var secondMayStart = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            var config = Masse.WithAttempts(3)
                .After(TimeSpan.FromMilliseconds(250))
                .When(r => (string?)r == "stale");

            var run = CaromMasseExtensions.ShotWithHedgingAsync<string>(async ct =>
            {
                var n = Interlocked.Increment(ref started);
                if (n >= 2) await secondMayStart.Task;
                await Task.Yield();
                return "stale";
            }, config);

            // The first attempt returns "stale" almost instantly. The second must not
            // launch until the 250ms hedge delay has elapsed, so right now there is one.
            await Task.Yield();
            Assert.Equal(1, Volatile.Read(ref started));

            secondMayStart.SetResult(true);
            var result = await run;

            Assert.Equal("stale", result); // the last result, not an empty AggregateException
            Assert.Equal(3, Volatile.Read(ref started));
        }

        [Fact]
        public async Task All_attempts_unsatisfactory_returns_the_last_result()
        {
            var config = Masse.WithAttempts(3)
                .After(TimeSpan.FromMilliseconds(1))
                .When(r => (string?)r == "stale");

            var result = await CaromMasseExtensions.ShotWithHedgingAsync(
                () => Task.FromResult("stale"), config);

            Assert.Equal("stale", result);
        }

        [Fact]
        public async Task A_satisfactory_result_still_returns_immediately()
        {
            var started = 0;

            var config = Masse.WithAttempts(4)
                .After(TimeSpan.FromMinutes(10)) // a launch gated on this would hang the test
                .When(r => (string?)r == "stale");

            var result = await CaromMasseExtensions.ShotWithHedgingAsync(async ct =>
            {
                Interlocked.Increment(ref started);
                await Task.Yield();
                return "fresh";
            }, config);

            Assert.Equal("fresh", result);
            Assert.Equal(1, Volatile.Read(ref started));
        }

        [Fact]
        public async Task Failures_still_surface_as_the_thrown_exception()
        {
            var config = Masse.WithAttempts(2)
                .After(TimeSpan.FromMilliseconds(1))
                .When(r => (string?)r == "stale");

            // One attempt throws, one returns an unsatisfactory result: the failure wins,
            // matching the pre-existing contract that exceptions surface.
            var calls = 0;
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                CaromMasseExtensions.ShotWithHedgingAsync<string>(async ct =>
                {
                    var n = Interlocked.Increment(ref calls);
                    await Task.Yield();
                    if (n == 1) return "stale";
                    throw new InvalidOperationException("attempt " + n);
                }, config));

            Assert.StartsWith("attempt", ex.Message);
        }
    }
}
