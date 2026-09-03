using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace Carom.Tests
{
    /// <summary>
    /// Tests for the CaromHooks retry seam. Hooks are process-wide mutable state, so:
    /// every test saves the prior hook and restores it in a finally block, all
    /// hook-mutating tests live in this one class so xunit serializes them, and
    /// subscribers filter by calling thread id (sync raises run on the caller's
    /// thread) or by a marker exception type no other test throws, so retries fired
    /// by tests running in parallel cannot leak into these assertions.
    /// </summary>
    public class CaromHooksTests
    {
        private sealed class CaromHooksProbeException : Exception
        {
        }

        [Fact]
        public void RetrySignal_CarriesAttemptNumberAndDelay()
        {
            var prior = CaromHooks.OnRetry;
            try
            {
                var threadId = Environment.CurrentManagedThreadId;
                var signals = new List<RetrySignal>();
                CaromHooks.OnRetry = s =>
                {
                    if (Environment.CurrentManagedThreadId == threadId) signals.Add(s);
                };

                int calls = 0;
                var result = Carom.Shot(
                    () =>
                    {
                        calls++;
                        if (calls <= 2) throw new CaromHooksProbeException();
                        return 7;
                    },
                    retries: 3,
                    baseDelay: TimeSpan.FromMilliseconds(1),
                    shouldBounce: null,
                    disableJitter: true);

                Assert.Equal(7, result);
                Assert.Equal(2, signals.Count);
                Assert.Equal(1, signals[0].Attempt);
                Assert.Equal(2, signals[1].Attempt);
                // Fixed backoff with jitter disabled: base * 2^attempt.
                Assert.Equal(TimeSpan.FromMilliseconds(2), signals[0].Delay);
                Assert.Equal(TimeSpan.FromMilliseconds(4), signals[1].Delay);
                Assert.Equal(nameof(CaromHooksProbeException), signals[0].ExceptionTypeName);
                Assert.Equal(nameof(CaromHooksProbeException), signals[1].ExceptionTypeName);
            }
            finally
            {
                CaromHooks.OnRetry = prior;
            }
        }

        [Fact]
        public void RetrySignal_NotRaisedOnSuccessfulFirstAttempt()
        {
            var prior = CaromHooks.OnRetry;
            try
            {
                var threadId = Environment.CurrentManagedThreadId;
                var signals = new List<RetrySignal>();
                CaromHooks.OnRetry = s =>
                {
                    if (Environment.CurrentManagedThreadId == threadId) signals.Add(s);
                };

                for (int i = 0; i < 100; i++)
                {
                    Assert.Equal(7, Carom.Shot(static () => 7));
                }

                Assert.Empty(signals);
            }
            finally
            {
                CaromHooks.OnRetry = prior;
            }
        }

        [Fact]
        public void RetrySignal_RaisedOnResultRetry_WithNullExceptionTypeName()
        {
            var prior = CaromHooks.OnRetry;
            try
            {
                var threadId = Environment.CurrentManagedThreadId;
                var signals = new List<RetrySignal>();
                CaromHooks.OnRetry = s =>
                {
                    if (Environment.CurrentManagedThreadId == threadId) signals.Add(s);
                };

                int calls = 0;
                var result = Carom.Shot(
                    () => ++calls,
                    retries: 3,
                    baseDelay: TimeSpan.Zero,
                    shouldBounce: null,
                    shouldRetryResult: static r => r < 2,
                    disableJitter: true);

                Assert.Equal(2, result);
                var signal = Assert.Single(signals);
                Assert.Equal(1, signal.Attempt);
                Assert.Null(signal.ExceptionTypeName);
            }
            finally
            {
                CaromHooks.OnRetry = prior;
            }
        }

        [Fact]
        public async Task RetrySignal_RaisedOnAsyncRetry()
        {
            var prior = CaromHooks.OnRetry;
            try
            {
                // Async continuations hop threads, so filter by the marker exception
                // type instead of thread id. No other test throws it.
                var signals = new List<RetrySignal>();
                CaromHooks.OnRetry = s =>
                {
                    if (s.ExceptionTypeName == nameof(CaromHooksProbeException))
                    {
                        lock (signals) signals.Add(s);
                    }
                };

                int calls = 0;
                var result = await Carom.ShotAsync(
                    () =>
                    {
                        calls++;
                        if (calls <= 2) throw new CaromHooksProbeException();
                        return Task.FromResult(7);
                    },
                    retries: 3,
                    baseDelay: TimeSpan.FromMilliseconds(1),
                    timeout: null,
                    shouldBounce: null,
                    disableJitter: true);

                Assert.Equal(7, result);
                Assert.Equal(2, signals.Count);
                Assert.Equal(1, signals[0].Attempt);
                Assert.Equal(2, signals[1].Attempt);
            }
            finally
            {
                CaromHooks.OnRetry = prior;
            }
        }

        [Fact]
        public void RetryPath_WithNoSubscriber_AllocatesZeroBytes()
        {
            var prior = CaromHooks.OnRetry;
            try
            {
                CaromHooks.OnRetry = null;

                int calls = 0;
                // Closure and delegates are created once, before measurement.
                Func<int> action = () => calls++;
                Func<int, bool> retryIfEven = static r => (r & 1) == 0;

                // Each Shot retries exactly once: first result is even, second is odd.
                for (int i = 0; i < 10_000; i++)
                {
                    Carom.Shot(action, retries: 1, baseDelay: TimeSpan.Zero,
                        shouldBounce: null, shouldRetryResult: retryIfEven, disableJitter: true);
                }

                long before = GC.GetAllocatedBytesForCurrentThread();

                for (int i = 0; i < 10_000; i++)
                {
                    Carom.Shot(action, retries: 1, baseDelay: TimeSpan.Zero,
                        shouldBounce: null, shouldRetryResult: retryIfEven, disableJitter: true);
                }

                long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

                Assert.True(allocated == 0,
                    $"The retry path allocated {allocated} bytes over 10,000 calls with no hook subscriber. " +
                    "The seam must not compute or allocate anything when CaromHooks.OnRetry is null.");
            }
            finally
            {
                CaromHooks.OnRetry = prior;
            }
        }
        [Fact]
        public void AThrowingHook_DoesNotBreakTheRetryItObserves()
        {
            var prior = CaromHooks.OnRetry;
            try
            {
                var threadId = Environment.CurrentManagedThreadId;
                CaromHooks.OnRetry = _ =>
                {
                    if (Environment.CurrentManagedThreadId == threadId)
                        throw new InvalidOperationException("subscriber is broken");
                };

                int calls = 0;
                var thrown = Assert.Throws<CaromHooksProbeException>(() =>
                    Carom.Shot<int>(
                        () => { calls++; throw new CaromHooksProbeException(); },
                        retries: 3,
                        baseDelay: TimeSpan.FromMilliseconds(1),
                        shouldBounce: null,
                        disableJitter: true));

                // Unguarded, the subscriber's exception escapes the catch block: the caller
                // sees InvalidOperationException and only one attempt ever runs.
                Assert.IsType<CaromHooksProbeException>(thrown);
                Assert.Equal(4, calls);
            }
            finally
            {
                CaromHooks.OnRetry = prior;
            }
        }

        [Fact]
        public async Task AThrowingHook_DoesNotBreakTheAsyncRetryItObserves()
        {
            var prior = CaromHooks.OnRetry;
            try
            {
                CaromHooks.OnRetry = s =>
                {
                    if (s.ExceptionTypeName == nameof(CaromHooksProbeException))
                        throw new InvalidOperationException("subscriber is broken");
                };

                int calls = 0;
                var thrown = await Assert.ThrowsAsync<CaromHooksProbeException>(() =>
                    Carom.ShotAsync<int>(
                        () => { calls++; throw new CaromHooksProbeException(); },
                        retries: 2,
                        baseDelay: TimeSpan.FromMilliseconds(1),
                        timeout: null,
                        shouldBounce: null,
                        disableJitter: true));

                // The exception type alone would pass if the loop stopped retrying, so count
                // the attempts too: retries 2 means three executions.
                Assert.IsType<CaromHooksProbeException>(thrown);
                Assert.Equal(3, calls);
            }
            finally
            {
                CaromHooks.OnRetry = prior;
            }
        }

    }
}
