// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Threading;
using System.Threading.Tasks;
using Carom.Extensions;
using Xunit;

namespace Carom.Extensions.Tests
{
    /// <summary>
    /// A caller that cancels while queued for a slot used to get CompartmentFullException,
    /// because TryEnterAsync turned the cancellation into "not entered". The compartment was
    /// not full; the caller gave up. Measured before the fix: CompartmentFullException and
    /// one OnBulkheadRejected signal for a cancelled wait.
    /// </summary>
    public class CompartmentCancellationTests
    {
        [Fact]
        public async Task Cancelling_while_queued_throws_cancellation_not_full()
        {
            var c = Compartment.ForResource("cancel-" + Guid.NewGuid())
                .WithMaxConcurrency(1)
                .WithQueueDepth(1)
                .Build();

            var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var holder = c.ExecuteAsync(() => release.Task);

            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => c.ExecuteAsync(() => Task.FromResult(1), cts.Token));
            Assert.Equal(cts.Token, ex.CancellationToken);

            release.SetResult(1);
            await holder;
        }

        [Fact]
        public async Task Cancelled_waiter_gives_its_queue_place_back()
        {
            var c = Compartment.ForResource("cancel-queue-" + Guid.NewGuid())
                .WithMaxConcurrency(1)
                .WithQueueDepth(1)
                .Build();

            var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var holder = c.ExecuteAsync(() => release.Task);

            using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50)))
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => c.ExecuteAsync(() => Task.FromResult(1), cts.Token));
            }

            // The only queue place is free again, so this caller waits instead of being shed.
            var queued = c.ExecuteAsync(() => Task.FromResult(2));
            Assert.False(queued.IsCompleted);

            release.SetResult(1);
            await holder;
            Assert.Equal(2, await queued);
        }

        [Fact]
        public async Task Already_cancelled_token_throws_cancellation_on_a_free_compartment()
        {
            var c = Compartment.ForResource("cancel-free-" + Guid.NewGuid())
                .WithMaxConcurrency(1)
                .Build();

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => c.ExecuteAsync(() => Task.FromResult(1), cts.Token));
        }
    }
}
