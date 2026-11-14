using Carom.Extensions;

namespace Carom.Extensions.Tests
{
    /// <summary>
    /// CaromAttemptTimeoutExtensions: each try gets its own timeout, a try that runs out fails
    /// with a retryable TimeoutRejectedException, and the overall timeout still wins.
    /// A zero base delay keeps the waits between tries at zero.
    /// </summary>
    public class AttemptTimeoutTests
    {
        private static Bounce Quick(int retries) => Bounce.Times(retries).WithDelay(TimeSpan.Zero);

        [Fact]
        public async Task PerTryTimeout_FirstTryHangs_RetriesAndTheNextTrySucceeds()
        {
            int calls = 0;

            var result = await CaromAttemptTimeoutExtensions.ShotAsync<int>(async ct =>
            {
                calls++;
                if (calls == 1) await Task.Delay(Timeout.Infinite, ct);
                return 7;
            }, Quick(2), TimeSpan.FromMilliseconds(50));

            Assert.Equal(7, result);
            Assert.Equal(2, calls);
        }

        [Fact]
        public async Task PerTryTimeout_ActionThatIgnoresItsToken_IsAbandonedAndRetried()
        {
            int calls = 0;

            var result = await CaromAttemptTimeoutExtensions.ShotAsync<int>(ct =>
            {
                calls++;
                return calls == 1 ? new TaskCompletionSource<int>().Task : Task.FromResult(7);
            }, Quick(2), TimeSpan.FromMilliseconds(50));

            Assert.Equal(7, result);
            Assert.Equal(2, calls);
        }

        [Fact]
        public async Task PerTryTimeout_VoidAction_RetriesAndSucceeds()
        {
            int calls = 0;

            await CaromAttemptTimeoutExtensions.ShotAsync(async ct =>
            {
                calls++;
                if (calls == 1) await Task.Delay(Timeout.Infinite, ct);
            }, Quick(2), TimeSpan.FromMilliseconds(50));

            Assert.Equal(2, calls);
        }

        [Fact]
        public async Task PerTryTimeout_TypedBounce_RetriesAndSucceeds()
        {
            int calls = 0;
            var bounce = Bounce.For<string>(2).WithDelay(TimeSpan.Zero);

            var result = await CaromAttemptTimeoutExtensions.ShotAsync<string>(async ct =>
            {
                calls++;
                if (calls == 1) await Task.Delay(Timeout.Infinite, ct);
                return "ok";
            }, bounce, TimeSpan.FromMilliseconds(50));

            Assert.Equal("ok", result);
        }

        [Fact]
        public async Task PerTryTimeout_EveryTryHangs_ThrowsTimeoutRejectedWithTheAttemptTimeout()
        {
            var attempt = TimeSpan.FromMilliseconds(40);
            int calls = 0;

            var ex = await Assert.ThrowsAsync<TimeoutRejectedException>(() =>
                CaromAttemptTimeoutExtensions.ShotAsync<int>(async ct =>
                {
                    calls++;
                    await Task.Delay(Timeout.Infinite, ct);
                    return 0;
                }, Quick(1), attempt));

            Assert.Equal(attempt, ex.Timeout);
            Assert.Equal(2, calls);
        }

        [Fact]
        public async Task OverallTimeout_StillWins_OverALongerAttemptTimeout()
        {
            var overall = TimeSpan.FromMilliseconds(150);
            var bounce = Quick(5).WithTimeout(overall);

            var ex = await Assert.ThrowsAsync<TimeoutRejectedException>(() =>
                CaromAttemptTimeoutExtensions.ShotAsync<int>(async ct =>
                {
                    await Task.Delay(Timeout.Infinite, ct);
                    return 0;
                }, bounce, TimeSpan.FromSeconds(30)));

            Assert.Equal(overall, ex.Timeout);
        }

        [Fact]
        public async Task CallerCancellation_IsNotTurnedIntoAnAttemptTimeout()
        {
            using var cts = new CancellationTokenSource();
            int calls = 0;

            var task = CaromAttemptTimeoutExtensions.ShotAsync<int>(async ct =>
            {
                calls++;
                await Task.Delay(Timeout.Infinite, ct);
                return 0;
            }, Quick(3), TimeSpan.FromSeconds(30), cts.Token);
            cts.CancelAfter(50);

            var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);

            Assert.IsNotType<TimeoutRejectedException>(ex);
            Assert.Equal(1, calls);
        }

