using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Carom.Extensions;

namespace Carom.Extensions.Tests
{
    public class ThrottleTests
    {
        public ThrottleTests()
        {
            ThrottleStore.Clear();
        }

        #region Configuration Tests

        [Fact]
        public void ForService_CreatesBuilder_WithValidServiceKey()
        {
            var builder = Throttle.ForService("test-service");
            Assert.NotNull(builder);
        }

        [Fact]
        public void Build_ThrowsArgumentException_WhenServiceKeyIsEmpty()
        {
            Assert.Throws<ArgumentException>(() =>
                Throttle.ForService("")
                    .WithRate(100, TimeSpan.FromSeconds(1))
                    .Build());
        }

        [Fact]
        public void Build_ThrowsArgumentException_WhenMaxRequestsIsZero()
        {
            Assert.Throws<ArgumentException>(() =>
                Throttle.ForService("test")
                    .WithRate(0, TimeSpan.FromSeconds(1))
                    .Build());
        }

        [Fact]
        public void Build_ThrowsArgumentException_WhenTimeWindowIsZero()
        {
            Assert.Throws<ArgumentException>(() =>
                Throttle.ForService("test")
                    .WithRate(100, TimeSpan.Zero)
                    .Build());
        }

        #endregion

        #region Rate Limiting Tests

        [Fact]
        public async Task ExecuteAsync_AllowsRequests_WithinRateLimit()
        {
            var throttle = Throttle.ForService("within-limit-test")
                .WithRate(10, TimeSpan.FromSeconds(1))
                .Build();

            // Should allow 10 requests
            for (int i = 0; i < 10; i++)
            {
                var result = await CaromThrottleExtensions.ShotAsync(
                    async () =>
                    {
                        await Task.Delay(1);
                        return i;
                    },
                    throttle,
                    retries: 0);

                Assert.Equal(i, result);
            }
        }

        [Fact]
        public void Execute_ThrowsThrottledException_WhenRateLimitExceeded()
        {
            var throttle = Throttle.ForService("exceeded-test")
                .WithRate(2, TimeSpan.FromSeconds(10))
                .WithBurst(2)  // Limit burst to match rate
                .Build();

            // First 2 should succeed (using initial burst)
            CaromThrottleExtensions.Shot(() => 1, throttle, retries: 0);
            CaromThrottleExtensions.Shot(() => 2, throttle, retries: 0);

            // Third should fail (no tokens left)
            var ex = Assert.Throws<ThrottledException>(() =>
            {
                CaromThrottleExtensions.Shot(() => 3, throttle, retries: 0);
            });

            Assert.Equal("exceeded-test", ex.ServiceKey);
            Assert.Equal(2, ex.MaxRequests);
        }

        [Fact]
        public async Task ExecuteAsync_EnforcesRateLimit_Eventually()
        {
            var throttle = Throttle.ForService("limit-test")
                .WithRate(3, TimeSpan.FromSeconds(1))
                .WithBurst(3)
                .Build();

            // Consume all burst tokens
            for (int i = 0; i < 3; i++)
            {
                await CaromThrottleExtensions.ShotAsync(
                    async () =>
                    {
                        await Task.Delay(1);
                        return i;
                    },
                    throttle,
                    retries: 0);
            }

            // Next request should eventually be rejected (tokens exhausted)
            // Note: Due to refill timing, we may need multiple attempts
            var rejected = false;
            for (int attempt = 0; attempt < 5 && !rejected; attempt++)
            {
                try
                {
                    await CaromThrottleExtensions.ShotAsync(
                        () => Task.FromResult(99),
                        throttle,
                        retries: 0);
                }
                catch (ThrottledException)
                {
                    rejected = true;
                }
            }

            Assert.True(rejected, "Rate limit should eventually reject requests");
        }

        #endregion

        #region Integration Tests

        [Fact]
        public async Task Shot_WithBounce_WorksWithThrottle()
        {
            var throttle = Throttle.ForService("bounce-test")
                .WithRate(100, TimeSpan.FromSeconds(1))
                .Build();

            var bounce = Bounce.Times(2)
                .WithDelay(TimeSpan.FromMilliseconds(10));

            var result = await CaromThrottleExtensions.ShotAsync(
                async () =>
                {
                    await Task.Delay(10);
                    return "success";
                },
                throttle,
                bounce);

            Assert.Equal("success", result);
        }

        #endregion

        #region ThrottledException Tests

        [Fact]
        public void ThrottledException_ContainsServiceKey()
        {
            var exception = new ThrottledException("my-service", 100, TimeSpan.FromSeconds(1));

            Assert.Equal("my-service", exception.ServiceKey);
            Assert.Equal(100, exception.MaxRequests);
            Assert.Equal(TimeSpan.FromSeconds(1), exception.TimeWindow);
            Assert.Contains("my-service", exception.Message);
            Assert.Contains("100", exception.Message);
        }

        [Fact]
        public void ThrottledException_WithInnerException_PreservesInner()
        {
            var inner = new InvalidOperationException("Original error");
            var exception = new ThrottledException("my-service", 50, TimeSpan.FromSeconds(2), inner);

            Assert.Equal("my-service", exception.ServiceKey);
            Assert.Same(inner, exception.InnerException);
        }

        #endregion
    }
}
