// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Carom;
using Carom.Extensions;
using Xunit;

namespace Carom.Extensions.Tests
{
    /// <summary>
    /// The Bounce overloads of the three extension classes used to unpack selected
    /// Bounce fields by hand, so MaxDelay never reached core and the sync timeout
    /// guard never ran. They must pass the whole Bounce through, so every field,
    /// present and future, flows without a per-field call site to forget.
    /// </summary>
    public class BouncePassthroughTests
    {
        // Fixed backoff for retries 3 is 200 + 400 + 800 = 1400ms; a 20ms cap
        // bounds the run to roughly 60ms of sleeping. The 1000ms ceiling separates
        // the two without flaking, same pattern as MaxDelayTests in core.
        private static Bounce CappedBounce() => Bounce.Times(3)
            .WithDelay(TimeSpan.FromMilliseconds(100))
            .WithoutJitter()
            .WithMaxDelay(TimeSpan.FromMilliseconds(20));

        private static Bounce TimeoutBounce() =>
            Bounce.Times(0).WithTimeout(TimeSpan.FromSeconds(1));

        private static readonly TimeSpan CapCeiling = TimeSpan.FromMilliseconds(1000);

        private static Cushion QuietCushion() =>
            Cushion.ForService("bounce-passthrough-" + Guid.NewGuid())
                .OpenAfter(failures: 10, trackingLast: 10)
                .HalfOpenAfter(TimeSpan.FromMinutes(10));

        private static Compartment RoomyCompartment() =>
            Compartment.ForResource("bounce-passthrough-" + Guid.NewGuid())
                .WithMaxConcurrency(2)
                .Build();

        private static Throttle GenerousThrottle() =>
            Throttle.ForService("bounce-passthrough-" + Guid.NewGuid())
                .WithRate(1000, TimeSpan.FromHours(1))
                .WithBurst(1000)
                .Build();

        private static int AlwaysFails(ref int attempts)
        {
            attempts++;
            throw new InvalidTimeZoneException("always fails");
        }

        [Fact]
        public void Cushion_sync_Bounce_overload_honours_MaxDelay()
        {
            var cushion = QuietCushion();
            var attempts = 0;
            var sw = Stopwatch.StartNew();

            Assert.Throws<InvalidTimeZoneException>(() =>
                CaromCushionExtensions.Shot<int>(() => AlwaysFails(ref attempts), cushion, CappedBounce()));
            sw.Stop();

            Assert.Equal(4, attempts);
            Assert.True(sw.Elapsed < CapCeiling,
                $"took {sw.ElapsedMilliseconds}ms; MaxDelay was dropped on the way to core");
        }

        [Fact]
        public void Compartment_sync_Bounce_overload_honours_MaxDelay()
        {
            var compartment = RoomyCompartment();
            var attempts = 0;
            var sw = Stopwatch.StartNew();

            Assert.Throws<InvalidTimeZoneException>(() =>
                CaromCompartmentExtensions.Shot<int>(() => AlwaysFails(ref attempts), compartment, CappedBounce()));
            sw.Stop();

            Assert.Equal(4, attempts);
            Assert.True(sw.Elapsed < CapCeiling,
                $"took {sw.ElapsedMilliseconds}ms; MaxDelay was dropped on the way to core");
        }

        [Fact]
        public void Throttle_sync_Bounce_overload_honours_MaxDelay()
        {
            var throttle = GenerousThrottle();
            var attempts = 0;
            var sw = Stopwatch.StartNew();

            Assert.Throws<InvalidTimeZoneException>(() =>
                CaromThrottleExtensions.Shot<int>(() => AlwaysFails(ref attempts), throttle, CappedBounce()));
            sw.Stop();

            Assert.Equal(4, attempts);
            Assert.True(sw.Elapsed < CapCeiling,
                $"took {sw.ElapsedMilliseconds}ms; MaxDelay was dropped on the way to core");
        }

        [Fact]
        public void Cushion_sync_Bounce_overload_rejects_a_timeout()
        {
            var cushion = QuietCushion();
            var ran = false;

            Assert.Throws<InvalidOperationException>(() =>
                CaromCushionExtensions.Shot(() => { ran = true; return 1; }, cushion, TimeoutBounce()));

            Assert.False(ran, "the action ran even though the sync path cannot enforce the timeout");
        }

        [Fact]
        public void Compartment_sync_Bounce_overload_rejects_a_timeout()
        {
            var compartment = RoomyCompartment();
            var ran = false;

            Assert.Throws<InvalidOperationException>(() =>
                CaromCompartmentExtensions.Shot(() => { ran = true; return 1; }, compartment, TimeoutBounce()));

            Assert.False(ran, "the action ran even though the sync path cannot enforce the timeout");
        }

        [Fact]
        public void Throttle_sync_Bounce_overload_rejects_a_timeout()
        {
            var throttle = GenerousThrottle();
            var ran = false;

            Assert.Throws<InvalidOperationException>(() =>
                CaromThrottleExtensions.Shot(() => { ran = true; return 1; }, throttle, TimeoutBounce()));

            Assert.False(ran, "the action ran even though the sync path cannot enforce the timeout");
        }

        [Fact]
        public async Task Cushion_async_Bounce_overload_still_enforces_the_timeout()
        {
            var cushion = QuietCushion();
            var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                await Assert.ThrowsAsync<TimeoutRejectedException>(() =>
                    CaromCushionExtensions.ShotAsync(() => gate.Task, cushion,
                        Bounce.Times(0).WithTimeout(TimeSpan.FromMilliseconds(50))));
            }
            finally
            {
                gate.TrySetResult(0);
            }
        }

        [Fact]
        public async Task Compartment_async_Bounce_overload_still_enforces_the_timeout()
        {
            var compartment = RoomyCompartment();
            var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                await Assert.ThrowsAsync<TimeoutRejectedException>(() =>
                    CaromCompartmentExtensions.ShotAsync(() => gate.Task, compartment,
                        Bounce.Times(0).WithTimeout(TimeSpan.FromMilliseconds(50))));
            }
            finally
            {
                gate.TrySetResult(0);
            }
        }

        [Fact]
        public async Task Throttle_async_Bounce_overload_still_enforces_the_timeout()
        {
            var throttle = GenerousThrottle();
            var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                await Assert.ThrowsAsync<TimeoutRejectedException>(() =>
                    CaromThrottleExtensions.ShotAsync(() => gate.Task, throttle,
                        Bounce.Times(0).WithTimeout(TimeSpan.FromMilliseconds(50))));
            }
            finally
            {
                gate.TrySetResult(0);
            }
        }
    }
}
