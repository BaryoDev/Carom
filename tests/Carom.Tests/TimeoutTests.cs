using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Carom.Tests
{
    public class TimeoutTests
    {
        [Fact]
        public void Bounce_WithTimeout_SetsProperty()
        {
            var bounce = Bounce.Times(0).WithTimeout(TimeSpan.FromMilliseconds(100));

            Assert.Equal(TimeSpan.FromMilliseconds(100), bounce.Timeout);
        }

        [Fact]
        public async Task ShotAsync_WithoutTimeout_DoesNotAllocateExtra()
        {
            // This test verifies behavior - allocation testing is in benchmarks
            var result = await Carom.ShotAsync(
                async () =>
                {
                    await Task.Delay(1);
                    return 42;
                },
                retries: 0);

            Assert.Equal(42, result);
        }

        [Fact]
        public async Task ShotAsync_WithTimeout_SucceedsIfCompletesInTime()
        {
            var result = await Carom.ShotAsync(
                async () =>
                {
                    await Task.Delay(10);
                    return 42;
                },
                retries: 0,
                timeout: TimeSpan.FromSeconds(5));

            Assert.Equal(42, result);
        }

        [Fact]
        public void Bounce_WithTimeout_SetsTimeoutProperty()
        {
            var bounce = Bounce.Times(3).WithTimeout(TimeSpan.FromSeconds(10));

            Assert.Equal(TimeSpan.FromSeconds(10), bounce.Timeout);
            Assert.Equal(3, bounce.Retries);
        }

        [Fact]
        public void Bounce_WithTimeout_ChainsMethods()
        {
            var bounce = Bounce.Times(5)
                .WithDelay(TimeSpan.FromMilliseconds(200))
                .WithTimeout(TimeSpan.FromSeconds(30));

            Assert.Equal(5, bounce.Retries);
            Assert.Equal(TimeSpan.FromMilliseconds(200), bounce.BaseDelay);
            Assert.Equal(TimeSpan.FromSeconds(30), bounce.Timeout);
        }

        [Fact]
        public async Task ShotAsync_VoidOverload_WorksWithTimeout()
        {
            var executed = false;

            await Carom.ShotAsync(
                async () =>
                {
                    await Task.Delay(10);
                    executed = true;
                },
                retries: 0,
                timeout: TimeSpan.FromSeconds(5));

            Assert.True(executed);
        }

        [Fact]
        public void TimeoutRejectedException_ContainsTimeout()
        {
            var timeout = TimeSpan.FromSeconds(5);
            var ex = new TimeoutRejectedException(timeout);

            Assert.Equal(timeout, ex.Timeout);
            Assert.Contains("5000ms", ex.Message);
        }

        [Fact]
        public void TimeoutRejectedException_WithInnerException_PreservesInner()
        {
            var inner = new InvalidOperationException("Original");
            var timeout = TimeSpan.FromSeconds(3);
            var ex = new TimeoutRejectedException(timeout, inner);

            Assert.Equal(timeout, ex.Timeout);
            Assert.Same(inner, ex.InnerException);
        }

        [Fact]
        public void Bounce_DefaultTimeout_IsNull()
        {
            var bounce = Bounce.Times(3);
            Assert.Null(bounce.Timeout);
        }

        [Fact]
        public async Task ShotAsync_TimeoutDuringBackoffDelay_ThrowsTimeoutRejectedException()
        {
            // The action fails instantly, so the 200ms timeout fires during the
            // multi-second retry backoff. The backoff wait must apply the same
            // timeout translation as the operation itself, not leak a raw
            // TaskCanceledException.
            await Assert.ThrowsAsync<TimeoutRejectedException>(() =>
                Carom.ShotAsync<int>(
                    () => throw new InvalidOperationException("boom"),
                    retries: 3,
                    baseDelay: TimeSpan.FromSeconds(5),
                    timeout: TimeSpan.FromMilliseconds(200),
                    shouldBounce: _ => true,
                    disableJitter: true));
        }

        [Fact]
        public async Task ShotAsync_UserCancellationDuringBackoffDelay_ThrowsOperationCanceled()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                Carom.ShotAsync<int>(
                    () => throw new InvalidOperationException("boom"),
                    retries: 3,
                    baseDelay: TimeSpan.FromSeconds(5),
                    timeout: null,
                    shouldBounce: _ => true,
                    disableJitter: true,
                    ct: cts.Token));
        }

        [Fact]
        public void Bounce_WithTimeout_RejectsZero()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                Bounce.Times(3).WithTimeout(TimeSpan.Zero));
        }

        [Fact]
        public void Bounce_WithTimeout_RejectsNegative()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                Bounce.Times(3).WithTimeout(TimeSpan.FromMilliseconds(-1)));
        }

        [Fact]
        public void BounceOfT_WithTimeout_RejectsZero()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                Bounce<int>.Times(3).WithTimeout(TimeSpan.Zero));
        }

        [Fact]
        public void BounceOfT_WithTimeout_RejectsNegative()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                Bounce<int>.Times(3).WithTimeout(TimeSpan.FromMilliseconds(-1)));
        }

        [Fact]
        public async Task ShotAsync_ZeroTimeout_IsRejectedEveryRun()
        {
            // A zero timeout used to race the callee and win about one run in nine.
            // A single call would pass most of the time, so this loops.
            for (int i = 0; i < 200; i++)
            {
                await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                    Carom.ShotAsync(
                        () => Task.FromResult(42),
                        retries: 0,
                        timeout: TimeSpan.Zero));
            }
        }

        [Fact]
        public async Task ShotAsync_VoidOverload_ZeroTimeout_IsRejectedEveryRun()
        {
            for (int i = 0; i < 200; i++)
            {
                await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                    Carom.ShotAsync(
                        () => Task.CompletedTask,
                        retries: 0,
                        timeout: TimeSpan.Zero));
            }
        }
    }
}
