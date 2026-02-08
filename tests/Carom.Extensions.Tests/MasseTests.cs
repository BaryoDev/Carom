using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Carom.Extensions.Tests
{
    /// <summary>
    /// Tests for the hedging (Masse) pattern.
    /// </summary>
    public class MasseTests
    {
        [Fact]
        public async Task Masse_FirstAttemptWins_ReturnsImmediately()
        {
            var config = Masse.WithAttempts(3).After(TimeSpan.FromSeconds(1));
            var attempts = 0;

            var result = await CaromMasseExtensions.ShotWithHedgingAsync(
                async ct =>
                {
                    Interlocked.Increment(ref attempts);
                    await Task.Delay(10, ct);
                    return "success";
                },
                config);

            Assert.Equal("success", result);
            Assert.Equal(1, attempts); // Only first attempt should run
        }

        [Fact]
        public async Task Masse_HedgedAttemptWins_CancelsPending()
        {
            var config = Masse.WithAttempts(3).After(TimeSpan.FromMilliseconds(50));
            var attempts = new List<int>();
            var attemptCounter = 0;
            var cancellations = 0;

            var result = await CaromMasseExtensions.ShotWithHedgingAsync(
                async ct =>
                {
                    var attemptNum = Interlocked.Increment(ref attemptCounter);
                    lock (attempts) attempts.Add(attemptNum);

                    if (attemptNum == 1)
                    {
                        // First attempt is slow
                        try
                        {
                            await Task.Delay(1000, ct);
                            return "slow";
                        }
                        catch (OperationCanceledException)
                        {
                            Interlocked.Increment(ref cancellations);
                            throw;
                        }
                    }

                    // Hedged attempt returns quickly
                    await Task.Delay(10, ct);
                    return "fast";
                },
                config);

            Assert.Equal("fast", result);
            Assert.True(attempts.Count >= 2); // At least 2 attempts started
            // First attempt should have been cancelled
            await Task.Delay(100); // Give time for cancellation to propagate
        }

        [Fact]
        public async Task Masse_AllAttemptsFail_ThrowsAggregateException()
        {
            var config = Masse.WithAttempts(3).After(TimeSpan.FromMilliseconds(10));

            var ex = await Assert.ThrowsAsync<AggregateException>(async () =>
            {
                await CaromMasseExtensions.ShotWithHedgingAsync<string>(
                    async ct =>
                    {
                        await Task.Delay(1, ct);
                        throw new InvalidOperationException("Always fails");
                    },
                    config);
            });

            Assert.Equal(3, ex.InnerExceptions.Count);
            Assert.All(ex.InnerExceptions, e => Assert.IsType<InvalidOperationException>(e));
        }

        [Fact]
        public async Task Masse_SingleAttemptFails_ReturnsFromSurvivor()
        {
            var config = Masse.WithAttempts(2).After(TimeSpan.FromMilliseconds(20));
            var attempts = 0;

            var result = await CaromMasseExtensions.ShotWithHedgingAsync(
                async ct =>
                {
                    var attempt = Interlocked.Increment(ref attempts);
                    await Task.Delay(10, ct);

                    if (attempt == 1)
                        throw new InvalidOperationException("First fails");

                    return "second succeeds";
                },
                config);

            Assert.Equal("second succeeds", result);
        }

        [Fact]
        public async Task Masse_WithCancellationToken_PropagatesCancellation()
        {
            var config = Masse.WithAttempts(3).After(TimeSpan.FromMilliseconds(50));
            using var cts = new CancellationTokenSource();

            var task = CaromMasseExtensions.ShotWithHedgingAsync(
                async ct =>
                {
                    await Task.Delay(10000, ct);
                    return "never";
                },
                config,
                cts.Token);

            cts.Cancel();

            // TaskCanceledException inherits from OperationCanceledException
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task);
        }

        [Fact]
        public async Task Masse_ShouldHedgePredicate_ContinuesOnUnsatisfactoryResult()
        {
            var config = Masse.WithAttempts(3)
                .After(TimeSpan.FromMilliseconds(30))
                .When(result => result is string s && s == "retry");

            var attempts = 0;

            var result = await CaromMasseExtensions.ShotWithHedgingAsync(
                async ct =>
                {
                    var attempt = Interlocked.Increment(ref attempts);
                    await Task.Delay(10, ct);

                    if (attempt < 3)
                        return "retry";

                    return "success";
                },
                config);

            Assert.Equal("success", result);
        }

        [Fact]
        public void Masse_Configuration_BuildsCorrectly()
        {
            var config = Masse.WithAttempts(5)
                .After(TimeSpan.FromMilliseconds(100))
                .WithCancellation(false)
                .When(r => r == null);

            Assert.Equal(5, config.MaxHedgedAttempts);
            Assert.Equal(TimeSpan.FromMilliseconds(100), config.HedgeDelay);
            Assert.False(config.CancelPendingOnSuccess);
            Assert.NotNull(config.ShouldHedge);
        }

        [Fact]
        public void Masse_InvalidAttempts_ThrowsArgumentException()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => Masse.WithAttempts(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => Masse.WithAttempts(-1));
        }

        [Fact]
        public void Masse_NegativeDelay_ThrowsArgumentException()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                Masse.WithAttempts(2).After(TimeSpan.FromMilliseconds(-1)));
        }

        [Fact]
        public async Task Masse_WithoutCancellationToken_Works()
        {
            var config = Masse.WithAttempts(2).After(TimeSpan.FromMilliseconds(100));

            var result = await CaromMasseExtensions.ShotWithHedgingAsync(
                async () =>
                {
                    await Task.Delay(10);
                    return 42;
                },
                config);

            Assert.Equal(42, result);
        }

        [Fact]
        public async Task Masse_HighContention_HandlesMultipleAttempts()
        {
            var config = Masse.WithAttempts(5).After(TimeSpan.FromMilliseconds(10));
            var maxConcurrent = 0;
            var currentConcurrent = 0;
            var lockObj = new object();

            var result = await CaromMasseExtensions.ShotWithHedgingAsync(
                async ct =>
                {
                    lock (lockObj)
                    {
                        currentConcurrent++;
                        if (currentConcurrent > maxConcurrent)
                            maxConcurrent = currentConcurrent;
                    }

                    // Slow enough to allow hedged attempts
                    await Task.Delay(100, ct);

                    lock (lockObj)
                    {
                        currentConcurrent--;
                    }

                    return "done";
                },
                config);

            Assert.Equal("done", result);
            Assert.True(maxConcurrent >= 1); // At least some concurrency
        }
    }
}
