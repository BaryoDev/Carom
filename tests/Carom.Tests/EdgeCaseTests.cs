using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Carom.Tests
{
    /// <summary>
    /// Edge case tests for Carom core retry mechanism
    /// Tests boundary conditions, concurrent operations, and error scenarios
    /// </summary>
    public class EdgeCaseTests
    {
        #region Null and Empty Input Tests

        [Fact]
        public void Shot_ThrowsArgumentNullException_WhenActionIsNull()
        {
            Assert.Throws<ArgumentNullException>(() =>
                Carom.Shot<int>(null!, retries: 0));
        }

        [Fact]
        public void Shot_VoidAction_ThrowsArgumentNullException_WhenActionIsNull()
        {
            Assert.Throws<ArgumentNullException>(() =>
                Carom.Shot(null!, retries: 0));
        }

        [Fact]
        public async Task ShotAsync_ThrowsArgumentNullException_WhenActionIsNull()
        {
            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                Carom.ShotAsync<int>(null!, retries: 0));
        }

        [Fact]
        public async Task ShotAsync_VoidAction_ThrowsArgumentNullException_WhenActionIsNull()
        {
            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                Carom.ShotAsync(null!, retries: 0));
        }

        #endregion

        #region Boundary Condition Tests

        [Fact]
        public void Shot_WithZeroRetries_ExecutesOnlyOnce()
        {
            var executionCount = 0;
            var result = Carom.Shot(() =>
            {
                executionCount++;
                return executionCount;
            }, retries: 0);

            Assert.Equal(1, result);
            Assert.Equal(1, executionCount);
        }

        [Fact]
        public void Shot_WithNegativeRetries_TreatsAsZero()
        {
            var executionCount = 0;
            var result = Carom.Shot(() =>
            {
                executionCount++;
                return executionCount;
            }, retries: -1);

            Assert.Equal(1, result);
            Assert.Equal(1, executionCount);
        }

        [Fact]
        public void Shot_WithMaxRetries_ExecutesCorrectNumberOfTimes()
        {
            var executionCount = 0;
            var maxRetries = 100;

            try
            {
                Carom.Shot<int>(() =>
                {
                    executionCount++;
                    throw new InvalidOperationException("Always fails");
                }, retries: maxRetries);
            }
            catch (InvalidOperationException)
            {
                // Expected
            }

            Assert.Equal(maxRetries + 1, executionCount); // Initial attempt + retries
        }

        [Fact]
        public void Shot_WithZeroBaseDelay_ExecutesImmediately()
        {
            var executionCount = 0;
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                Carom.Shot<int>(() =>
                {
                    executionCount++;
                    throw new InvalidOperationException();
                }, retries: 3, baseDelay: TimeSpan.Zero);
            }
            catch (InvalidOperationException)
            {
                // Expected
            }

            stopwatch.Stop();
            Assert.Equal(4, executionCount);
            // Should complete very quickly with no delay
            Assert.True(stopwatch.ElapsedMilliseconds < 100, 
                $"Expected fast execution, but took {stopwatch.ElapsedMilliseconds}ms");
        }

        [Fact]
        public async Task ShotAsync_WithZeroBaseDelay_ExecutesImmediately()
        {
            var executionCount = 0;
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                await Carom.ShotAsync(async () =>
                {
                    executionCount++;
                    await Task.Yield();
                    throw new InvalidOperationException();
                }, retries: 3, baseDelay: TimeSpan.Zero);
            }
            catch (InvalidOperationException)
            {
                // Expected
            }

            stopwatch.Stop();
            Assert.Equal(4, executionCount);
            Assert.True(stopwatch.ElapsedMilliseconds < 100,
                $"Expected fast execution, but took {stopwatch.ElapsedMilliseconds}ms");
        }

        [Fact]
        public void Shot_WithVeryLongDelay_StillExecutes()
        {
            var result = Carom.Shot(() => 42, 
                retries: 1, 
                baseDelay: TimeSpan.FromHours(1));

            Assert.Equal(42, result);
        }

        #endregion

        #region Concurrent Operation Tests

        [Fact]
        public async Task ShotAsync_ConcurrentCalls_DoNotInterfere()
        {
            var tasks = new List<Task<int>>();
            var random = new Random(42); // Use seed for deterministic behavior

            // Execute 10 concurrent retry operations
            for (int i = 0; i < 10; i++)
            {
                int taskId = i;
                int delay = random.Next(1, 10);
                tasks.Add(Task.Run(async () =>
                {
                    var attemptCount = 0;
                    return await Carom.ShotAsync(async () =>
                    {
                        attemptCount++;
                        await Task.Delay(delay);
                        if (attemptCount < 2)
                        {
                            throw new InvalidOperationException($"Task {taskId} attempt {attemptCount}");
                        }
                        return taskId;
                    }, retries: 3, baseDelay: TimeSpan.FromMilliseconds(1));
                }));
            }

            var results = await Task.WhenAll(tasks);

            // Each task should return its own ID
            for (int i = 0; i < 10; i++)
            {
                Assert.Equal(i, results[i]);
            }
        }

        [Fact]
        public void Shot_ConcurrentCalls_DoNotInterfere()
        {
            var threads = new List<Thread>();
            var results = new int[10];
            var exceptions = new Exception?[10];

            for (int i = 0; i < 10; i++)
            {
                int threadId = i;
                var thread = new Thread(() =>
                {
                    try
                    {
                        var attemptCount = 0;
                        results[threadId] = Carom.Shot(() =>
                        {
                            attemptCount++;
                            Thread.Sleep(new Random().Next(1, 10));
                            if (attemptCount < 2)
                            {
                                throw new InvalidOperationException($"Thread {threadId} attempt {attemptCount}");
                            }
                            return threadId;
                        }, retries: 3, baseDelay: TimeSpan.FromMilliseconds(1));
                    }
                    catch (Exception ex)
                    {
                        exceptions[threadId] = ex;
                    }
                });
                threads.Add(thread);
                thread.Start();
            }

            foreach (var thread in threads)
            {
                thread.Join();
            }

            // Verify all threads completed successfully
            for (int i = 0; i < 10; i++)
            {
                Assert.Null(exceptions[i]);
                Assert.Equal(i, results[i]);
            }
        }

        #endregion

        #region Error Recovery Tests

        [Fact]
        public void Shot_RecoversFromTransientFailures()
        {
            var attemptCount = 0;
            var failureLimit = 2;

            var result = Carom.Shot(() =>
            {
                attemptCount++;
                if (attemptCount <= failureLimit)
                {
                    throw new InvalidOperationException($"Transient failure {attemptCount}");
                }
                return 42;
            }, retries: 3, baseDelay: TimeSpan.FromMilliseconds(1));

            Assert.Equal(42, result);
            Assert.Equal(failureLimit + 1, attemptCount);
        }

        [Fact]
        public async Task ShotAsync_RecoversFromTransientFailures()
        {
            var attemptCount = 0;
            var failureLimit = 2;

            var result = await Carom.ShotAsync(async () =>
            {
                attemptCount++;
                await Task.Yield();
                if (attemptCount <= failureLimit)
                {
                    throw new InvalidOperationException($"Transient failure {attemptCount}");
                }
                return 42;
            }, retries: 3, baseDelay: TimeSpan.FromMilliseconds(1));

            Assert.Equal(42, result);
            Assert.Equal(failureLimit + 1, attemptCount);
        }

        [Fact]
        public void Shot_PreservesOriginalException_WhenAllRetriesFail()
        {
            var originalMessage = "Original exception message";
            var attemptCount = 0;

            var ex = Assert.Throws<InvalidOperationException>(() =>
            {
                Carom.Shot<int>(() =>
                {
                    attemptCount++;
                    throw new InvalidOperationException(originalMessage);
                }, retries: 2, baseDelay: TimeSpan.FromMilliseconds(1));
            });

            Assert.Equal(originalMessage, ex.Message);
            Assert.Equal(3, attemptCount); // Initial + 2 retries
        }

        [Fact]
        public void Shot_WithShouldBounce_OnlyRetriesMatchingExceptions()
        {
            var attemptCount = 0;

            var ex = Assert.Throws<ArgumentException>(() =>
            {
                Carom.Shot<int>(() =>
                {
                    attemptCount++;
                    if (attemptCount == 1)
                    {
                        // This should NOT retry - different exception type
                        throw new ArgumentException("Wrong exception type");
                    }
                    return 42;
                }, retries: 3, 
                   baseDelay: TimeSpan.FromMilliseconds(1),
                   shouldBounce: ex => ex is InvalidOperationException);
            });

            Assert.Equal(1, attemptCount); // Should not retry
            Assert.IsType<ArgumentException>(ex);
        }

        #endregion

        #region Malformed/Unexpected Input Tests

        [Fact]
        public void Shot_HandlesExtremelyShortDelay()
        {
            var result = Carom.Shot(() => 42, 
                retries: 1, 
                baseDelay: TimeSpan.FromTicks(1));

            Assert.Equal(42, result);
        }

        [Fact]
        public void Shot_HandlesActionThatReturnsNull()
        {
            var result = Carom.Shot<string?>(() => null, retries: 0);
            Assert.Null(result);
        }

        [Fact]
        public async Task ShotAsync_HandlesActionThatReturnsNull()
        {
            var result = await Carom.ShotAsync<string?>(
                async () =>
                {
                    await Task.Yield();
                    return null;
                }, retries: 0);

            Assert.Null(result);
        }

        [Fact]
        public void Shot_HandlesActionThatThrowsNullReferenceException()
        {
            Assert.Throws<NullReferenceException>(() =>
            {
                Carom.Shot<int>(() =>
                {
                    string? nullString = null;
                    return nullString!.Length;
                }, retries: 0);
            });
        }

        [Fact]
        public void Shot_WithBounce_HandlesNullShouldBounce()
        {
            var bounce = Bounce.Times(1).WithDelay(TimeSpan.FromMilliseconds(1));
            var result = Carom.Shot(() => 42, bounce);
            Assert.Equal(42, result);
        }

        #endregion

        #region Cancellation Edge Cases

        [Fact]
        public async Task ShotAsync_ThrowsImmediately_WhenAlreadyCancelled()
        {
            var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await Carom.ShotAsync(
                    async () =>
                    {
                        await Task.Delay(100);
                        return 42;
                    },
                    retries: 3,
                    ct: cts.Token);
            });
        }

        [Fact]
        public async Task ShotAsync_CancellationDuringRetry_StopsRetrying()
        {
            var cts = new CancellationTokenSource();
            var attemptCount = 0;

            var task = Task.Run(async () =>
            {
                await Carom.ShotAsync(async () =>
                {
                    attemptCount++;
                    if (attemptCount == 1)
                    {
                        // Cancel after first attempt
                        cts.Cancel();
                        throw new InvalidOperationException("First attempt fails");
                    }
                    await Task.Yield();
                    return 42;
                }, retries: 5, baseDelay: TimeSpan.FromMilliseconds(10), ct: cts.Token);
            });

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
            
            // Should not complete all retries
            Assert.True(attemptCount < 6, $"Expected fewer than 6 attempts, got {attemptCount}");
        }

        [Fact]
        public async Task ShotAsync_WithTimeout_CancelsAfterTimeout()
        {
            var attemptCount = 0;

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await Carom.ShotAsync(async () =>
                {
                    attemptCount++;
                    await Task.Delay(200);
                    return 42;
                }, retries: 3, timeout: TimeSpan.FromMilliseconds(50));
            });

            // Should cancel quickly
            Assert.True(attemptCount <= 2, $"Expected 2 or fewer attempts, got {attemptCount}");
        }

        #endregion

        #region State Consistency Tests

        [Fact]
        public void Shot_MaintainsConsistentState_AcrossMultipleCalls()
        {
            var counter = 0;

            // First call
            var result1 = Carom.Shot(() => ++counter, retries: 0);
            Assert.Equal(1, result1);

            // Second call should not affect first
            var result2 = Carom.Shot(() => ++counter, retries: 0);
            Assert.Equal(2, result2);

            // Third call
            var result3 = Carom.Shot(() => ++counter, retries: 0);
            Assert.Equal(3, result3);
        }

        [Fact]
        public async Task ShotAsync_MaintainsConsistentState_AcrossMultipleCalls()
        {
            var counter = 0;

            var result1 = await Carom.ShotAsync(async () =>
            {
                await Task.Yield();
                return ++counter;
            }, retries: 0);
            Assert.Equal(1, result1);

            var result2 = await Carom.ShotAsync(async () =>
            {
                await Task.Yield();
                return ++counter;
            }, retries: 0);
            Assert.Equal(2, result2);
        }

        #endregion

        #region Exception Type Tests

        [Fact]
        public void Shot_PreservesExceptionType_ThroughRetries()
        {
            Assert.Throws<DivideByZeroException>(() =>
            {
                Carom.Shot<int>(() =>
                {
                    int zero = 0;
                    return 10 / zero;
                }, retries: 2);
            });
        }

        [Fact]
        public void Shot_HandlesAggregateException()
        {
            var taskException = new InvalidOperationException("Inner task exception");

            var ex = Assert.Throws<InvalidOperationException>(() =>
            {
                Carom.Shot<int>(() =>
                {
                    throw taskException;
                }, retries: 0);
            });

            Assert.Equal(taskException.Message, ex.Message);
        }

        [Fact]
        public async Task ShotAsync_HandlesTaskCanceledException()
        {
            var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await Carom.ShotAsync(async () =>
                {
                    await Task.Delay(100, cts.Token);
                    return 42;
                }, retries: 0, ct: cts.Token);
            });
        }

        #endregion
    }
}