        [Fact]
        public async Task NonPositiveAttemptTimeout_AndNullAction_Throw()
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                CaromAttemptTimeoutExtensions.ShotAsync<int>(ct => Task.FromResult(1), Quick(1), TimeSpan.Zero));
            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                CaromAttemptTimeoutExtensions.ShotAsync<int>(null!, Quick(1), TimeSpan.FromSeconds(1)));
        }

        [Fact]
        public async Task OverallTimeout_ReportsTheOverallTimeout_NotTheAttemptTimeout()
        {
            // Both timers can be the one that fires; the attempt timeout is longer here, so any
            // TimeoutRejectedException must carry the overall value.
            var overall = TimeSpan.FromMilliseconds(100);
            var attempt = TimeSpan.FromSeconds(30);

            var ex = await Assert.ThrowsAsync<TimeoutRejectedException>(() =>
                CaromAttemptTimeoutExtensions.ShotAsync<int>(async ct =>
                {
                    await Task.Delay(Timeout.Infinite, ct);
                    return 0;
                }, Quick(5).WithTimeout(overall), attempt));

            Assert.Equal(overall, ex.Timeout);
            Assert.NotEqual(attempt, ex.Timeout);
        }

        [Fact]
        public async Task CallerCancellation_SurfacesAsCancellation_NotAsAnAttemptTimeout()
        {
            using var cts = new CancellationTokenSource();
            var started = new TaskCompletionSource<bool>();

            var task = CaromAttemptTimeoutExtensions.ShotAsync<int>(async ct =>
            {
                started.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, ct);
                return 0;
            }, Quick(3), TimeSpan.FromSeconds(30), cts.Token);
            await started.Task;
            cts.Cancel();

            var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);

            Assert.False(ex is TimeoutRejectedException, "caller cancellation was reported as a try timeout");
        }

        [Fact]
        public async Task ACancelledTry_WhenTheCallerCancelled_IsNotReportedAsAnAttemptTimeout()
        {
            // The caller cancels and the action returns an already-cancelled task, so the try
            // ends with the caller's cancellation before any timer fires. Core's own race
            // cannot pre-empt this: the task is complete when it is awaited.
            using var cts = new CancellationTokenSource();
            int calls = 0;

            var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                CaromAttemptTimeoutExtensions.ShotAsync<int>(ct =>
                {
                    calls++;
                    cts.Cancel();
                    return Task.FromCanceled<int>(ct);
                }, Quick(3), TimeSpan.FromSeconds(30), cts.Token));

            Assert.False(ex is TimeoutRejectedException, "caller cancellation was reported as a try timeout");
            Assert.Equal(1, calls);
        }

        [Fact]
        public async Task TheTryTokenSource_IsDisposedWhenTheTryEnds()
        {
            CancellationToken seen = default;

            await CaromAttemptTimeoutExtensions.ShotAsync<int>(ct =>
            {
                seen = ct;
                return Task.FromResult(1);
            }, Quick(0), TimeSpan.FromSeconds(30));

            // WaitHandle throws once the source behind the token is disposed.
            Assert.Throws<ObjectDisposedException>(() => seen.WaitHandle);
        }

        private sealed class AbandonedTryFault : Exception { }

        [Fact]
        public async Task AnAbandonedTry_ThatLaterFaults_IsObserved()
        {
            int unobserved = 0;
            EventHandler<UnobservedTaskExceptionEventArgs> handler = (_, e) =>
            {
                if (e.Exception.InnerExceptions.Any(x => x is AbandonedTryFault)) Interlocked.Increment(ref unobserved);
            };
            TaskScheduler.UnobservedTaskException += handler;
            try
            {
                await RunWithAbandonedFaultingTry();

                for (int i = 0; i < 5; i++)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    await Task.Delay(20);
                }

                Assert.Equal(0, unobserved);
            }
            finally
            {
                TaskScheduler.UnobservedTaskException -= handler;
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static async Task RunWithAbandonedFaultingTry()
        {
            var abandoned = new TaskCompletionSource<int>();
            int calls = 0;

            var result = await CaromAttemptTimeoutExtensions.ShotAsync<int>(ct =>
            {
                calls++;
                return calls == 1 ? abandoned.Task : Task.FromResult(7);
            }, Quick(2), TimeSpan.FromMilliseconds(50));

            Assert.Equal(7, result);
            // The first try was abandoned; now it fails with nobody awaiting it.
            abandoned.SetException(new AbandonedTryFault());
        }

        [Fact]
        public async Task AttemptTimeoutBeyondWhatCancelAfterAccepts_IsRejectedUpFront()
        {
            int calls = 0;
            Func<CancellationToken, Task<int>> action = ct => { calls++; return Task.FromResult(1); };

            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                CaromAttemptTimeoutExtensions.ShotAsync(action, Quick(3), TimeSpan.MaxValue));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                CaromAttemptTimeoutExtensions.ShotAsync(action, Quick(3), TimeSpan.FromDays(50)));

            Assert.Equal(0, calls);

            // The largest accepted value still runs.
            var result = await CaromAttemptTimeoutExtensions.ShotAsync(
                action, Quick(0), TimeSpan.FromMilliseconds(int.MaxValue));
            Assert.Equal(1, result);
        }
    }
}
