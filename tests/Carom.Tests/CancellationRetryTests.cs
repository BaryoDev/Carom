using System;
using System.Threading.Tasks;
using Xunit;

namespace Carom.Tests
{
    public class CancellationRetryTests
    {
        [Fact]
        public async Task ShotAsync_CalleeThrowsOperationCanceled_IsNotRetried()
        {
            var attempts = 0;

            var ex = await Assert.ThrowsAsync<OperationCanceledException>(() =>
                Carom.ShotAsync<int>(
                    () =>
                    {
                        attempts++;
                        throw new OperationCanceledException("callee gave up");
                    },
                    retries: 3,
                    baseDelay: TimeSpan.FromMilliseconds(1)));

            Assert.Equal("callee gave up", ex.Message);
            Assert.Equal(1, attempts);
        }

        [Fact]
        public async Task ShotAsync_VoidOverload_CalleeThrowsOperationCanceled_IsNotRetried()
        {
            var attempts = 0;

            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                Carom.ShotAsync(
                    new Func<Task>(() =>
                    {
                        attempts++;
                        throw new OperationCanceledException("callee gave up");
                    }),
                    retries: 3,
                    baseDelay: TimeSpan.FromMilliseconds(1)));

            Assert.Equal(1, attempts);
        }

        [Fact]
        public void Shot_CalleeThrowsOperationCanceled_IsNotRetried()
        {
            var attempts = 0;

            var ex = Assert.Throws<OperationCanceledException>(() =>
                Carom.Shot<int>(
                    () =>
                    {
                        attempts++;
                        throw new OperationCanceledException("callee gave up");
                    },
                    retries: 3,
                    baseDelay: TimeSpan.FromMilliseconds(1)));

            Assert.Equal("callee gave up", ex.Message);
            Assert.Equal(1, attempts);
        }

        [Fact]
        public void Shot_ActionOverload_CalleeThrowsOperationCanceled_IsNotRetried()
        {
            var attempts = 0;

            Assert.Throws<OperationCanceledException>(() =>
                Carom.Shot(
                    new Action(() =>
                    {
                        attempts++;
                        throw new OperationCanceledException("callee gave up");
                    }),
                    retries: 3,
                    baseDelay: TimeSpan.FromMilliseconds(1)));

            Assert.Equal(1, attempts);
        }

        [Fact]
        public async Task ShotAsync_ShouldBounceCannotWidenRetryToCancellation()
        {
            // A predicate may only narrow what gets retried, never widen it.
            // Even a shouldBounce that returns true for cancellation must not retry it.
            var attempts = 0;

            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                Carom.ShotAsync<int>(
                    () =>
                    {
                        attempts++;
                        throw new OperationCanceledException("callee gave up");
                    },
                    retries: 3,
                    baseDelay: TimeSpan.FromMilliseconds(1),
                    shouldBounce: ex => ex is OperationCanceledException));

            Assert.Equal(1, attempts);
        }

        [Fact]
        public void Shot_ShouldBounceCannotWidenRetryToCancellation()
        {
            var attempts = 0;

            Assert.Throws<OperationCanceledException>(() =>
                Carom.Shot<int>(
                    () =>
                    {
                        attempts++;
                        throw new OperationCanceledException("callee gave up");
                    },
                    retries: 3,
                    baseDelay: TimeSpan.FromMilliseconds(1),
                    shouldBounce: ex => ex is OperationCanceledException));

            Assert.Equal(1, attempts);
        }

        [Fact]
        public async Task ShotAsync_BounceOnException_DoesNotRetryCancellation()
        {
            // Bounce.On<Exception> supplies a predicate that is true for everything,
            // the same shape as the extension packages' default predicates. It must
            // still not retry a cancelled operation.
            var attempts = 0;
            var bounce = Bounce.On<Exception>(3).WithDelay(TimeSpan.FromMilliseconds(1));

            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                Carom.ShotAsync<int>(
                    () =>
                    {
                        attempts++;
                        throw new OperationCanceledException("callee gave up");
                    },
                    bounce));

            Assert.Equal(1, attempts);
        }

        [Fact]
        public void Shot_BounceOnException_DoesNotRetryCancellation()
        {
            var attempts = 0;
            var bounce = Bounce.On<Exception>(3).WithDelay(TimeSpan.FromMilliseconds(1));

            Assert.Throws<OperationCanceledException>(() =>
                Carom.Shot<int>(
                    () =>
                    {
                        attempts++;
                        throw new OperationCanceledException("callee gave up");
                    },
                    bounce));

            Assert.Equal(1, attempts);
        }

        [Fact]
        public async Task ShotAsync_InnerTimeout_IsNotRetriedByOuterShot()
        {
            // TimeoutRejectedException derives from OperationCanceledException, so an
            // inner Carom timeout must stop an outer Carom retry loop on attempt one,
            // even when the outer predicate would retry everything.
            var outerAttempts = 0;

            await Assert.ThrowsAsync<TimeoutRejectedException>(() =>
                Carom.ShotAsync<int>(
                    async () =>
                    {
                        outerAttempts++;
                        return await Carom.ShotAsync<int>(
                            async token =>
                            {
                                await Task.Delay(TimeSpan.FromSeconds(30), token);
                                return 1;
                            },
                            retries: 0,
                            timeout: TimeSpan.FromMilliseconds(50));
                    },
                    retries: 3,
                    baseDelay: TimeSpan.FromMilliseconds(1),
                    shouldBounce: _ => true));

            Assert.Equal(1, outerAttempts);
        }

        [Fact]
        public async Task ShotAsync_Timeout_StillThrowsTimeoutRejected_NotAffectedByCancelRule()
        {
            // TimeoutRejectedException derives from OperationCanceledException.
            // The timeout path must still surface it, with no retry after the timeout.
            await Assert.ThrowsAsync<TimeoutRejectedException>(() =>
                Carom.ShotAsync<int>(
                    async () =>
                    {
                        await Task.Delay(5000);
                        return 42;
                    },
                    retries: 3,
                    baseDelay: TimeSpan.FromMilliseconds(1),
                    timeout: TimeSpan.FromMilliseconds(100)));
        }
    }
}
