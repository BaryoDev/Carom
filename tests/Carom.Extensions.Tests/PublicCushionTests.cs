// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Carom.Extensions;
using Xunit;

namespace Carom.Extensions.Tests
{
    /// <summary>
    /// Issue 45: the breaker works on its own, passes the attempt token, has a settable
    /// cap, can be reset, and can be driven by a public clock.
    /// </summary>
    [Collection("StoreEviction")]
    public class PublicCushionTests
    {
        private static string Key(string name) => name + "-" + Guid.NewGuid();

        private static Cushion Basic(string key, Func<long>? clock = null)
        {
            var b = Cushion.ForService(key).OpenAfter(failures: 1, trackingLast: 1);
            if (clock != null) b = b.WithClock(clock);
            return b.HalfOpenAfter(TimeSpan.FromSeconds(30));
        }

        [Fact]
        public async Task The_token_reaches_the_action_and_a_bounce_timeout_cancels_it()
        {
            var cushion = Basic(Key("token"));
            var bounce = Bounce.Times(0).WithTimeout(TimeSpan.FromMilliseconds(50));
            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            await Assert.ThrowsAsync<TimeoutRejectedException>(() =>
                CaromCushionExtensions.ShotAsync<int>(async ct =>
                {
                    try { await Task.Delay(TimeSpan.FromSeconds(30), ct); }
                    catch (OperationCanceledException) { cancelled.TrySetResult(true); throw; }
                    return 1;
                }, cushion, bounce));

            var done = await Task.WhenAny(cancelled.Task, Task.Delay(TimeSpan.FromSeconds(10)));
            Assert.Same(cancelled.Task, done);
        }

        [Fact]
        public async Task The_token_overload_with_retries_passes_the_caller_token()
        {
            var cushion = Basic(Key("token-retries"));
            using var cts = new CancellationTokenSource();
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var sawCancel = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            var call = CaromCushionExtensions.ShotAsync<int>(async ct =>
            {
                started.TrySetResult(true);
                try { await Task.Delay(TimeSpan.FromSeconds(30), ct); }
                catch (OperationCanceledException) { sawCancel.TrySetResult(true); throw; }
                return 1;
            }, cushion, retries: 0, ct: cts.Token);

            Assert.Same(started.Task, await Task.WhenAny(started.Task, Task.Delay(TimeSpan.FromSeconds(10))));
            cts.Cancel();
            Assert.Same(sawCancel.Task, await Task.WhenAny(sawCancel.Task, Task.Delay(TimeSpan.FromSeconds(10))));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        }

        [Fact]
        public async Task An_existing_call_that_ignores_the_token_still_compiles_and_runs()
        {
            var cushion = Basic(Key("no-token"));
            Func<Task<int>> noToken = () => Task.FromResult(3);
            Assert.Equal(3, await CaromCushionExtensions.ShotAsync(() => Task.FromResult(3), cushion));
            Assert.Equal(3, await CaromCushionExtensions.ShotAsync(noToken, cushion, Bounce.Times(0)));
        }

        [Fact]
        public async Task Public_ExecuteAsync_guards_a_call_with_no_bounce()
        {
            var key = Key("execute");
            var cushion = Basic(key);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                cushion.ExecuteAsync<int>(() => throw new InvalidOperationException("down")));
            Assert.Equal(CircuitState.Open, Cushion.GetState(key));
            await Assert.ThrowsAsync<CircuitOpenException>(() => cushion.ExecuteAsync(() => Task.FromResult(1)));
        }

        [Fact]
        public void Public_Execute_guards_a_synchronous_call()
        {
            var key = Key("execute-sync");
            var cushion = Basic(key);

            Assert.Equal(5, cushion.Execute(() => 5));
            Assert.Throws<InvalidOperationException>(() => cushion.Execute<int>(() => throw new InvalidOperationException()));
            Assert.Throws<CircuitOpenException>(() => cushion.Execute(() => 5));
        }

