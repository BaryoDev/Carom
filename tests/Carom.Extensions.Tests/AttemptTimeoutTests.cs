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
    }
}
