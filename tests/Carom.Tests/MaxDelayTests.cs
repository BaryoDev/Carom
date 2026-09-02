// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Xunit;

namespace Carom.Tests
{
    /// <summary>
    /// Issue #35: the 30 second delay cap was a hardcoded constant. It is now the
    /// default of Bounce.WithMaxDelay, so callers can raise it for long backoffs
    /// or lower it for latency-sensitive paths. Existing behaviour is unchanged
    /// unless a caller asks: the JitterStrategyTests cap pins stay green.
    /// </summary>
    public class MaxDelayTests
    {
        [Fact]
        public void Default_max_delay_is_thirty_seconds()
        {
            Assert.Equal(TimeSpan.FromSeconds(30), Bounce.Times().MaxDelay);
            Assert.Equal(TimeSpan.FromSeconds(30), Bounce.For<int>().MaxDelay);
            Assert.Equal(TimeSpan.FromSeconds(30), default(Bounce).MaxDelay);
            Assert.Equal(TimeSpan.FromSeconds(30), default(Bounce<int>).MaxDelay);
        }

        [Fact]
        public void WithMaxDelay_sets_the_property()
        {
            var bounce = Bounce.Times().WithMaxDelay(TimeSpan.FromSeconds(2));

            Assert.Equal(TimeSpan.FromSeconds(2), bounce.MaxDelay);
        }

        [Fact]
        public void WithMaxDelay_survives_further_fluent_calls()
        {
            var bounce = Bounce.Times(2)
                .WithMaxDelay(TimeSpan.FromSeconds(5))
                .WithDelay(TimeSpan.FromMilliseconds(1))
                .When(ex => true)
                .WithoutJitter();

            Assert.Equal(TimeSpan.FromSeconds(5), bounce.MaxDelay);

            var typed = Bounce.For<int>(2)
                .WithMaxDelay(TimeSpan.FromSeconds(5))
                .WithDelay(TimeSpan.FromMilliseconds(1))
                .WhenResult(r => false)
                .WithoutJitter();

            Assert.Equal(TimeSpan.FromSeconds(5), typed.MaxDelay);
        }

        [Fact]
        public void WithMaxDelay_rejects_zero_and_negative()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => Bounce.Times().WithMaxDelay(TimeSpan.Zero));
            Assert.Throws<ArgumentOutOfRangeException>(() => Bounce.Times().WithMaxDelay(TimeSpan.FromSeconds(-1)));
            Assert.Throws<ArgumentOutOfRangeException>(() => Bounce.For<int>().WithMaxDelay(TimeSpan.Zero));
            Assert.Throws<ArgumentOutOfRangeException>(() => Bounce.For<int>().WithMaxDelay(TimeSpan.FromSeconds(-1)));
        }

        [Fact]
        public void Custom_max_caps_the_jittered_branch()
        {
            var baseDelay = TimeSpan.FromSeconds(1);
            var previous = TimeSpan.FromSeconds(10);
            var max = TimeSpan.FromSeconds(2);

            for (int i = 0; i < 1000; i++)
            {
                var d = JitterStrategy.CalculateDelay(baseDelay, previous, disableJitter: false, attempt: 3, max);
                Assert.True(d <= max, $"delay {d.TotalMilliseconds}ms exceeds the {max.TotalSeconds}s cap");
            }
        }

        [Fact]
        public void Custom_max_caps_the_fixed_backoff_branch()
        {
            var baseDelay = TimeSpan.FromMilliseconds(100);
            var max = TimeSpan.FromMilliseconds(500);

            // 100ms * 2^10 is far past the cap
            var d = JitterStrategy.CalculateDelay(baseDelay, baseDelay, disableJitter: true, attempt: 10, max);

            Assert.Equal(500, d.TotalMilliseconds);
        }

        [Fact]
        public void A_max_above_thirty_seconds_lifts_the_old_ceiling()
        {
            var baseDelay = TimeSpan.FromSeconds(1);
            var max = TimeSpan.FromSeconds(60);

            // 1s * 2^10 = 1024s, capped at 60s instead of the old 30s
            var d = JitterStrategy.CalculateDelay(baseDelay, baseDelay, disableJitter: true, attempt: 10, max);

            Assert.Equal(60, d.TotalSeconds);
        }

        [Fact]
        public void A_base_delay_above_a_custom_max_clamps_to_the_max()
        {
            var baseDelay = TimeSpan.FromSeconds(10);
            var max = TimeSpan.FromSeconds(1);

            for (int i = 0; i < 1000; i++)
            {
                var d = JitterStrategy.CalculateDelay(baseDelay, baseDelay, disableJitter: false, attempt: 1, max);
                Assert.True(d <= max, $"delay {d.TotalMilliseconds}ms exceeds the cap");
                Assert.True(d >= TimeSpan.Zero);
            }
        }

        [Fact]
        public void Shot_honours_a_lowered_max_delay()
        {
            // Fixed backoff would sleep 200 + 400 + 800 = 1400ms; a 20ms cap bounds it.
            var bounce = Bounce.Times(3)
                .WithDelay(TimeSpan.FromMilliseconds(100))
                .WithoutJitter()
                .WithMaxDelay(TimeSpan.FromMilliseconds(20));
            var attempts = 0;
            var sw = Stopwatch.StartNew();

            Assert.Throws<InvalidTimeZoneException>(() => Carom.Shot<int>(() =>
            {
                attempts++;
                throw new InvalidTimeZoneException("always fails");
            }, bounce));
            sw.Stop();

            Assert.Equal(4, attempts);
            Assert.True(sw.ElapsedMilliseconds < 1000, $"took {sw.ElapsedMilliseconds}ms, cap not applied");
        }

        [Fact]
        public async Task ShotAsync_honours_a_lowered_max_delay()
        {
            var bounce = Bounce.Times(3)
                .WithDelay(TimeSpan.FromMilliseconds(100))
                .WithoutJitter()
                .WithMaxDelay(TimeSpan.FromMilliseconds(20));
            var attempts = 0;
            var sw = Stopwatch.StartNew();

            await Assert.ThrowsAsync<InvalidTimeZoneException>(() => Carom.ShotAsync<int>(() =>
            {
                attempts++;
                throw new InvalidTimeZoneException("always fails");
            }, bounce));
            sw.Stop();

            Assert.Equal(4, attempts);
            Assert.True(sw.ElapsedMilliseconds < 1000, $"took {sw.ElapsedMilliseconds}ms, cap not applied");
        }

        [Fact]
        public void CalculateDelay_without_a_max_still_defaults_to_thirty_seconds()
        {
            Assert.Equal(TimeSpan.FromSeconds(30), JitterStrategy.DefaultMaxDelay);

            // 1s * 2^10 = 1024s, still capped at the 30s default
            var d = JitterStrategy.CalculateDelay(
                TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), disableJitter: true, attempt: 10);

            Assert.Equal(30, d.TotalSeconds);
        }
    }
}
