using System;
using System.Threading.Tasks;
using Xunit;

namespace Carom.Tests
{
    public class NegativeDelayTests
    {
        [Fact]
        public void Bounce_WithDelay_RejectsNegative()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                Bounce.Times(2).WithDelay(TimeSpan.FromMilliseconds(-100)));
        }

        [Fact]
        public void BounceOfT_WithDelay_RejectsNegative()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                Bounce<int>.Times(2).WithDelay(TimeSpan.FromMilliseconds(-100)));
        }

        [Fact]
        public void Bounce_WithDelay_AcceptsZero()
        {
            var bounce = Bounce.Times(2).WithDelay(TimeSpan.Zero);
            Assert.Equal(TimeSpan.Zero, bounce.BaseDelay);
        }

        [Fact]
        public void CalculateDelay_NegativeInputs_NeverProducesNegativeDelay()
        {
            var negative = TimeSpan.FromMilliseconds(-100);

            for (int i = 0; i < 1000; i++)
            {
                var jittered = JitterStrategy.CalculateDelay(negative, negative, disableJitter: false, attempt: 1);
                Assert.True(jittered >= TimeSpan.Zero, $"Jittered delay was {jittered}");

                var fixedBackoff = JitterStrategy.CalculateDelay(negative, negative, disableJitter: true, attempt: i % 10 + 1);
                Assert.True(fixedBackoff >= TimeSpan.Zero, $"Fixed backoff delay was {fixedBackoff}");
            }
        }

        [Fact]
        public void Shot_NegativeBaseDelay_SurfacesCallersException()
        {
            var attempts = 0;

            var ex = Assert.Throws<InvalidOperationException>(() =>
                Carom.Shot<int>(
                    () =>
                    {
                        attempts++;
                        throw new InvalidOperationException("the real failure");
                    },
                    retries: 2,
                    baseDelay: TimeSpan.FromMilliseconds(-100)));

            Assert.Equal("the real failure", ex.Message);
            Assert.Equal(3, attempts);
        }

        [Fact]
        public async Task ShotAsync_NegativeBaseDelay_SurfacesCallersException()
        {
            var attempts = 0;

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Carom.ShotAsync<int>(
                    () =>
                    {
                        attempts++;
                        throw new InvalidOperationException("the real failure");
                    },
                    retries: 2,
                    baseDelay: TimeSpan.FromMilliseconds(-100)));

            Assert.Equal("the real failure", ex.Message);
            Assert.Equal(3, attempts);
        }
    }
}
