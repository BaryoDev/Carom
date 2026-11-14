using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Carom.Tests
{
    /// <summary>
    /// Bounce.WithAttemptTimeout: each try gets its own timeout, a try that runs out fails
    /// with a retryable TimeoutRejectedException, and the overall timeout still wins.
    /// Waits between tries go through a fake clock so no test sleeps for real.
    /// </summary>
    public class AttemptTimeoutTests
    {
        private static readonly Func<TimeSpan, CancellationToken, Task> NoWait = (_, _) => Task.CompletedTask;

        [Fact]
        public void WithAttemptTimeout_SetsProperty_AndRejectsNonPositive()
        {
            var bounce = Bounce.Times(1).WithAttemptTimeout(TimeSpan.FromSeconds(2));

            Assert.Equal(TimeSpan.FromSeconds(2), bounce.AttemptTimeout);
            Assert.Null(Bounce.Times(1).AttemptTimeout);
            Assert.Throws<ArgumentOutOfRangeException>(() => Bounce.Times(1).WithAttemptTimeout(TimeSpan.Zero));
        }

        [Fact]
        public void WithAttemptTimeout_SurvivesTheOtherWithMethods()
        {
            var bounce = Bounce.Times(1)
                .WithAttemptTimeout(TimeSpan.FromSeconds(2))
                .WithDelay(TimeSpan.FromMilliseconds(5))
                .WithTimeout(TimeSpan.FromSeconds(9))
                .WithMaxDelay(TimeSpan.FromSeconds(1))
                .WithoutJitter();

            Assert.Equal(TimeSpan.FromSeconds(2), bounce.AttemptTimeout);
        }

        [Fact]
        public async Task PerTryTimeout_FirstTryHangs_RetriesAndTheNextTrySucceeds()
        {
            var bounce = Bounce.Times(2).WithAttemptTimeout(TimeSpan.FromMilliseconds(50)).WithSleep(NoWait);
            int calls = 0;

            var result = await Carom.ShotAsync<int>(async ct =>
            {
                calls++;
                if (calls == 1) await Task.Delay(Timeout.Infinite, ct);
                return 7;
            }, bounce);

            Assert.Equal(7, result);
            Assert.Equal(2, calls);
        }

        [Fact]
        public async Task PerTryTimeout_WithAnActionThatTakesNoToken_AbandonsTheTryAndRetries()
        {
            var bounce = Bounce.Times(2).WithAttemptTimeout(TimeSpan.FromMilliseconds(50)).WithSleep(NoWait);
            int calls = 0;

            var result = await Carom.ShotAsync<int>(() =>
            {
                calls++;
                return calls == 1 ? new TaskCompletionSource<int>().Task : Task.FromResult(7);
            }, bounce);

            Assert.Equal(7, result);
            Assert.Equal(2, calls);
        }

        [Fact]
        public async Task PerTryTimeout_VoidAction_RetriesAndSucceeds()
        {
            var bounce = Bounce.Times(2).WithAttemptTimeout(TimeSpan.FromMilliseconds(50)).WithSleep(NoWait);
            int calls = 0;

            await Carom.ShotAsync(async ct =>
            {
                calls++;
                if (calls == 1) await Task.Delay(Timeout.Infinite, ct);
            }, bounce);

            Assert.Equal(2, calls);
        }

        [Fact]
        public async Task PerTryTimeout_EveryTryHangs_ThrowsTimeoutRejectedWithTheAttemptTimeout()
        {
            var attempt = TimeSpan.FromMilliseconds(40);
            var bounce = Bounce.Times(1).WithAttemptTimeout(attempt).WithSleep(NoWait);
            int calls = 0;

            var ex = await Assert.ThrowsAsync<TimeoutRejectedException>(() =>
                Carom.ShotAsync<int>(async ct =>
                {
                    calls++;
                    await Task.Delay(Timeout.Infinite, ct);
                    return 0;
                }, bounce));

            Assert.Equal(attempt, ex.Timeout);
            Assert.Equal(2, calls);
        }

        [Fact]
        public async Task PerTryTimeout_TypedBounce_RetriesAndSucceeds()
        {
            var bounce = Bounce.For<string>(2).WithAttemptTimeout(TimeSpan.FromMilliseconds(50)).WithSleep(NoWait);
            int calls = 0;

            var result = await Carom.ShotAsync<string>(async ct =>
            {
                calls++;
                if (calls == 1) await Task.Delay(Timeout.Infinite, ct);
                return "ok";
            }, bounce);

            Assert.Equal("ok", result);
        }

        [Fact]
        public async Task OverallTimeout_StillWins_OverALongerAttemptTimeout()
        {
            var overall = TimeSpan.FromMilliseconds(150);
            var bounce = Bounce.Times(5)
                .WithAttemptTimeout(TimeSpan.FromSeconds(30))
                .WithTimeout(overall)
                .WithSleep(NoWait);

            var ex = await Assert.ThrowsAsync<TimeoutRejectedException>(() =>
                Carom.ShotAsync<int>(async ct =>
                {
                    await Task.Delay(Timeout.Infinite, ct);
                    return 0;
                }, bounce));

            Assert.Equal(overall, ex.Timeout);
        }

        [Fact]
        public async Task CallerCancellation_IsNotTurnedIntoAnAttemptTimeout()
        {
            var bounce = Bounce.Times(3).WithAttemptTimeout(TimeSpan.FromSeconds(30)).WithSleep(NoWait);
            using var cts = new CancellationTokenSource();
            int calls = 0;

            var task = Carom.ShotAsync<int>(async ct =>
            {
                calls++;
                await Task.Delay(Timeout.Infinite, ct);
                return 0;
            }, bounce, cts.Token);
            cts.CancelAfter(50);

            var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);

            Assert.IsNotType<TimeoutRejectedException>(ex);
            Assert.Equal(1, calls);
        }

        [Fact]
        public void SyncShot_WithAnAttemptTimeout_Throws()
        {
            var bounce = Bounce.Times(1).WithAttemptTimeout(TimeSpan.FromSeconds(1));

            Assert.Throws<InvalidOperationException>(() => Carom.Shot(() => 1, bounce));
        }
    }
}
