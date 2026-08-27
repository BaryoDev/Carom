// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Xunit;

namespace Carom.Tests
{
    /// <summary>
    /// Issue #9: JitterStrategy computes every retry delay in the library and had no
    /// tests, because Carom.csproj granted no InternalsVisibleTo. It is a pure
    /// function; these pin the behaviours nothing pinned before.
    /// </summary>
    public class JitterStrategyTests
    {
        private static readonly TimeSpan Cap = TimeSpan.FromMilliseconds(JitterStrategy.MaxDelayMilliseconds);

        [Fact]
        public void Jittered_delay_stays_between_base_and_three_times_previous()
        {
            var baseDelay = TimeSpan.FromMilliseconds(100);
            var previous = TimeSpan.FromMilliseconds(400);

            for (int i = 0; i < 1000; i++)
            {
                var d = JitterStrategy.CalculateDelay(baseDelay, previous, disableJitter: false, attempt: 1);
                Assert.InRange(d.TotalMilliseconds, 100, 1200);
            }
        }

        [Fact]
        public void Jittered_delay_never_exceeds_the_cap()
        {
            var baseDelay = TimeSpan.FromSeconds(1);
            var previous = TimeSpan.FromSeconds(29); // previous * 3 = 87s, far past the cap

            for (int i = 0; i < 1000; i++)
            {
                var d = JitterStrategy.CalculateDelay(baseDelay, previous, disableJitter: false, attempt: 5);
                Assert.True(d <= Cap, $"delay {d.TotalMilliseconds}ms exceeds the {Cap.TotalSeconds}s cap");
            }
        }

        [Fact]
        public void A_base_delay_above_the_cap_cannot_invert_the_range()
        {
            // The floor is clamped too. Without that, baseDelay 60s with a small
            // previousDelay inverts min and max, and min + rand * (negative) walks
            // below the floor while the intended fix point is the cap itself.
            var baseDelay = TimeSpan.FromSeconds(60);
            var previous = TimeSpan.FromMilliseconds(100);

            for (int i = 0; i < 1000; i++)
            {
                var d = JitterStrategy.CalculateDelay(baseDelay, previous, disableJitter: false, attempt: 1);
                Assert.True(d <= Cap, $"delay {d.TotalMilliseconds}ms exceeds the cap");
                // min is clamped to the cap and max collapses onto it
                Assert.Equal(Cap.TotalMilliseconds, d.TotalMilliseconds, precision: 3);
            }
        }

        [Fact]
        public void Small_previous_delay_falls_back_to_three_times_base()
        {
            // previous * 3 below base triggers the maxMs = minMs * 3 fallback,
            // and that fallback is then still capped.
            var baseDelay = TimeSpan.FromMilliseconds(500);
            var previous = TimeSpan.FromMilliseconds(50); // 150 < 500

            for (int i = 0; i < 1000; i++)
            {
                var d = JitterStrategy.CalculateDelay(baseDelay, previous, disableJitter: false, attempt: 1);
                Assert.InRange(d.TotalMilliseconds, 500, 1500);
            }
        }

        [Fact]
        public void Disabled_jitter_doubles_from_the_first_retry()
        {
            // attempt is 1-indexed, so the first retry waits base * 2, not base.
            // Documented here because it is observable behaviour, intentional or not.
            var baseDelay = TimeSpan.FromMilliseconds(100);

            Assert.Equal(200, JitterStrategy.CalculateDelay(baseDelay, baseDelay, true, attempt: 1).TotalMilliseconds);
            Assert.Equal(400, JitterStrategy.CalculateDelay(baseDelay, baseDelay, true, attempt: 2).TotalMilliseconds);
            Assert.Equal(800, JitterStrategy.CalculateDelay(baseDelay, baseDelay, true, attempt: 3).TotalMilliseconds);
        }

        [Fact]
        public void Disabled_jitter_is_capped()
        {
            var baseDelay = TimeSpan.FromSeconds(10);

            // 10s * 2^4 = 160s without the cap
            var d = JitterStrategy.CalculateDelay(baseDelay, baseDelay, disableJitter: true, attempt: 4);
            Assert.Equal(Cap, d);
        }

        [Fact]
        public void Two_threads_produce_different_jitter_sequences()
        {
            // Random is [ThreadStatic] and seeded per thread precisely so that
            // concurrent callers decorrelate; identical sequences would recreate
            // the synchronized retry storm jitter exists to prevent.
            var baseDelay = TimeSpan.FromMilliseconds(100);
            var previous = TimeSpan.FromMilliseconds(400);

            List<double> Sample()
            {
                var values = new List<double>();
                var t = new Thread(() =>
                {
                    for (int i = 0; i < 20; i++)
                    {
                        values.Add(JitterStrategy.CalculateDelay(baseDelay, previous, false, 1).TotalMilliseconds);
                    }
                });
                t.Start();
                t.Join();
                return values;
            }

            var a = Sample();
            var b = Sample();

            Assert.False(a.SequenceEqual(b), "two threads produced identical jitter sequences");
        }
    }
}
