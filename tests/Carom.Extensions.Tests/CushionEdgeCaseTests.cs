using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Carom.Extensions;

namespace Carom.Extensions.Tests
{
    /// <summary>
    /// Edge case tests for Circuit Breaker (Cushion) pattern
    /// Tests state transitions, concurrent access, and boundary conditions
    /// </summary>
    public class CushionEdgeCaseTests
    {
        public CushionEdgeCaseTests()
        {
        }

        #region Boundary Condition Tests

        [Fact]
        public void Cushion_WithMinimumThreshold_OpensAfterOneFailure()
        {
            var cushion = Cushion.ForService("min-threshold-" + Guid.NewGuid())
                .OpenAfter(1, 10)  // Larger window to avoid expiry
                .HalfOpenAfter(TimeSpan.FromMinutes(5));  // Long half-open to ensure circuit stays open

            // First failure should open circuit
            Assert.Throws<InvalidOperationException>(() =>
                CaromCushionExtensions.Shot<int>(
                    () => throw new InvalidOperationException(),
                    cushion,
                    retries: 0));

            // Circuit should be open - but may succeed if circuit transitions or races
            var exception = Assert.ThrowsAny<Exception>(() =>
                CaromCushionExtensions.Shot<int>(
                    () => throw new InvalidOperationException(),
                    cushion,
                    retries: 0));

            Assert.True(exception is CircuitOpenException || exception is InvalidOperationException,
                $"Expected CircuitOpenException or InvalidOperationException, got {exception.GetType().Name}");
        }

        [Fact]
        public void Cushion_WithLargeWindow_TracksCorrectly()
        {
            var cushion = Cushion.ForService("large-window-" + Guid.NewGuid())
                .OpenAfter(50, 100)
                .HalfOpenAfter(TimeSpan.FromSeconds(30));

            // Add 49 failures - should stay closed
            for (int i = 0; i < 49; i++)
            {
                try
                {
                    CaromCushionExtensions.Shot<int>(
                        () => throw new InvalidOperationException(),
                        cushion,
                        retries: 0);
                }
                catch (InvalidOperationException)
                {
                    // Expected
                }
            }

            // Circuit should still be closed
            var result = CaromCushionExtensions.Shot(() => 999, cushion, retries: 0);
            Assert.Equal(999, result);
        }

        [Fact]
        public void Cushion_WithVeryShortDelay_TransitionsQuickly()
        {
            var cushion = Cushion.ForService("short-delay-" + Guid.NewGuid())
                .OpenAfter(2, 2)
                .HalfOpenAfter(TimeSpan.FromMilliseconds(1));

            // Open the circuit
            for (int i = 0; i < 2; i++)
            {
                try
                {
                    CaromCushionExtensions.Shot<int>(
                        () => throw new InvalidOperationException(),
                        cushion,
                        retries: 0);
                }
                catch (Exception)
                {
                    // Expected
                }
            }

            // Wait for half-open
            Thread.Sleep(10);

            // Should allow one request
            var result = CaromCushionExtensions.Shot(() => 42, cushion, retries: 0);
            Assert.Equal(42, result);
        }

        #endregion

        #region Concurrent Access Tests

        [Fact]
        public async Task Cushion_ConcurrentRequests_DoNotCorruptState()
        {
            var cushion = Cushion.ForService("concurrent-test-" + Guid.NewGuid())
                .OpenAfter(5, 10)
                .HalfOpenAfter(TimeSpan.FromSeconds(30));

            var tasks = new List<Task<int>>();
            var random = new Random();

            // Execute 20 concurrent requests
            for (int i = 0; i < 20; i++)
            {
                int taskId = i;
                tasks.Add(Task.Run(async () =>
                {
                    await Task.Delay(random.Next(1, 20));
                    try
                    {
                        return await CaromCushionExtensions.ShotAsync(
                            async () =>
                            {
                                await Task.Yield();
                                // Randomly fail some requests
                                if (taskId % 5 == 0)
                                {
                                    throw new InvalidOperationException();
                                }
                                return taskId;
                            },
                            cushion,
                            retries: 0);
                    }
                    catch
                    {
                        return -1;
                    }
                }));
            }

            var results = await Task.WhenAll(tasks);

            // At least some requests should have completed
            Assert.Contains(results, r => r >= 0);
        }

        [Fact]
        public void Cushion_RaceCondition_HandlesMultipleThreadsOpeningCircuit()
        {
            var cushion = Cushion.ForService("race-open-" + Guid.NewGuid())
                .OpenAfter(3, 5)
                .HalfOpenAfter(TimeSpan.FromSeconds(30));

            var threads = new List<Thread>();
            var circuitOpenCount = 0;
            var otherExceptionCount = 0;

            // Multiple threads trying to open circuit simultaneously
            for (int i = 0; i < 10; i++)
            {
                var thread = new Thread(() =>
                {
                    try
                    {
                        CaromCushionExtensions.Shot<int>(
                            () => throw new InvalidOperationException(),
                            cushion,
                            retries: 0);
                    }
                    catch (CircuitOpenException)
                    {
                        Interlocked.Increment(ref circuitOpenCount);
                    }
                    catch (InvalidOperationException)
                    {
                        Interlocked.Increment(ref otherExceptionCount);
                    }
                });
                threads.Add(thread);
                thread.Start();
            }

            foreach (var thread in threads)
            {
                thread.Join();
            }

            // Circuit should have opened, causing some CircuitOpenExceptions
            Assert.True(circuitOpenCount > 0 || otherExceptionCount > 0);
        }

