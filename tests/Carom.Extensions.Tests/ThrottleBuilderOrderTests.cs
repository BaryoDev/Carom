using System;
using Xunit;
using Carom.Extensions;

namespace Carom.Extensions.Tests
{
    /// <summary>
    /// A fluent builder has to be order independent. Reordering calls that set different things
    /// must not change the result, because nothing in the API tells a caller there is an order.
    /// </summary>
    /// <remarks>
    /// <c>WithRate</c> used to assign <c>_burstSize = maxRequests</c>, so
    /// <c>WithBurst(20).WithRate(100, ...)</c> silently discarded the 20 while the same two calls
    /// the other way round worked. Silent is the problem: the throttle ran at a burst nobody asked
    /// for and nothing reported it.
    /// </remarks>
    public class ThrottleBuilderOrderTests
    {
        private static string Key() => "order-test-" + Guid.NewGuid();

        [Fact]
        public void Burst_survives_a_later_call_to_WithRate()
        {
            var throttle = Throttle.ForService(Key())
                .WithBurst(20)
                .WithRate(100, TimeSpan.FromSeconds(1))
                .Build();

            Assert.Equal(20, throttle.BurstSize);
        }

        [Fact]
        public void The_two_orderings_produce_the_same_configuration()
        {
            var burstFirst = Throttle.ForService(Key())
                .WithBurst(20)
                .WithRate(100, TimeSpan.FromSeconds(1))
                .Build();

            var rateFirst = Throttle.ForService(Key())
                .WithRate(100, TimeSpan.FromSeconds(1))
                .WithBurst(20)
                .Build();

            Assert.Equal(rateFirst.MaxRequests, burstFirst.MaxRequests);
            Assert.Equal(rateFirst.TimeWindow, burstFirst.TimeWindow);
            Assert.Equal(rateFirst.BurstSize, burstFirst.BurstSize);
        }

        /// <summary>
        /// The example in README.md, verbatim, minus the call it wraps.
        /// </summary>
        /// <remarks>
        /// This is the bug that started issue #7: the documented example threw, because burst was
        /// required to be at least maxRequests and the example asks for 20 against a rate of 100.
        /// A README that throws is only discovered by someone new, which is the worst audience to
        /// discover it.
        ///
        /// Kept as its own test rather than folded into the ordering ones so that changing the
        /// documented example forces a decision here rather than quietly diverging from it.
        /// </remarks>
        [Fact]
        public void The_documented_example_builds()
        {
            var apiThrottle = Throttle.ForService("external-api-" + Guid.NewGuid())
                .WithRate(100, TimeSpan.FromSeconds(1))
                .WithBurst(20)
                .Build();

            Assert.Equal(100, apiThrottle.MaxRequests);
            Assert.Equal(20, apiThrottle.BurstSize);
        }

        [Fact]
        public void Burst_defaults_to_the_rate_when_it_is_not_set()
        {
            var throttle = Throttle.ForService(Key())
                .WithRate(75, TimeSpan.FromSeconds(1))
                .Build();

            Assert.Equal(75, throttle.BurstSize);
        }

        /// <summary>
        /// The floor moved from maxRequests to 1; it did not disappear.
        /// </summary>
        [Fact]
        public void A_burst_below_one_is_still_rejected()
        {
            Assert.Throws<ArgumentException>(() =>
                Throttle.ForService(Key())
                    .WithRate(100, TimeSpan.FromSeconds(1))
                    .WithBurst(0)
                    .Build());
        }
    }
}
