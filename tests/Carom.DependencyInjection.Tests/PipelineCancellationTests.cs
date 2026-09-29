// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Threading;
using System.Threading.Tasks;
using Carom.DependencyInjection;
using Xunit;

namespace Carom.DependencyInjection.Tests
{
    /// <summary>
    /// Two token defects in the pipeline strategies, both measured before the fix.
    /// The retry strategy handed the action the caller's token, so a Bounce timeout left the
    /// action running with a token that never fired. The timeout strategy turned any
    /// OperationCanceledException into TimeoutException, including one the action raised for
    /// its own reasons, and reported it as "timed out after 10000ms" straight away.
    /// </summary>
    public class PipelineCancellationTests
    {
        [Fact]
        public async Task Retry_timeout_cancels_the_token_the_action_receives()
        {
            var pipeline = new ResiliencePipelineBuilder("retry-timeout")
                .AddRetry(Bounce.Times(0).WithTimeout(TimeSpan.FromMilliseconds(100)))
                .Build();

            var observed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            await Assert.ThrowsAsync<TimeoutRejectedException>(() => pipeline.ExecuteAsync(async token =>
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, token);
                }
                catch (OperationCanceledException)
                {
                    observed.TrySetResult(true);
                    throw;
                }
                observed.TrySetResult(false);
                return 1;
            }));

            var winner = await Task.WhenAny(observed.Task, Task.Delay(TimeSpan.FromSeconds(30)));
            Assert.Same(observed.Task, winner);
            Assert.True(await observed.Task);
        }

        [Fact]
        public async Task Timeout_strategy_passes_through_a_cancellation_it_did_not_cause()
        {
            var pipeline = new ResiliencePipelineBuilder("timeout-foreign-oce")
                .AddTimeout(TimeSpan.FromSeconds(10))
                .Build();

            var ex = await Assert.ThrowsAsync<TaskCanceledException>(() =>
                pipeline.ExecuteAsync<int>(_ => throw new TaskCanceledException("inner client timeout")));

            Assert.Equal("inner client timeout", ex.Message);
        }

        [Fact]
        public async Task Timeout_strategy_still_reports_its_own_deadline()
        {
            var pipeline = new ResiliencePipelineBuilder("timeout-own")
                .AddTimeout(TimeSpan.FromMilliseconds(100))
                .Build();

            await Assert.ThrowsAsync<TimeoutException>(() =>
                pipeline.ExecuteAsync(async token =>
                {
                    await Task.Delay(Timeout.Infinite, token);
                    return 1;
                }));
        }
    }
}