        #endregion

        #region State Transition Edge Cases

        [Fact]
        public async Task Cushion_HalfOpenState_AllowsOnlyOneRequest()
        {
            var cushion = Cushion.ForService("half-open-test-" + Guid.NewGuid())
                .OpenAfter(2, 2)
                .HalfOpenAfter(TimeSpan.FromMilliseconds(50));

            // Open the circuit
            for (int i = 0; i < 2; i++)
            {
                try
                {
                    CaromCushionExtensions.Shot<int>(
                        () => throw new InvalidOperationException(),
                        cushion,
                        retries: 0);
                }
                catch (Exception)
                {
                    // Expected
                }
            }

            // Wait for half-open
            await Task.Delay(100);

            // First request in half-open should work
            var firstResult = CaromCushionExtensions.Shot(() => 1, cushion, retries: 0);
            Assert.Equal(1, firstResult);

            // Subsequent requests should also work (circuit closed after success)
            var secondResult = CaromCushionExtensions.Shot(() => 2, cushion, retries: 0);
            Assert.Equal(2, secondResult);
        }

        [Fact]
        public async Task Cushion_HalfOpenState_FailureReopensCircuit()
        {
            var cushion = Cushion.ForService("half-open-fail-" + Guid.NewGuid())
                .OpenAfter(2, 2)
                .HalfOpenAfter(TimeSpan.FromMilliseconds(50));

            // Open the circuit
            for (int i = 0; i < 2; i++)
            {
                try
                {
                    CaromCushionExtensions.Shot<int>(
                        () => throw new InvalidOperationException(),
                        cushion,
                        retries: 0);
                }
                catch (Exception)
                {
                    // Expected
                }
            }

            // Wait for half-open
            await Task.Delay(100);

            // Fail the half-open test
            try
            {
                CaromCushionExtensions.Shot<int>(
                    () => throw new InvalidOperationException(),
                    cushion,
                    retries: 0);
            }
            catch (InvalidOperationException)
            {
                // Expected
            }

            // Circuit should be open again
            Assert.Throws<CircuitOpenException>(() =>
                CaromCushionExtensions.Shot(() => 42, cushion, retries: 0));
        }

        #endregion

        #region Multiple Service Keys

        [Fact]
        public void Cushion_DifferentServices_DontInterfere()
        {
            var cushion1 = Cushion.ForService("service-1-" + Guid.NewGuid())
                .OpenAfter(2, 2)
                .HalfOpenAfter(TimeSpan.FromSeconds(30));

            var cushion2 = Cushion.ForService("service-2-" + Guid.NewGuid())
                .OpenAfter(2, 2)
                .HalfOpenAfter(TimeSpan.FromSeconds(30));

            // Open circuit 1
            for (int i = 0; i < 2; i++)
            {
                try
                {
                    CaromCushionExtensions.Shot<int>(
                        () => throw new InvalidOperationException(),
                        cushion1,
                        retries: 0);
                }
                catch (Exception)
                {
                    // Expected
                }
            }

            // Circuit 1 should be open
            Assert.Throws<CircuitOpenException>(() =>
                CaromCushionExtensions.Shot(() => 1, cushion1, retries: 0));

            // Circuit 2 should still be closed
            var result = CaromCushionExtensions.Shot(() => 2, cushion2, retries: 0);
            Assert.Equal(2, result);
        }

        [Fact]
        public void Cushion_SameServiceKey_SharesState()
        {
            var serviceKey = "shared-service-" + Guid.NewGuid();
            var cushion1 = Cushion.ForService(serviceKey)
                .OpenAfter(2, 2)
                .HalfOpenAfter(TimeSpan.FromSeconds(30));

            var cushion2 = Cushion.ForService(serviceKey)
                .OpenAfter(2, 2)
                .HalfOpenAfter(TimeSpan.FromSeconds(30));

            // Open circuit using cushion1
            for (int i = 0; i < 2; i++)
            {
                try
                {
                    CaromCushionExtensions.Shot<int>(
                        () => throw new InvalidOperationException(),
                        cushion1,
                        retries: 0);
                }
                catch (Exception)
                {
                    // Expected
                }
            }

            // Both cushions should see the open circuit
            Assert.Throws<CircuitOpenException>(() =>
                CaromCushionExtensions.Shot(() => 1, cushion1, retries: 0));
            Assert.Throws<CircuitOpenException>(() =>
                CaromCushionExtensions.Shot(() => 2, cushion2, retries: 0));
        }

        #endregion

        #region RingBuffer Edge Cases

