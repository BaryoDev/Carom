using System;
using System.Threading.Tasks;
using Xunit;

namespace Carom.Tests
{
    /// <summary>
    /// Tests for result-based retry functionality.
    /// </summary>
    public class ResultRetryTests
    {
        [Fact]
        public void Shot_WithResultPredicate_RetriesOnMatch()
        {
            var attempts = 0;

            var result = Carom.Shot(
                () =>
                {
                    attempts++;
                    return attempts < 3 ? "retry" : "success";
                },
                retries: 5,
                baseDelay: TimeSpan.FromMilliseconds(1),
                shouldBounce: null,
                shouldRetryResult: r => r == "retry");

            Assert.Equal("success", result);
            Assert.Equal(3, attempts);
        }

        [Fact]
        public void Shot_WithResultPredicate_ReturnsLastResultWhenRetriesExhausted()
        {
            var attempts = 0;

            var result = Carom.Shot(
                () =>
                {
                    attempts++;
                    return "always-retry";
                },
                retries: 3,
                baseDelay: TimeSpan.FromMilliseconds(1),
                shouldBounce: null,
                shouldRetryResult: r => r == "always-retry");

            Assert.Equal("always-retry", result);
            Assert.Equal(4, attempts); // 1 initial + 3 retries
        }

        [Fact]
        public void Shot_WithResultPredicate_DoesNotRetryOnSuccess()
        {
            var attempts = 0;

            var result = Carom.Shot(
                () =>
                {
                    attempts++;
                    return "success";
                },
                retries: 3,
                baseDelay: TimeSpan.FromMilliseconds(1),
                shouldBounce: null,
                shouldRetryResult: r => r == "retry");

            Assert.Equal("success", result);
            Assert.Equal(1, attempts);
        }

        [Fact]
        public void Shot_WithBothPredicates_HandlesExceptionAndResult()
        {
            var attempts = 0;

            var result = Carom.Shot(
                () =>
                {
                    attempts++;
                    if (attempts == 1)
                        throw new InvalidOperationException("First attempt fails");
                    if (attempts == 2)
                        return "retry-result";
                    return "success";
                },
                retries: 5,
                baseDelay: TimeSpan.FromMilliseconds(1),
                shouldBounce: ex => ex is InvalidOperationException,
                shouldRetryResult: r => r == "retry-result");

            Assert.Equal("success", result);
            Assert.Equal(3, attempts);
        }

        [Fact]
        public async Task ShotAsync_WithResultPredicate_RetriesOnMatch()
        {
            var attempts = 0;

            var result = await Carom.ShotAsync(
                async () =>
                {
                    await Task.Yield();
                    attempts++;
                    return attempts < 3 ? -1 : 42;
                },
                retries: 5,
                baseDelay: TimeSpan.FromMilliseconds(1),
                timeout: null,
                shouldBounce: null,
                shouldRetryResult: r => r < 0);

            Assert.Equal(42, result);
            Assert.Equal(3, attempts);
        }

        [Fact]
        public async Task ShotAsync_WithResultPredicate_RespectsTimeout()
        {
            var attempts = 0;

            await Assert.ThrowsAsync<TimeoutRejectedException>(async () =>
            {
                await Carom.ShotAsync(
                    async () =>
                    {
                        attempts++;
                        await Task.Delay(100); // Long operation
                        return "retry";
                    },
                    retries: 10,
                    baseDelay: TimeSpan.FromMilliseconds(1),
                    timeout: TimeSpan.FromMilliseconds(50),
                    shouldBounce: null,
                    shouldRetryResult: r => r == "retry");
            });
        }

        [Fact]
        public void Bounce_WhenResult_CreatesPredicateCorrectly()
        {
            var bounce = Bounce.For<int>(3)
                .WhenResult(r => r < 0)
                .WithDelay(TimeSpan.FromMilliseconds(1));

            Assert.Equal(3, bounce.Retries);
            Assert.NotNull(bounce.ShouldRetryResult);
            Assert.True(bounce.ShouldRetryResult!(-1));
            Assert.False(bounce.ShouldRetryResult!(1));
        }

        [Fact]
        public void Shot_WithTypedBounce_RetriesOnResult()
        {
            var attempts = 0;
            var bounce = Bounce.For<string>(5)
                .WhenResult(r => r == "retry")
                .WithDelay(TimeSpan.FromMilliseconds(1));

            var result = Carom.Shot(
                () =>
                {
                    attempts++;
                    return attempts < 3 ? "retry" : "done";
                },
                bounce);

            Assert.Equal("done", result);
            Assert.Equal(3, attempts);
        }

        [Fact]
        public async Task ShotAsync_WithTypedBounce_RetriesOnResult()
        {
            var attempts = 0;
            var bounce = Bounce.For<int>(5)
                .WhenResult(r => r == 0)
                .WithDelay(TimeSpan.FromMilliseconds(1));

            var result = await Carom.ShotAsync(
                async () =>
                {
                    await Task.Yield();
                    attempts++;
                    return attempts < 4 ? 0 : 42;
                },
                bounce);

            Assert.Equal(42, result);
            Assert.Equal(4, attempts);
        }

        [Fact]
        public void Bounce_ForT_ChainsAllConfigurations()
        {
            var bounce = Bounce.For<HttpResponse>(3)
                .When(ex => ex is TimeoutException)
                .WhenResult(r => r.StatusCode >= 500)
                .WithDelay(TimeSpan.FromMilliseconds(100))
                .WithTimeout(TimeSpan.FromSeconds(30))
                .WithoutJitter();

            Assert.Equal(3, bounce.Retries);
            Assert.Equal(TimeSpan.FromMilliseconds(100), bounce.BaseDelay);
            Assert.Equal(TimeSpan.FromSeconds(30), bounce.Timeout);
            Assert.True(bounce.DisableJitter);
            Assert.NotNull(bounce.ShouldBounce);
            Assert.NotNull(bounce.ShouldRetryResult);
        }

        // Test helper class
        private class HttpResponse
        {
            public int StatusCode { get; set; }
        }
    }
}
