using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Carom.Tests
{
    public class CancellableCallbackTests
    {
        [Fact]
        public async Task ShotAsync_TokenOverload_TimeoutCancelsTheCallee()
        {
            var ran = false;
            var observedCancel = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            await Assert.ThrowsAsync<TimeoutRejectedException>(() =>
                Carom.ShotAsync<int>(
                    async token =>
                    {
                        try
                        {
                            await Task.Delay(TimeSpan.FromSeconds(30), token);
                        }
                        catch (OperationCanceledException)
                        {
                            observedCancel.TrySetResult(true);
                            throw;
                        }
                        ran = true;
                        observedCancel.TrySetResult(false);
                        return 1;
                    },
                    retries: 0,
                    timeout: TimeSpan.FromMilliseconds(100)));

            // The callee saw the cancellation and stopped; the work never ran to completion
            Assert.True(await observedCancel.Task);
            Assert.False(ran);
        }

        [Fact]
        public async Task ShotAsync_VoidTokenOverload_TimeoutCancelsTheCallee()
        {
            var ran = false;
            var observedCancel = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            await Assert.ThrowsAsync<TimeoutRejectedException>(() =>
                Carom.ShotAsync(
                    async token =>
                    {
                        try
                        {
                            await Task.Delay(TimeSpan.FromSeconds(30), token);
                        }
                        catch (OperationCanceledException)
                        {
                            observedCancel.TrySetResult(true);
                            throw;
                        }
                        ran = true;
                        observedCancel.TrySetResult(false);
                    },
                    retries: 0,
                    timeout: TimeSpan.FromMilliseconds(100)));

            Assert.True(await observedCancel.Task);
            Assert.False(ran);
        }

        [Fact]
        public async Task ShotAsync_TokenOverload_CallerCancellationReachesTheCallee()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            var observedCancel = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                Carom.ShotAsync<int>(
                    async token =>
                    {
                        try
                        {
                            await Task.Delay(TimeSpan.FromSeconds(30), token);
                        }
                        catch (OperationCanceledException)
                        {
                            observedCancel.TrySetResult(true);
                            throw;
                        }
                        observedCancel.TrySetResult(false);
                        return 1;
                    },
                    retries: 0,
                    ct: cts.Token));

            // Caller cancellation is not a timeout
            Assert.IsNotType<TimeoutRejectedException>(ex);
            Assert.True(await observedCancel.Task);
        }

        [Fact]
        public async Task ShotAsync_TokenOverload_ReturnsResult()
        {
            var result = await Carom.ShotAsync(
                token => Task.FromResult(42),
                retries: 0);

            Assert.Equal(42, result);
        }

        [Fact]
        public async Task ShotAsync_TokenOverload_RetriesLikeTheLegacyOverload()
        {
            var attempts = 0;

            var result = await Carom.ShotAsync(
                token =>
                {
                    attempts++;
                    if (attempts < 3) throw new InvalidOperationException("not yet");
                    return Task.FromResult(42);
                },
                retries: 3,
                baseDelay: TimeSpan.FromMilliseconds(1));

            Assert.Equal(42, result);
            Assert.Equal(3, attempts);
        }

        [Fact]
        public async Task ShotAsync_TokenOverload_WithBounce_TimeoutCancelsTheCallee()
        {
            var observedCancel = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var bounce = Bounce.Times(0).WithTimeout(TimeSpan.FromMilliseconds(100));

            await Assert.ThrowsAsync<TimeoutRejectedException>(() =>
                Carom.ShotAsync<int>(
                    async token =>
                    {
                        try
                        {
                            await Task.Delay(TimeSpan.FromSeconds(30), token);
                        }
                        catch (OperationCanceledException)
                        {
                            observedCancel.TrySetResult(true);
                            throw;
                        }
                        observedCancel.TrySetResult(false);
                        return 1;
                    },
                    bounce));

            Assert.True(await observedCancel.Task);
        }

        [Fact]
        public async Task ShotAsync_TokenOverload_WithTypedBounce_RetriesOnResult()
        {
            var attempts = 0;
            var bounce = Bounce.For<int>(3)
                .WithDelay(TimeSpan.FromMilliseconds(1))
                .WhenResult(r => r < 0);

            var result = await Carom.ShotAsync(
                token =>
                {
                    attempts++;
                    return Task.FromResult(attempts < 3 ? -1 : 42);
                },
                bounce);

            Assert.Equal(42, result);
            Assert.Equal(3, attempts);
        }

        [Fact]
        public async Task ShotAsync_VoidTokenOverload_WithBounce_Runs()
        {
            var ran = false;

            await Carom.ShotAsync(
                token =>
                {
                    ran = true;
                    return Task.CompletedTask;
                },
                Bounce.Times(0));

            Assert.True(ran);
        }

        [Fact]
        public async Task ShotAsync_LegacyOverload_StillAbandonsTheWorkOnTimeout()
        {
            var ran = false;
            var innerDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            await Assert.ThrowsAsync<TimeoutRejectedException>(() =>
                Carom.ShotAsync<int>(
                    async () =>
                    {
                        await Task.Delay(1000);
                        ran = true;
                        innerDone.TrySetResult(true);
                        return 1;
                    },
                    retries: 0,
                    timeout: TimeSpan.FromMilliseconds(100)));

            // The timeout threw before the inner work finished
            Assert.False(ran);

            // The abandoned work has no token and still runs to completion
            Assert.True(await innerDone.Task);
            Assert.True(ran);
        }
    }
}
