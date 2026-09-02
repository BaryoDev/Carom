using Microsoft.EntityFrameworkCore;
using Xunit;
using Carom.EntityFramework;

namespace Carom.EntityFramework.Tests;

/// <summary>
/// Behavioural tests for the EF Core retry extensions. The context under test scripts
/// SaveChangesAsync directly, so no database provider is needed and every assertion is
/// on attempt counts and outcomes, never on elapsed time.
/// </summary>
public class DbContextRetryTests
{
    /// <summary>
    /// A DbContext whose SaveChangesAsync throws a scripted sequence of exceptions,
    /// then succeeds. Overriding the virtual means no provider is ever touched.
    /// </summary>
    private sealed class ScriptedDbContext : DbContext
    {
        private readonly Queue<Exception> _failures;
        private readonly int _result;

        public int Attempts { get; private set; }

        public ScriptedDbContext(int result, params Exception[] failures)
        {
            _result = result;
            _failures = new Queue<Exception>(failures);
        }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            Attempts++;
            if (_failures.Count > 0)
                throw _failures.Dequeue();
            return Task.FromResult(_result);
        }
    }

    [Fact]
    public async Task TransientFailureIsRetriedUntilItSucceeds()
    {
        using var context = new ScriptedDbContext(7, new Exception("connection was forcibly closed"));

        var written = await context.SaveChangesWithRetryAsync();

        Assert.Equal(7, written);
        Assert.Equal(2, context.Attempts);
    }

    [Fact]
    public async Task NonTransientFailureIsNotRetried()
    {
        var permanent = new InvalidOperationException("boom");
        using var context = new ScriptedDbContext(1, permanent);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => context.SaveChangesWithRetryAsync());

        Assert.Same(permanent, thrown);
        Assert.Equal(1, context.Attempts);
    }

    [Fact]
    public async Task RetryCountBoundsTheAttempts()
    {
        // retries: 2 means one initial attempt plus two retries, then the failure surfaces.
        using var context = new ScriptedDbContext(1,
            new Exception("deadlock victim"),
            new Exception("deadlock victim"),
            new Exception("deadlock victim"),
            new Exception("deadlock victim"));

        await Assert.ThrowsAsync<Exception>(() => context.SaveChangesWithRetryAsync(retries: 2));

        Assert.Equal(3, context.Attempts);
    }

    [Fact]
    public async Task DbUpdateExceptionWithTransientInnerIsRetried()
    {
        using var context = new ScriptedDbContext(3,
            new DbUpdateException("save failed", new Exception("network path was lost")));

        var written = await context.SaveChangesWithRetryAsync();

        Assert.Equal(3, written);
        Assert.Equal(2, context.Attempts);
    }

    [Fact]
    public async Task DbUpdateExceptionWithoutInnerIsNotRetried()
    {
        using var context = new ScriptedDbContext(1, new DbUpdateException("save failed"));

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesWithRetryAsync());

        Assert.Equal(1, context.Attempts);
    }

    [Fact]
    public async Task DbUpdateExceptionWithPermanentInnerIsNotRetried()
    {
        // A constraint violation must not be hammered against the database again.
        using var context = new ScriptedDbContext(1,
            new DbUpdateException("save failed", new Exception("unique constraint violated")));

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesWithRetryAsync());

        Assert.Equal(1, context.Attempts);
    }

    [Fact]
    public async Task OperationCanceledIsNeverRetriedEvenWhenItsMessageLooksTransient()
    {
        // 2.0 rule: cancellation stops the loop before the transient classifier is consulted.
        using var context = new ScriptedDbContext(1, new OperationCanceledException("connection timeout"));

        await Assert.ThrowsAsync<OperationCanceledException>(() => context.SaveChangesWithRetryAsync());

        Assert.Equal(1, context.Attempts);
    }

    [Fact]
    public async Task TimeoutRejectedIsNotRetriedBecauseTheClassifierMissesTimedOut()
    {
        // Pins current behaviour. Core lets TimeoutRejectedException through to shouldBounce,
        // but IsTransientError matches "timeout" and the message says "timed out", so the
        // package treats a timeout as permanent. Reported as a finding on issue #34.
        using var context = new ScriptedDbContext(1,
            new TimeoutRejectedException(TimeSpan.FromMilliseconds(50)));

        await Assert.ThrowsAsync<TimeoutRejectedException>(() => context.SaveChangesWithRetryAsync());

        Assert.Equal(1, context.Attempts);
    }

    [Theory]
    [InlineData("Connection Timeout expired")]
    [InlineData("chosen as the Deadlock victim")]
    [InlineData("the connection is broken")]
    [InlineData("a Network-related error occurred")]
    [InlineData("the transport channel failed")]
    public async Task EachTransientKeywordIsRecognisedCaseInsensitively(string message)
    {
        var attempts = 0;
        using var context = new ScriptedDbContext(1);

        var result = await context.ExecuteWithRetryAsync(() =>
        {
            attempts++;
            if (attempts == 1)
                throw new Exception(message);
            return Task.FromResult(42);
        });

        Assert.Equal(42, result);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task ExecuteWithRetryAsyncReturnsTheOperationResultWithoutRetryOnSuccess()
    {
        var attempts = 0;
        using var context = new ScriptedDbContext(1);

        var result = await context.ExecuteWithRetryAsync(() =>
        {
            attempts++;
            return Task.FromResult("ok");
        });

        Assert.Equal("ok", result);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task BounceOverloadUsesTheCallerPredicateNotTheTransientClassifier()
    {
        // With no predicate on the Bounce, core retries everything except cancellation,
        // so a permanent error is retried here where the int overload would rethrow it.
        using var context = new ScriptedDbContext(5,
            new InvalidOperationException("boom"),
            new InvalidOperationException("boom"));

        var written = await context.SaveChangesWithRetryAsync(
            Bounce.Times(2).WithDelay(TimeSpan.Zero));

        Assert.Equal(5, written);
        Assert.Equal(3, context.Attempts);
    }

    [Fact]
    public async Task BounceOverloadStillNeverRetriesCancellation()
    {
        using var context = new ScriptedDbContext(1, new OperationCanceledException());

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => context.SaveChangesWithRetryAsync(Bounce.Times(2).WithDelay(TimeSpan.Zero)));

        Assert.Equal(1, context.Attempts);
    }

    [Fact]
    public async Task AlreadyCancelledTokenStopsBeforeTheFirstAttempt()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        using var context = new ScriptedDbContext(1);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => context.SaveChangesWithRetryAsync(cancellationToken: cts.Token));

        Assert.Equal(0, context.Attempts);
    }
}
