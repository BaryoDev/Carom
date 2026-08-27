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
    /// Issue #3: WithQueueDepth was stored, validated, compared on conflicting
    /// registration, and never read on any execution path. A queue depth now bounds
    /// how many callers may wait for a slot; anything past the bound is shed
    /// immediately, because an unbounded wait queue is the exact failure a bulkhead
    /// exists to prevent.
    /// </summary>
    public class CompartmentQueueDepthTests
    {
        [Fact]
        public async Task QueueDepth_bounds_waiters_and_sheds_beyond_it()
        {
            var c = Compartment.ForResource("queue-" + Guid.NewGuid())
                .WithMaxConcurrency(1)
                .WithQueueDepth(2)
                .Build();

            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var holderIn = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            var holder = CaromCompartmentExtensions.ShotAsync(async () =>
            {
                holderIn.SetResult(true);
                await release.Task;
                return 0;
            }, c, retries: 0);

            await holderIn.Task; // the single slot is definitely occupied

            // Two callers may queue. The queue-slot reservation happens synchronously
            // inside the call, before the first await, so these hold their places by
            // the time the call returns its task.
            var q1 = CaromCompartmentExtensions.ShotAsync(async () => { await Task.Yield(); return 1; }, c, retries: 0);
            var q2 = CaromCompartmentExtensions.ShotAsync(async () => { await Task.Yield(); return 2; }, c, retries: 0);

            // The third must be shed immediately, not queued.
            await Assert.ThrowsAsync<CompartmentFullException>(() =>
                CaromCompartmentExtensions.ShotAsync(async () => { await Task.Yield(); return 3; }, c, retries: 0));

            release.SetResult(true);
            var results = await Task.WhenAll(holder, q1, q2); // the two queued callers do complete
            Assert.Equal(new[] { 0, 1, 2 }, results);
        }

        [Fact]
        public async Task Zero_queue_depth_still_sheds_immediately()
        {
            var c = Compartment.ForResource("queue-" + Guid.NewGuid())
                .WithMaxConcurrency(1)
                .WithQueueDepth(0)
                .Build();

            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var holderIn = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            var holder = CaromCompartmentExtensions.ShotAsync(async () =>
            {
                holderIn.SetResult(true);
                await release.Task;
                return 0;
            }, c, retries: 0);

            await holderIn.Task;

            var sw = Stopwatch.StartNew();
            await Assert.ThrowsAsync<CompartmentFullException>(() =>
                CaromCompartmentExtensions.ShotAsync(async () => { await Task.Yield(); return 1; }, c, retries: 0));
            sw.Stop();

            Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(500),
                $"a zero-depth compartment must not wait; spent {sw.ElapsedMilliseconds}ms");

            release.SetResult(true);
            await holder;
        }

        [Fact]
        public void Sync_path_queues_within_the_depth_and_sheds_beyond_it()
        {
            var key = "queue-sync-" + Guid.NewGuid();
            var c = Compartment.ForResource(key)
                .WithMaxConcurrency(1)
                .WithQueueDepth(1)
                .Build();

            using var holderIn = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            int holderResult = -1, queuedResult = -1;
            Exception? holderEx = null, queuedEx = null;

            // Dedicated threads, not Task.Run: these calls block, and the shared pool
            // can be saturated by the stress tests running in parallel with this one.
            // Blocking a starved pool from an xunit worker stalls the whole run. The
            // catch matters too: an unhandled exception on a bare thread aborts the
            // whole test host instead of failing this test.
            var holder = new Thread(() =>
            {
                try
                {
                    holderResult = CaromCompartmentExtensions.Shot(() =>
                    {
                        holderIn.Set();
                        release.Wait();
                        return 0;
                    }, c, retries: 0);
                }
                catch (Exception ex) { holderEx = ex; holderIn.Set(); }
            }) { IsBackground = true };
            holder.Start();
            Assert.True(holderIn.Wait(TimeSpan.FromSeconds(10)), "holder never entered the compartment");
            Assert.True(holderEx is null, $"holder threw: {holderEx}");

            var queued = new Thread(() =>
            {
                try { queuedResult = CaromCompartmentExtensions.Shot(() => 1, c, retries: 0); }
                catch (Exception ex) { queuedEx = ex; }
            }) { IsBackground = true };
            queued.Start();

            // Wait until the queued caller holds its queue slot, with a deadline so a
            // regression fails rather than hangs.
            var state = CompartmentStore.GetOrCreate(key, c);
            var deadline = Stopwatch.StartNew();
            while (state.QueuedCount < 1)
            {
                Assert.True(queuedEx is null, $"queued caller was shed instead of queueing: {queuedEx}");
                Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(10), "queued caller never took its queue slot");
                Thread.Yield();
            }

            // Queue of 1 is full: the next sync caller is shed immediately.
            Assert.Throws<CompartmentFullException>(() =>
                CaromCompartmentExtensions.Shot(() => 2, c, retries: 0));

            release.Set();
            Assert.True(holder.Join(TimeSpan.FromSeconds(10)), "holder did not finish");
            Assert.True(queued.Join(TimeSpan.FromSeconds(10)), "queued caller did not finish");
            Assert.Equal(0, holderResult);
            Assert.Equal(1, queuedResult);
        }
    }
}
