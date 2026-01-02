using System;
using System.Threading.Tasks;
using Xunit;

namespace Carom.Tests
{
    /// <summary>
    /// Smoke tests for critical Carom functionality.
    /// These tests verify that basic operations work correctly.
    /// Run these first to catch fundamental issues.
    /// </summary>
    public class SmokeTests
    {
        [Fact]
        public void Smoke_BasicRetry_Works()
        {
            var result = Carom.Shot(() => 42, retries: 3);
            Assert.Equal(42, result);
        }

        [Fact]
        public async Task Smoke_AsyncRetry_Works()
        {
            var result = await Carom.ShotAsync(
                async () =>
                {
                    await Task.Yield();
                    return 42;
                },
                retries: 3);
            
            Assert.Equal(42, result);
        }

        [Fact]
        public void Smoke_RetryWithFailure_WorksCorrectly()
        {
            var attempts = 0;
            var result = Carom.Shot(() =>
            {
                attempts++;
                if (attempts < 3)
                {
                    throw new InvalidOperationException("Transient failure");
                }
                return 42;
            }, retries: 5, baseDelay: TimeSpan.FromMilliseconds(1));
            
            Assert.Equal(42, result);
            Assert.Equal(3, attempts);
        }

        [Fact]
        public void Smoke_Bounce_Configuration_Works()
        {
            var bounce = Bounce.Times(3)
                .WithDelay(TimeSpan.FromMilliseconds(100))
                .WithTimeout(TimeSpan.FromSeconds(10));
            
            var result = Carom.Shot(() => 42, bounce);
            Assert.Equal(42, result);
        }

        [Fact]
        public void Smoke_Bounce_WithTimeout_Configuration_Works()
        {
            var bounce = Bounce.Times(3)
                .WithDelay(TimeSpan.FromMilliseconds(100))
                .WithTimeout(TimeSpan.FromSeconds(10));
            
            Assert.Equal(TimeSpan.FromSeconds(10), bounce.Timeout);
            
            var result = Carom.Shot(() => 42, bounce);
            Assert.Equal(42, result);
        }

        [Fact]
        public void Smoke_ExceptionPreserved_OnAllRetriesFail()
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
            {
                Carom.Shot<int>(() =>
                {
                    throw new InvalidOperationException("Test error");
                }, retries: 2, baseDelay: TimeSpan.FromMilliseconds(1));
            });
            
            Assert.Equal("Test error", ex.Message);
        }
    }
}
