// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Carom;
using Carom.Extensions;
using Xunit;

namespace Carom.Extensions.Tests
{
    /// <summary>
    /// Issue #2: the Bounce overloads must apply the same fail-fast default as the
    /// retries: overloads. A CircuitOpenException, ThrottledException or
    /// CompartmentFullException is the pattern saying "stop", and retrying through
    /// it with full backoff turns a fail-fast primitive into added latency.
    /// </summary>
    public class BounceOverloadTests
    {
        // With the default applied, a rejection propagates on the first attempt and no
        // backoff runs. Without it, WithoutJitter gives deterministic delays of
        // base*2 + base*4 = 1.8s for base=300ms, so the elapsed assertion separates
        // the two behaviours without depending on jitter randomness.
        private static readonly TimeSpan BaseDelay = TimeSpan.FromMilliseconds(300);
        private static readonly TimeSpan FastFailCeiling = TimeSpan.FromMilliseconds(1000);

        private static Cushion OpenCushion(out string key)
        {
            key = "bounce-overload-" + Guid.NewGuid();
            var cushion = Cushion.ForService(key)
                .OpenAfter(failures: 1, within: 1)
                .HalfOpenAfter(TimeSpan.FromMinutes(10)); // cannot drift to half-open mid-test

            Assert.Throws<InvalidOperationException>(() =>
                CaromCushionExtensions.Shot<int>(
                    () => throw new InvalidOperationException("boom"), cushion, retries: 0));
            Assert.Equal(CircuitState.Open, Cushion.GetState(key));
            return cushion;
        }

        private static Bounce SlowBounce() =>
            Bounce.Times(2).WithDelay(BaseDelay).WithoutJitter();

        [Fact]
        public void Sync_Bounce_overload_fails_fast_on_an_open_circuit()
        {
            var cushion = OpenCushion(out _);

            var sw = Stopwatch.StartNew();
            Assert.Throws<CircuitOpenException>(() =>
                CaromCushionExtensions.Shot(() => 1, cushion, SlowBounce()));
            sw.Stop();

            Assert.True(sw.Elapsed < FastFailCeiling,
                $"expected fail-fast, spent {sw.ElapsedMilliseconds}ms backing off against an open circuit");
        }

        [Fact]
        public async Task Async_Bounce_overload_fails_fast_on_an_open_circuit()
        {
            var cushion = OpenCushion(out _);

            var sw = Stopwatch.StartNew();
            await Assert.ThrowsAsync<CircuitOpenException>(() =>
                CaromCushionExtensions.ShotAsync(() => Task.FromResult(1), cushion, SlowBounce()));
            sw.Stop();

            Assert.True(sw.Elapsed < FastFailCeiling,
                $"expected fail-fast, spent {sw.ElapsedMilliseconds}ms backing off against an open circuit");
        }

        [Fact]
        public void Sync_Bounce_overload_fails_fast_on_an_exhausted_throttle()
        {
            var throttle = Throttle.ForService("bounce-overload-" + Guid.NewGuid())
                .WithRate(1, TimeSpan.FromHours(1))
                .WithBurst(1)
                .Build();

            Assert.Equal(1, CaromThrottleExtensions.Shot(() => 1, throttle, retries: 0)); // drain the bucket

            var sw = Stopwatch.StartNew();
            Assert.Throws<ThrottledException>(() =>
                CaromThrottleExtensions.Shot(() => 2, throttle, SlowBounce()));
            sw.Stop();

            Assert.True(sw.Elapsed < FastFailCeiling,
                $"expected fail-fast, spent {sw.ElapsedMilliseconds}ms backing off against an exhausted throttle");
        }

        [Fact]
        public void Sync_Bounce_overload_fails_fast_on_a_full_compartment()
        {
            var compartment = Compartment.ForResource("bounce-overload-" + Guid.NewGuid())
                .WithMaxConcurrency(1)
                .Build();

            using var holderIn = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var holder = Task.Run(() => CaromCompartmentExtensions.Shot(() =>
            {
                holderIn.Set();
                release.Wait();
                return 0;
            }, compartment, retries: 0));
            holderIn.Wait();

            try
            {
                var sw = Stopwatch.StartNew();
                Assert.Throws<CompartmentFullException>(() =>
                    CaromCompartmentExtensions.Shot(() => 1, compartment, SlowBounce()));
                sw.Stop();

                Assert.True(sw.Elapsed < FastFailCeiling,
                    $"expected fail-fast, spent {sw.ElapsedMilliseconds}ms backing off against a full compartment");
            }
            finally
            {
                release.Set();
                holder.Wait();
            }
        }

        [Fact]
        public void A_caller_supplied_predicate_still_wins_over_the_default()
        {
            var cushion = OpenCushion(out _);

            // The caller explicitly asks to retry through the open circuit. One retry
            // without jitter waits base*2 = 600ms, which fail-fast would skip.
            var bounce = Bounce.On<CircuitOpenException>(retries: 1)
                .WithDelay(BaseDelay).WithoutJitter();

            var sw = Stopwatch.StartNew();
            Assert.Throws<CircuitOpenException>(() =>
                CaromCushionExtensions.Shot(() => 1, cushion, bounce));
            sw.Stop();

            Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(500),
                $"caller's predicate was ignored: no backoff ran ({sw.ElapsedMilliseconds}ms)");
        }

        [Theory]
        [InlineData(typeof(CircuitOpenException))]
        [InlineData(typeof(ThrottledException))]
        [InlineData(typeof(CompartmentFullException))]
        public void Each_default_predicate_refuses_its_own_rejection_type(Type rejection)
        {
            // The three defaults are distinct predicates; each must refuse exactly its
            // own pattern's rejection and retry anything else.
            Assert.False(Dispatch(rejection, Rejection(rejection)));
            Assert.True(Dispatch(rejection, new InvalidOperationException()));
        }

        private static bool Dispatch(Type rejection, Exception ex) =>
            rejection == typeof(CircuitOpenException) ? CaromCushionExtensions.DefaultShouldBounce(ex)
            : rejection == typeof(ThrottledException) ? CaromThrottleExtensions.DefaultShouldBounce(ex)
            : CaromCompartmentExtensions.DefaultShouldBounce(ex);

        private static Exception Rejection(Type rejection) =>
            rejection == typeof(CircuitOpenException) ? new CircuitOpenException("k")
            : rejection == typeof(ThrottledException) ? new ThrottledException("k", 1, TimeSpan.FromSeconds(1))
            : (Exception)new CompartmentFullException("k", 1);
    }
}
