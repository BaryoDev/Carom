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
    }
}
