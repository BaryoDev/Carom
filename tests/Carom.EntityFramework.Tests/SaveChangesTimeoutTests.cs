// Copyright (c) BaryoDev. All rights reserved.
// Licensed under the MPL-2.0 license. See LICENSE file in the project root for full license information.

using Microsoft.EntityFrameworkCore;
using Xunit;
using Carom.EntityFramework;

namespace Carom.EntityFramework.Tests;

public class SaveChangesTimeoutTests
{
    // Blocks until its token fires, and records that it did.
    private sealed class HangingDbContext : DbContext
    {
        public readonly TaskCompletionSource<bool> Cancelled =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return 0;
            }
            catch (OperationCanceledException)
            {
                Cancelled.TrySetResult(true);
                throw;
            }
        }
    }

    [Fact]
    public async Task BounceTimeoutCancelsTheSaveItself()
    {
        using var context = new HangingDbContext();

        await Assert.ThrowsAsync<TimeoutRejectedException>(
            () => context.SaveChangesWithRetryAsync(Bounce.Times(0).WithTimeout(TimeSpan.FromMilliseconds(100))));

        // Without the attempt token the save never sees the timeout and keeps running.
        var finished = await Task.WhenAny(context.Cancelled.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(context.Cancelled.Task, finished);
    }

    [Fact]
    public async Task CallerCancellationStillReachesTheSave()
    {
        using var context = new HangingDbContext();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => context.SaveChangesWithRetryAsync(Bounce.Times(0), cts.Token));

        var finished = await Task.WhenAny(context.Cancelled.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(context.Cancelled.Task, finished);
    }
}