        [Fact]
        public void A_configured_cap_evicts_at_that_size()
        {
            var original = Cushion.MaxBreakers;
            var prefix = Key("cap");
            try
            {
                Cushion.MaxBreakers = 5;
                Assert.Equal(5, Cushion.MaxBreakers);
                for (int i = 0; i < 12; i++)
                    Basic(prefix + i).Execute(() => 1);

                Assert.True(CushionStore.Count <= 5, $"count {CushionStore.Count}");
                Assert.Null(Cushion.GetState(prefix + 0));
                Assert.NotNull(Cushion.GetState(prefix + 11));
            }
            finally
            {
                Cushion.MaxBreakers = original;
                for (int i = 0; i < 12; i++) Cushion.Reset(prefix + i);
            }
        }

        [Fact]
        public void The_cap_must_be_at_least_one()
        {
            var original = Cushion.MaxBreakers;
            try
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => Cushion.MaxBreakers = 0);
            }
            finally
            {
                Cushion.MaxBreakers = original;
            }
        }

        [Fact]
        public void Reset_closes_an_open_breaker()
        {
            var key = Key("reset");
            var cushion = Basic(key);
            Assert.Throws<InvalidOperationException>(() => cushion.Execute<int>(() => throw new InvalidOperationException()));
            Assert.Equal(CircuitState.Open, Cushion.GetState(key));

            Assert.True(Cushion.Reset(key));
            Assert.Null(Cushion.GetState(key));
            Assert.Equal(1, cushion.Execute(() => 1));
            Assert.Equal(CircuitState.Closed, Cushion.GetState(key));
            Assert.False(Cushion.Reset(Key("never-stored")));
        }

        [Fact]
        public void Half_open_is_reached_through_the_public_clock_with_no_sleep()
        {
            var key = Key("clock");
            long now = 100L * Stopwatch.Frequency;
            var cushion = Basic(key, () => now);

            Assert.Throws<InvalidOperationException>(() => cushion.Execute<int>(() => throw new InvalidOperationException()));
            Assert.Throws<CircuitOpenException>(() => cushion.Execute(() => 1));

            now += 31L * Stopwatch.Frequency;
            Assert.Equal(1, cushion.Execute(() => 1));
            Assert.Equal(CircuitState.Closed, Cushion.GetState(key));
        }

        [Fact]
        public void The_conflict_rule_is_unchanged_one_key_one_setting()
        {
            var key = Key("conflict");
            Cushion.ForService(key).OpenAfter(1, 1).HalfOpenAfter(TimeSpan.FromSeconds(30)).Execute(() => 1);
            var other = Cushion.ForService(key).OpenAfter(2, 2).HalfOpenAfter(TimeSpan.FromSeconds(30));
            Assert.Throws<InvalidOperationException>(() => other.Execute(() => 1));
            Cushion.Reset(key);
            Assert.Equal(1, other.Execute(() => 1));
        }

        [Fact]
        public async Task A_null_action_throws_ArgumentNullException_and_does_not_open_the_breaker()
        {
            var key = Key("null-action");
            var cushion = Basic(key);

            Assert.Throws<ArgumentNullException>(() => cushion.Execute<int>(null!));
            await Assert.ThrowsAsync<ArgumentNullException>(() => cushion.ExecuteAsync<int>((Func<Task<int>>)null!));
            Assert.NotEqual(CircuitState.Open, Cushion.GetState(key));
        }

        [Fact]
        public void The_first_registration_clock_wins_until_Reset()
        {
            var key = Key("clock-first");
            long real = Stopwatch.GetTimestamp();
            long fake = real;
            var first = Basic(key, () => real);
            var second = Basic(key, () => fake);

            Assert.Throws<InvalidOperationException>(() => first.Execute<int>(() => throw new InvalidOperationException()));
            fake += 31L * Stopwatch.Frequency;
            // The second cushion's fake clock is ignored: the circuit is still open.
            Assert.Throws<CircuitOpenException>(() => second.Execute(() => 1));

            Cushion.Reset(key);
            Assert.Throws<InvalidOperationException>(() => second.Execute<int>(() => throw new InvalidOperationException()));
            fake += 31L * Stopwatch.Frequency;
            Assert.Equal(1, second.Execute(() => 1));
        }
    }
}
