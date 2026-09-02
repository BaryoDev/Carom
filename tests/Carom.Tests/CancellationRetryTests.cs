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
        public async Task ShotAsync_ShouldBounceOptsIn_OperationCanceledIsRetried()
        {
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

            Assert.Equal(4, attempts);
        }

        [Fact]
        public void Shot_ShouldBounceOptsIn_OperationCanceledIsRetried()
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

            Assert.Equal(4, attempts);
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
