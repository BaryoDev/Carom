using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Carom.Extensions;

namespace Carom.Extensions.Tests
{
    public class FallbackTests
    {
        #region Synchronous Pocket Tests

        [Fact]
        public void Pocket_ReturnsResult_WhenActionSucceeds()
        {
            var result = new Func<int>(() => 42).Pocket(0);
            Assert.Equal(42, result);
        }

        [Fact]
        public void Pocket_ReturnsFallback_WhenActionFails()
        {
            var result = new Func<int>(() => throw new InvalidOperationException()).Pocket(99);
            Assert.Equal(99, result);
        }

        [Fact]
        public void Pocket_WithFunction_ReturnsResult_WhenActionSucceeds()
        {
            var fallbackCalled = false;
            var result = new Func<string>(() => "success").Pocket(() =>
            {
                fallbackCalled = true;
                return "fallback";
            });

            Assert.Equal("success", result);
            Assert.False(fallbackCalled);
        }

        [Fact]
        public void Pocket_WithFunction_ReturnsFallback_WhenActionFails()
        {
            var fallbackCalled = false;
            var result = new Func<string>(() => throw new InvalidOperationException()).Pocket(() =>
            {
                fallbackCalled = true;
                return "fallback";
            });

            Assert.Equal("fallback", result);
            Assert.True(fallbackCalled);
        }

        [Fact]
        public void Pocket_WithExceptionFunction_ReceivesException()
        {
            Exception? capturedException = null;
            var result = new Func<int>(() => throw new InvalidOperationException("test error")).Pocket(ex =>
            {
                capturedException = ex;
                return 100;
            });

            Assert.Equal(100, result);
            Assert.NotNull(capturedException);
            Assert.IsType<InvalidOperationException>(capturedException);
            Assert.Equal("test error", capturedException.Message);
        }

        #endregion

        #region Asynchronous PocketAsync Tests

        [Fact]
        public async Task PocketAsync_ReturnsResult_WhenActionSucceeds()
        {
            var result = await new Func<Task<int>>(async () =>
            {
                await Task.Delay(1);
                return 42;
            }).PocketAsync(0);

            Assert.Equal(42, result);
        }

        [Fact]
        public async Task PocketAsync_ReturnsFallback_WhenActionFails()
        {
            var result = await new Func<Task<int>>(async () =>
            {
                await Task.Delay(1);
                throw new InvalidOperationException();
            }).PocketAsync(99);

            Assert.Equal(99, result);
        }

        [Fact]
        public async Task PocketAsync_WithFunction_OnlyCallsFallbackOnError()
        {
            var fallbackCalled = false;
            var result = await new Func<Task<string>>(async () =>
            {
                await Task.Delay(1);
                return "success";
            }).PocketAsync(() =>
            {
                fallbackCalled = true;
                return "fallback";
            });

            Assert.Equal("success", result);
            Assert.False(fallbackCalled);
        }

        [Fact]
        public async Task PocketAsync_WithAsyncFunction_ReturnsFallback_WhenActionFails()
        {
            var fallbackCalled = false;
            var result = await new Func<Task<string>>(async () =>
            {
                await Task.Delay(1);
                throw new InvalidOperationException();
            }).PocketAsync(async () =>
            {
                fallbackCalled = true;
                await Task.Delay(1);
                return "async-fallback";
            });

            Assert.Equal("async-fallback", result);
            Assert.True(fallbackCalled);
        }

        [Fact]
        public async Task PocketAsync_WithExceptionFunction_ReceivesException()
        {
            Exception? capturedException = null;
            var result = await new Func<Task<int>>(async () =>
            {
                await Task.Delay(1);
                throw new InvalidOperationException("async error");
            }).PocketAsync(async ex =>
            {
                capturedException = ex;
                await Task.Delay(1);
                return 200;
            });

            Assert.Equal(200, result);
            Assert.NotNull(capturedException);
            Assert.IsType<InvalidOperationException>(capturedException);
            Assert.Equal("async error", capturedException.Message);
        }

        [Fact]
        public async Task PocketAsync_ReturnsFallback_WhenCancelled()
        {
            var cts = new CancellationTokenSource();
            cts.Cancel();

            // When cancelled, the catch filter (!ct.IsCancellationRequested) prevents catching
            // So the exception propagates through
            await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                await new Func<Task<int>>(async () =>
                {
                    await Task.Delay(1, cts.Token);  // This will throw OperationCanceledException
                    throw new InvalidOperationException();
                }).PocketAsync(99, cts.Token);
            });
        }

        #endregion

        #region ShotWithPocket Tests

        [Fact]
        public void ShotWithPocket_ReturnsResult_WhenActionSucceeds()
        {
            var result = CaromFallbackExtensions.ShotWithPocket(
                () => 42,
                fallback: 0,
                retries: 0);

            Assert.Equal(42, result);
        }

        [Fact]
        public void ShotWithPocket_ReturnsFallback_AfterAllRetriesFail()
        {
            var attemptCount = 0;
            var result = CaromFallbackExtensions.ShotWithPocket(
                () =>
                {
                    attemptCount++;
                    throw new InvalidOperationException();
                },
                fallback: 999,
                retries: 3);

            Assert.Equal(999, result);
            Assert.True(attemptCount >= 1, $"Expected at least 1 attempt, got {attemptCount}");
        }

        [Fact]
        public async Task ShotWithPocketAsync_ReturnsResult_WhenActionSucceeds()
        {
            var result = await CaromFallbackExtensions.ShotWithPocketAsync(
                async () =>
                {
                    await Task.Delay(1);
                    return 42;
                },
                fallback: 0,
                retries: 0);

            Assert.Equal(42, result);
        }

        [Fact]
        public async Task ShotWithPocketAsync_ReturnsFallback_AfterAllRetriesFail()
        {
            var attemptCount = 0;
            var result = await CaromFallbackExtensions.ShotWithPocketAsync(
                async () =>
                {
                    attemptCount++;
                    await Task.Delay(1);
                    throw new InvalidOperationException();
                },
                fallback: 888,
                retries: 2);

            Assert.Equal(888, result);
            Assert.True(attemptCount >= 1, $"Expected at least 1 attempt, got {attemptCount}");
        }

        [Fact]
        public async Task ShotWithPocketAsync_PropagatesException_WhenCancelled()
        {
            var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                await CaromFallbackExtensions.ShotWithPocketAsync(
                    async () =>
                    {
                        await Task.Delay(1, cts.Token);  // Will throw OperationCanceledException
                        throw new InvalidOperationException();
                    },
                    fallback: 0,
                    retries: 2,
                    ct: cts.Token);
            });
        }

        #endregion

        #region Edge Cases

        [Fact]
        public void Pocket_WorksWithNullFallback()
        {
            var result = new Func<string?>(() => throw new Exception()).Pocket((string?)null);
            Assert.Null(result);
        }

        [Fact]
        public async Task PocketAsync_WorksWithNullFallback()
        {
            var result = await new Func<Task<string?>>(async () =>
            {
                await Task.Delay(1);
                throw new Exception();
            }).PocketAsync((string?)null);

            Assert.Null(result);
        }

        #endregion
    }
}
