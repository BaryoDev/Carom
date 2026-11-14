using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Carom.Tests
{
    /// <summary>
    /// Bounce.WithDelayHint: a server's Retry-After replaces the computed jitter, is clamped to
    /// MaxDelay, and is still bounded by the overall timeout and by cancellation. The waits go
    /// through a fake clock that records each requested delay and returns at once.
    /// </summary>
    public class DelayHintTests
    {
        private sealed class RetryAfterException : Exception
        {
            public TimeSpan After { get; }
            public RetryAfterException(TimeSpan after) { After = after; }
        }

        private sealed class FakeClock
        {
            public List<TimeSpan> Waits { get; } = new List<TimeSpan>();

            public Task Sleep(TimeSpan delay, CancellationToken ct)
            {
                Waits.Add(delay);
                return Task.CompletedTask;
            }
        }

        private static Func<Task<int>> FailOnceThenSucceed(Func<Exception> failure)
        {
            int calls = 0;
            return () => ++calls == 1 ? throw failure() : Task.FromResult(7);
        }

        [Fact]
        public async Task AHintOfThreeSeconds_IsWaited_InsteadOfJitter()
        {
            var clock = new FakeClock();
            var bounce = Bounce.Times(2)
                .WithDelayHint(ex => ex is RetryAfterException r ? r.After : (TimeSpan?)null)
                .WithSleep(clock.Sleep);

            var result = await Carom.ShotAsync(
                FailOnceThenSucceed(() => new RetryAfterException(TimeSpan.FromSeconds(3))), bounce);

            Assert.Equal(7, result);
            Assert.Equal(new[] { TimeSpan.FromSeconds(3) }, clock.Waits);
        }

        [Fact]
        public async Task AHintPastMaxDelay_IsClampedToMaxDelay()
        {
            var clock = new FakeClock();
            var bounce = Bounce.Times(2)
                .WithMaxDelay(TimeSpan.FromSeconds(2))
                .WithDelayHint(_ => TimeSpan.FromMinutes(10))
                .WithSleep(clock.Sleep);

            await Carom.ShotAsync(FailOnceThenSucceed(() => new RetryAfterException(TimeSpan.Zero)), bounce);

            Assert.Equal(new[] { TimeSpan.FromSeconds(2) }, clock.Waits);
        }

        [Fact]
        public async Task AHintPastTheDefaultMaxDelay_IsClampedToThirtySeconds()
        {
            var clock = new FakeClock();
            var bounce = Bounce.Times(2)
                .WithDelayHint(_ => TimeSpan.FromHours(1))
                .WithSleep(clock.Sleep);

            await Carom.ShotAsync(FailOnceThenSucceed(() => new RetryAfterException(TimeSpan.Zero)), bounce);

            Assert.Equal(new[] { TimeSpan.FromSeconds(30) }, clock.Waits);
        }

        [Fact]
        public async Task ANegativeHint_WaitsZero()
        {
            var clock = new FakeClock();
            var bounce = Bounce.Times(2).WithDelayHint(_ => TimeSpan.FromSeconds(-5)).WithSleep(clock.Sleep);

            await Carom.ShotAsync(FailOnceThenSucceed(() => new RetryAfterException(TimeSpan.Zero)), bounce);

            Assert.Equal(new[] { TimeSpan.Zero }, clock.Waits);
        }

        [Fact]
        public async Task ANullHint_FallsBackToTheComputedBackoff()
        {
            var clock = new FakeClock();
            var bounce = Bounce.Times(2)
                .WithDelay(TimeSpan.FromMilliseconds(10))
                .WithoutJitter()
                .WithDelayHint(_ => null)
                .WithSleep(clock.Sleep);

            await Carom.ShotAsync(FailOnceThenSucceed(() => new RetryAfterException(TimeSpan.Zero)), bounce);

            // Jitter off: base * 2^attempt for the first retry.
            Assert.Equal(new[] { TimeSpan.FromMilliseconds(20) }, clock.Waits);
        }

        [Fact]
        public async Task AThrowingHint_FallsBackToTheComputedBackoff()
        {
            var clock = new FakeClock();
            var bounce = Bounce.Times(2)
                .WithDelay(TimeSpan.FromMilliseconds(10))
                .WithoutJitter()
                .WithDelayHint(_ => throw new InvalidOperationException("broken hint"))
                .WithSleep(clock.Sleep);

            var result = await Carom.ShotAsync(
                FailOnceThenSucceed(() => new RetryAfterException(TimeSpan.Zero)), bounce);

            Assert.Equal(7, result);
            Assert.Equal(new[] { TimeSpan.FromMilliseconds(20) }, clock.Waits);
        }

        [Fact]
        public async Task AHint_DoesNotFeedTheNextJitterRange()
        {
            var clock = new FakeClock();
            int calls = 0;
            var bounce = Bounce.Times(3)
                .WithDelay(TimeSpan.FromMilliseconds(10))
                .WithoutJitter()
                .WithDelayHint(ex => ex is RetryAfterException ? TimeSpan.FromSeconds(3) : (TimeSpan?)null)
                .WithSleep(clock.Sleep);

            await Carom.ShotAsync<int>(() =>
            {
                calls++;
                if (calls == 1) throw new RetryAfterException(TimeSpan.Zero);
                if (calls == 2) throw new InvalidOperationException("no hint");
                return Task.FromResult(7);
            }, bounce);

            Assert.Equal(2, clock.Waits.Count);
            Assert.Equal(TimeSpan.FromSeconds(3), clock.Waits[0]);
            Assert.Equal(TimeSpan.FromMilliseconds(40), clock.Waits[1]);
        }

        [Fact]
        public async Task TypedBounce_AlsoHonoursTheHint()
        {
            var clock = new FakeClock();
            var bounce = Bounce.For<int>(2).WithDelayHint(_ => TimeSpan.FromSeconds(3)).WithSleep(clock.Sleep);

            await Carom.ShotAsync(FailOnceThenSucceed(() => new RetryAfterException(TimeSpan.Zero)), bounce);

            Assert.Equal(new[] { TimeSpan.FromSeconds(3) }, clock.Waits);
        }

        [Fact]
        public async Task VoidAction_HonoursTheHint()
        {
            var clock = new FakeClock();
            var bounce = Bounce.Times(2).WithDelayHint(_ => TimeSpan.FromSeconds(3)).WithSleep(clock.Sleep);
            int calls = 0;

            await Carom.ShotAsync(() =>
            {
                if (++calls == 1) throw new RetryAfterException(TimeSpan.Zero);
                return Task.CompletedTask;
            }, bounce);

            Assert.Equal(new[] { TimeSpan.FromSeconds(3) }, clock.Waits);
        }

        [Fact]
        public async Task TheOverallTimeout_CutsAHintShort()
        {
            // Real clock on purpose: the wait must be cancelled by the overall timeout.
            var overall = TimeSpan.FromMilliseconds(150);
            var bounce = Bounce.Times(2)
                .WithTimeout(overall)
                .WithDelayHint(_ => TimeSpan.FromSeconds(20));
            var watch = Stopwatch.StartNew();

            var ex = await Assert.ThrowsAsync<TimeoutRejectedException>(() =>
                Carom.ShotAsync(FailOnceThenSucceed(() => new RetryAfterException(TimeSpan.Zero)), bounce));

            Assert.Equal(overall, ex.Timeout);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), "the hint was waited in full");
        }

        [Fact]
        public async Task Cancellation_CutsAHintShort()
        {
            var bounce = Bounce.Times(2).WithDelayHint(_ => TimeSpan.FromSeconds(20));
            using var cts = new CancellationTokenSource();
            var watch = Stopwatch.StartNew();

            var task = Carom.ShotAsync(FailOnceThenSucceed(() => new RetryAfterException(TimeSpan.Zero)), bounce, cts.Token);
            cts.CancelAfter(100);

            var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);

            Assert.IsNotType<TimeoutRejectedException>(ex);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), "the hint was waited in full");
        }

        [Fact]
        public void WithDelayHint_RejectsNull_AndSurvivesTheOtherWithMethods()
        {
            Assert.Throws<ArgumentNullException>(() => Bounce.Times(1).WithDelayHint(null!));

            Func<Exception, TimeSpan?> hint = _ => null;
            var bounce = Bounce.Times(1).WithDelayHint(hint).WithDelay(TimeSpan.Zero).WithTimeout(TimeSpan.FromSeconds(1)).WithoutJitter();

            Assert.Same(hint, bounce._o!.Hint);
        }

        [Fact]
        public void SyncShot_HonoursTheHint()
        {
            var bounce = Bounce.Times(2).WithDelay(TimeSpan.FromSeconds(2)).WithDelayHint(_ => TimeSpan.FromMilliseconds(5));
            int calls = 0;
            var watch = Stopwatch.StartNew();

            var result = Carom.Shot(() => ++calls == 1 ? throw new RetryAfterException(TimeSpan.Zero) : 7, bounce);

            // The computed delay would be at least the 2 s base; the hint is 5 ms.
            Assert.Equal(7, result);
            Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(1000), $"waited {watch.Elapsed}");
        }

        [Fact]
        public async Task AHint_DoesNotInflateTheNextJitterRange_Async()
        {
            // Jitter on: the next delay is rand(base, previous * 3). If a 3 s hint became the
            // previous delay, the next wait would be up to 9 s instead of at most 3 * base.
            var clock = new FakeClock();
            int calls = 0;
            var bounce = Bounce.Times(3)
                .WithDelay(TimeSpan.FromMilliseconds(10))
                .WithDelayHint(ex => ex is RetryAfterException ? TimeSpan.FromSeconds(3) : (TimeSpan?)null)
                .WithSleep(clock.Sleep);

            await Carom.ShotAsync<int>(() =>
            {
                calls++;
                if (calls == 1) throw new RetryAfterException(TimeSpan.Zero);
                if (calls == 2) throw new InvalidOperationException("no hint");
                return Task.FromResult(7);
            }, bounce);

            Assert.Equal(TimeSpan.FromSeconds(3), clock.Waits[0]);
            Assert.InRange(clock.Waits[1], TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(30));
        }

        [Fact]
        public async Task AHint_DoesNotInflateTheNextJitterRange_VoidAsync()
        {
            var clock = new FakeClock();
            int calls = 0;
            var bounce = Bounce.Times(3)
                .WithDelay(TimeSpan.FromMilliseconds(10))
                .WithDelayHint(ex => ex is RetryAfterException ? TimeSpan.FromSeconds(3) : (TimeSpan?)null)
                .WithSleep(clock.Sleep);

            await Carom.ShotAsync(() =>
            {
                calls++;
                if (calls == 1) throw new RetryAfterException(TimeSpan.Zero);
                if (calls == 2) throw new InvalidOperationException("no hint");
                return Task.CompletedTask;
            }, bounce);

            Assert.Equal(TimeSpan.FromSeconds(3), clock.Waits[0]);
            Assert.InRange(clock.Waits[1], TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(30));
        }

        [Fact]
        public void SyncShotAction_HonoursTheHint()
        {
            var bounce = Bounce.Times(2).WithDelay(TimeSpan.FromSeconds(2)).WithDelayHint(_ => TimeSpan.FromMilliseconds(5));
            int calls = 0;
            var watch = Stopwatch.StartNew();

            Carom.Shot(() => { if (++calls == 1) throw new RetryAfterException(TimeSpan.Zero); }, bounce);

            // Without the hint the wait is at least the 2 s base delay.
            Assert.Equal(2, calls);
            Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(1000), $"waited {watch.Elapsed}");
        }

        [Fact]
        public void SyncShotTypedBounce_HonoursTheHint()
        {
            var bounce = Bounce.For<int>(2).WithDelay(TimeSpan.FromSeconds(2)).WithDelayHint(_ => TimeSpan.FromMilliseconds(5));
            int calls = 0;
            var watch = Stopwatch.StartNew();

            var result = Carom.Shot(() => ++calls == 1 ? throw new RetryAfterException(TimeSpan.Zero) : 7, bounce);

            Assert.Equal(7, result);
            Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(1000), $"waited {watch.Elapsed}");
        }

        [Fact]
        public async Task ResultRetries_IgnoreTheHint()
        {
            var clock = new FakeClock();
            int calls = 0;
            var hinted = false;
            var bounce = Bounce.For<int>(2)
                .WithDelay(TimeSpan.FromMilliseconds(10))
                .WithoutJitter()
                .WhenResult(r => r < 2)
                .WithDelayHint(_ => { hinted = true; return TimeSpan.FromSeconds(3); })
                .WithSleep(clock.Sleep);

            var result = await Carom.ShotAsync(() => Task.FromResult(++calls), bounce);

            Assert.Equal(2, result);
            Assert.False(hinted);
        }
    }
}