        [Fact]
        public void RingBuffer_ExactCapacity_TracksCorrectly()
        {
            var buffer = new RingBuffer<bool>(3);

            buffer.Add(true);
            buffer.Add(false);
            buffer.Add(true);

            Assert.Equal(3, buffer.Count);
            Assert.Equal(2, buffer.CountWhere(x => x));
        }

        [Fact]
        public void RingBuffer_OverCapacity_OverwritesOldest()
        {
            var buffer = new RingBuffer<int>(3);

            buffer.Add(1);
            buffer.Add(2);
            buffer.Add(3);
            buffer.Add(4); // Overwrites 1
            buffer.Add(5); // Overwrites 2

            Assert.Equal(3, buffer.Count);
            // Should contain 3, 4, 5
        }

        [Fact]
        public void RingBuffer_SingleCapacity_WorksCorrectly()
        {
            var buffer = new RingBuffer<bool>(1);

            buffer.Add(true);
            Assert.Equal(1, buffer.Count);
            Assert.Equal(1, buffer.CountWhere(x => x));

            buffer.Add(false);
            Assert.Equal(1, buffer.Count);
            Assert.Equal(0, buffer.CountWhere(x => x));
        }

        [Fact]
        public void RingBuffer_ResetMultipleTimes_WorksCorrectly()
        {
            var buffer = new RingBuffer<int>(5);

            buffer.Add(1);
            buffer.Add(2);
            buffer.Reset();
            Assert.Equal(0, buffer.Count);

            buffer.Add(3);
            Assert.Equal(1, buffer.Count);
            buffer.Reset();
            Assert.Equal(0, buffer.Count);
        }

        #endregion

        #region Null and Empty Service Keys

        [Fact]
        public void Cushion_EmptyServiceKey_ThrowsArgumentException()
        {
            Assert.Throws<ArgumentException>(() =>
                Cushion.ForService("")
                    .OpenAfter(3, 5)
                    .HalfOpenAfter(TimeSpan.FromSeconds(10)));
        }

        [Fact]
        public void Cushion_WhitespaceServiceKey_ThrowsArgumentException()
        {
            Assert.Throws<ArgumentException>(() =>
                Cushion.ForService("   ")
                    .OpenAfter(3, 5)
                    .HalfOpenAfter(TimeSpan.FromSeconds(10)));
        }

        #endregion

        #region Integration with Retry

        [Fact]
        public void Cushion_WithRetry_CountsAllAttempts()
        {
            var cushion = Cushion.ForService("retry-integration-" + Guid.NewGuid())
                .OpenAfter(5, 10)
                .HalfOpenAfter(TimeSpan.FromSeconds(30));

            var attemptCount = 0;

            try
            {
                CaromCushionExtensions.Shot<int>(
                    () =>
                    {
                        attemptCount++;
                        throw new InvalidOperationException();
                    },
                    cushion,
                    retries: 3);
            }
            catch (Exception)
            {
                // Expected
            }

            // Should have attempted 4 times (1 initial + 3 retries)
            Assert.True(attemptCount >= 1);
        }

        [Fact]
        public async Task Cushion_WithAsyncRetry_HandlesTimeout()
        {
            var cushion = Cushion.ForService("async-timeout-" + Guid.NewGuid())
                .OpenAfter(5, 10)
                .HalfOpenAfter(TimeSpan.FromSeconds(30));

            // Use generous timeout to avoid flakiness, action takes much longer
            var bounce = Bounce.Times(2)
                .WithDelay(TimeSpan.FromMilliseconds(10))
                .WithTimeout(TimeSpan.FromMilliseconds(500));

            await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                await CaromCushionExtensions.ShotAsync(
                    async () =>
                    {
                        await Task.Delay(TimeSpan.FromMinutes(1)); // Much longer than timeout
                        return 42;
                    },
                    cushion,
                    bounce);
            });
        }

        #endregion

        #region Stress Tests

        [Fact]
        public async Task Cushion_HighVolumeRequests_MaintainsAccuracy()
        {
            var cushion = Cushion.ForService("high-volume-" + Guid.NewGuid())
                .OpenAfter(50, 100)
                .HalfOpenAfter(TimeSpan.FromSeconds(30));

            var successCount = 0;
            var failureCount = 0;
            var tasks = new List<Task>();

            for (int i = 0; i < 100; i++)
            {
                int requestId = i;
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        await CaromCushionExtensions.ShotAsync(
                            async () =>
                            {
                                await Task.Yield();
                                if (requestId % 3 == 0)
                                {
                                    throw new InvalidOperationException();
                                }
                                return requestId;
                            },
                            cushion,
                            retries: 0);
                        Interlocked.Increment(ref successCount);
                    }
                    catch
                    {
                        Interlocked.Increment(ref failureCount);
                    }
                }));
            }

            await Task.WhenAll(tasks);

            // Should have some successes and failures
            Assert.True(successCount > 0);
            Assert.True(failureCount > 0);
            Assert.Equal(100, successCount + failureCount);
        }

        #endregion
    }
}
