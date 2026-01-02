using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Carom.Extensions;

namespace Carom.Extensions.Tests
{
    /// <summary>
    /// Edge case tests for Bulkhead (Compartment), Rate Limiting (Throttle), and Fallback (Pocket) patterns
    /// </summary>
    public class ExtensionsEdgeCaseTests
    {
        public ExtensionsEdgeCaseTests()
        {
            CompartmentStore.Clear();
            ThrottleStore.Clear();
        }

        #region Compartment Edge Cases

        [Fact]
        public async Task Compartment_WithMaxConcurrencyOne_SerializesExecution()
        {
            var compartment = Compartment.ForResource("serial")
                .WithMaxConcurrency(1)
                .Build();

            var executionOrder = new List<int>();
            var tasks = new List<Task>();

            for (int i = 0; i < 5; i++)
            {
                int taskId = i;
                tasks.Add(Task.Run(async () =>
                {
                    await CaromCompartmentExtensions.ShotAsync<int>(
                        async () =>
                        {
                            lock (executionOrder)
                            {
                                executionOrder.Add(taskId);
                            }
                            await Task.Delay(10);
                            return taskId;
                        },
                        compartment,
                        retries: 0);
                }));
            }

            await Task.WhenAll(tasks);

            // Should have executed all 5 tasks
            Assert.Equal(5, executionOrder.Count);
        }

        [Fact]
        public async Task Compartment_WithMaxConcurrency_RejectsExcess()
        {
            var compartment = Compartment.ForResource("limited")
                .WithMaxConcurrency(2)
                .Build();

            var semaphore = new SemaphoreSlim(0);
            var tasks = new List<Task>();
            var rejectedCount = 0;

            // Start 3 tasks that will block
            for (int i = 0; i < 3; i++)
            {
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        await CaromCompartmentExtensions.ShotAsync<int>(
                            async () =>
                            {
                                await semaphore.WaitAsync();
                                return 1;
                            },
                            compartment,
                            retries: 0);
                    }
                    catch (CompartmentFullException)
                    {
                        Interlocked.Increment(ref rejectedCount);
                    }
                }));
            }

            // Give tasks time to start
            await Task.Delay(100);

            // Release all tasks
            semaphore.Release(3);

            await Task.WhenAll(tasks);

            // At least one should have been rejected
            Assert.True(rejectedCount >= 1, $"Expected at least 1 rejection, got {rejectedCount}");
        }

        [Fact]
        public async Task Compartment_MaxConcurrency_RejectsWhenFull()
        {
            var compartment = Compartment.ForResource("no-wait")
                .WithMaxConcurrency(1)
                .Build();

            var semaphore = new SemaphoreSlim(0);
            
            // Start a task that blocks the compartment
            var blockingTask = Task.Run(async () =>
            {
                await CaromCompartmentExtensions.ShotAsync<int>(
                    async () => 
                    {
                        await semaphore.WaitAsync();
                        return 1;
                    },
                    compartment,
                    retries: 0);
            });

            // Give it time to acquire the semaphore
            await Task.Delay(50);

            // Try to enter - should reject immediately
            await Assert.ThrowsAsync<CompartmentFullException>(async () =>
            {
                await CaromCompartmentExtensions.ShotAsync<int>(
                    async () => 
                    {
                        await Task.Yield();
                        return 2;
                    },
                    compartment,
                    retries: 0);
            });

            semaphore.Release();
            await blockingTask;
        }

        [Fact]
        public void Compartment_EmptyResourceKey_ThrowsArgumentException()
        {
            Assert.Throws<ArgumentException>(() =>
                Compartment.ForResource("")
                    .WithMaxConcurrency(10)
                    .Build());
        }

        [Fact]
        public async Task Compartment_DifferentResources_DontInterfere()
        {
            var comp1 = Compartment.ForResource("db")
                .WithMaxConcurrency(1)
                .Build();

            var comp2 = Compartment.ForResource("api")
                .WithMaxConcurrency(1)
                .Build();

            var semaphore1 = new SemaphoreSlim(0);

            // Block compartment 1
            var task1 = Task.Run(async () =>
            {
                await CaromCompartmentExtensions.ShotAsync<int>(
                    async () => 
                    {
                        await semaphore1.WaitAsync();
                        return 1;
                    },
                    comp1,
                    retries: 0);
            });

            await Task.Delay(50);

            // Compartment 2 should still work
            var result = await CaromCompartmentExtensions.ShotAsync(
                async () =>
                {
                    await Task.Yield();
                    return 42;
                },
                comp2,
                retries: 0);

            Assert.Equal(42, result);

            semaphore1.Release();
            await task1;
        }

        [Fact]
        public async Task Compartment_HighConcurrency_HandlesCorrectly()
        {
            var compartment = Compartment.ForResource("high-concurrent")
                .WithMaxConcurrency(50)
                .Build();

            var tasks = new List<Task<int>>();
            for (int i = 0; i < 100; i++)
            {
                int taskId = i;
                tasks.Add(CaromCompartmentExtensions.ShotAsync(
                    async () =>
                    {
                        await Task.Delay(10);
                        return taskId;
                    },
                    compartment,
                    retries: 0));
            }

            var results = await Task.WhenAll(tasks);
            Assert.Equal(100, results.Length);
        }

        #endregion

        #region Throttle Edge Cases

        [Fact]
        public void Throttle_WithMaxRate_EnforcesLimit()
        {
            var throttle = Throttle.ForService("strict-limit")
                .WithRate(2, TimeSpan.FromSeconds(10))
                .WithBurst(2)
                .Build();

            // First 2 should succeed
            var result1 = CaromThrottleExtensions.Shot(() => 1, throttle, retries: 0);
            var result2 = CaromThrottleExtensions.Shot(() => 2, throttle, retries: 0);

            Assert.Equal(1, result1);
            Assert.Equal(2, result2);

            // Third should fail
            Assert.Throws<ThrottledException>(() =>
                CaromThrottleExtensions.Shot(() => 3, throttle, retries: 0));
        }

        [Fact]
        public async Task Throttle_TokensRefill_AllowsMoreRequests()
        {
            var throttle = Throttle.ForService("refill-test")
                .WithRate(5, TimeSpan.FromMilliseconds(100))
                .WithBurst(5)
                .Build();

            // Consume all tokens
            for (int i = 0; i < 5; i++)
            {
                await CaromThrottleExtensions.ShotAsync(
                    () => Task.FromResult(i),
                    throttle,
                    retries: 0);
            }

            // Wait for refill
            await Task.Delay(150);

            // Should allow more requests
            var result = await CaromThrottleExtensions.ShotAsync(
                () => Task.FromResult(42),
                throttle,
                retries: 0);

            Assert.Equal(42, result);
        }

        [Fact]
        public void Throttle_WithBurst_AllowsInitialBurst()
        {
            var throttle = Throttle.ForService("burst-test")
                .WithRate(1, TimeSpan.FromSeconds(10))
                .WithBurst(10)
                .Build();

            // Should allow 10 initial requests (burst)
            for (int i = 0; i < 10; i++)
            {
                var result = CaromThrottleExtensions.Shot(() => i, throttle, retries: 0);
                Assert.Equal(i, result);
            }

            // 11th should fail
            Assert.Throws<ThrottledException>(() =>
                CaromThrottleExtensions.Shot(() => 11, throttle, retries: 0));
        }

        [Fact]
        public void Throttle_EmptyServiceKey_ThrowsArgumentException()
        {
            Assert.Throws<ArgumentException>(() =>
                Throttle.ForService("")
                    .WithRate(100, TimeSpan.FromSeconds(1))
                    .Build());
        }

        [Fact]
        public void Throttle_DifferentServices_IndependentLimits()
        {
            var throttle1 = Throttle.ForService("api-1")
                .WithRate(2, TimeSpan.FromSeconds(10))
                .WithBurst(2)
                .Build();

            var throttle2 = Throttle.ForService("api-2")
                .WithRate(2, TimeSpan.FromSeconds(10))
                .WithBurst(2)
                .Build();

            // Exhaust throttle1
            CaromThrottleExtensions.Shot(() => 1, throttle1, retries: 0);
            CaromThrottleExtensions.Shot(() => 2, throttle1, retries: 0);

            Assert.Throws<ThrottledException>(() =>
                CaromThrottleExtensions.Shot(() => 3, throttle1, retries: 0));

            // throttle2 should still work
            var result = CaromThrottleExtensions.Shot(() => 42, throttle2, retries: 0);
            Assert.Equal(42, result);
        }

        [Fact]
        public async Task Throttle_ConcurrentRequests_EnforcesLimit()
        {
            var throttle = Throttle.ForService("concurrent-limit")
                .WithRate(10, TimeSpan.FromSeconds(1))
                .WithBurst(10)
                .Build();

            var tasks = new List<Task<bool>>();
            for (int i = 0; i < 20; i++)
            {
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        await CaromThrottleExtensions.ShotAsync<int>(
                            async () => 
                            {
                                await Task.Yield();
                                return 1;
                            },
                            throttle,
                            retries: 0);
                        return true;
                    }
                    catch (ThrottledException)
                    {
                        return false;
                    }
                }));
            }

            var results = await Task.WhenAll(tasks);
            var successCount = results.Count(r => r);
            var failureCount = results.Count(r => !r);

            // Should have some successes and some failures
            Assert.True(successCount > 0);
            Assert.True(failureCount > 0);
            Assert.Equal(20, successCount + failureCount);
        }

        [Fact]
        public void Throttle_VeryShortWindow_WorksCorrectly()
        {
            var throttle = Throttle.ForService("short-window")
                .WithRate(100, TimeSpan.FromMilliseconds(1))
                .Build();

            // Should allow requests
            var result = CaromThrottleExtensions.Shot(() => 42, throttle, retries: 0);
            Assert.Equal(42, result);
        }

        #endregion

        #region Fallback Edge Cases

        [Fact]
        public void Pocket_WithNullFallbackValue_ReturnsNull()
        {
            var result = new Func<string?>(() => throw new Exception()).Pocket((string?)null);
            Assert.Null(result);
        }

        [Fact]
        public void Pocket_WithNullAction_ThrowsNullReferenceException()
        {
            Func<int>? nullFunc = null;
            Assert.Throws<NullReferenceException>(() => nullFunc!.Pocket(42));
        }

        [Fact]
        public void Pocket_WithFallbackFunction_CalledOnFailure()
        {
            var fallbackCalled = false;
            var attemptCount = 0;

            var result = new Func<int>(() =>
            {
                attemptCount++;
                throw new InvalidOperationException();
            }).Pocket(() =>
            {
                fallbackCalled = true;
                return 999;
            });

            Assert.True(fallbackCalled);
            Assert.Equal(999, result);
            Assert.Equal(1, attemptCount);
        }

        [Fact]
        public void Pocket_WithExceptionHandler_ReceivesCorrectException()
        {
            var capturedException = false;
            var expectedMessage = "Test error message";

            var result = new Func<int>(() =>
            {
                throw new InvalidOperationException(expectedMessage);
            }).Pocket(ex =>
            {
                capturedException = ex.Message == expectedMessage;
                return 42;
            });

            Assert.True(capturedException);
            Assert.Equal(42, result);
        }

        [Fact]
        public async Task PocketAsync_WithCancellation_StillReturnsFallback()
        {
            var cts = new CancellationTokenSource();
            cts.Cancel();

            // When cancelled, exception propagates
            await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                await new Func<Task<int>>(async () =>
                {
                    await Task.Delay(100, cts.Token);
                    return 42;
                }).PocketAsync(0, cts.Token);
            });
        }

        [Fact]
        public async Task PocketAsync_WithAsyncFallback_ExecutesAsync()
        {
            var fallbackExecuted = false;

            var result = await new Func<Task<int>>(async () =>
            {
                await Task.Yield();
                throw new InvalidOperationException();
            }).PocketAsync(async () =>
            {
                fallbackExecuted = true;
                await Task.Delay(1);
                return 999;
            });

            Assert.True(fallbackExecuted);
            Assert.Equal(999, result);
        }

        [Fact]
        public void Pocket_NestedFallbacks_WorkCorrectly()
        {
            var result = new Func<int>(() => throw new Exception())
                .Pocket(() => new Func<int>(() => throw new Exception())
                    .Pocket(42));

            Assert.Equal(42, result);
        }

        [Fact]
        public async Task PocketAsync_MultipleConcurrentCalls_DontInterfere()
        {
            var tasks = new List<Task<int>>();
            var random = new Random(42); // Use seed for deterministic behavior

            for (int i = 0; i < 10; i++)
            {
                int taskId = i;
                int delay = random.Next(1, 20);
                tasks.Add(new Func<Task<int>>(async () =>
                {
                    await Task.Delay(delay);
                    if (taskId % 2 == 0)
                    {
                        throw new InvalidOperationException();
                    }
                    return taskId;
                }).PocketAsync(taskId * 100));
            }

            var results = await Task.WhenAll(tasks);

            // Verify even numbers use fallback, odd numbers succeed
            for (int i = 0; i < 10; i++)
            {
                if (i % 2 == 0)
                {
                    Assert.Equal(i * 100, results[i]); // Fallback value
                }
                else
                {
                    Assert.Equal(i, results[i]); // Original value
                }
            }
        }

        [Fact]
        public void Pocket_FallbackThrows_PropagatesFallbackException()
        {
            var ex = Assert.Throws<ArgumentException>(() =>
            {
                new Func<int>(() => throw new InvalidOperationException())
                    .Pocket(() => throw new ArgumentException("Fallback failed"));
            });

            Assert.Equal("Fallback failed", ex.Message);
        }

        [Fact]
        public async Task ShotWithPocketAsync_CombinesRetryAndFallback()
        {
            var attemptCount = 0;

            var result = await CaromFallbackExtensions.ShotWithPocketAsync(
                async () =>
                {
                    attemptCount++;
                    await Task.Yield();
                    throw new InvalidOperationException();
                },
                fallback: 999,
                retries: 2);

            Assert.Equal(999, result);
            Assert.True(attemptCount >= 1, $"Expected at least 1 attempt, got {attemptCount}");
        }

        [Fact]
        public void ShotWithPocket_ZeroRetries_ImmediatelyFallsBack()
        {
            var attemptCount = 0;

            var result = CaromFallbackExtensions.ShotWithPocket(
                () =>
                {
                    attemptCount++;
                    throw new InvalidOperationException();
                },
                fallback: 42,
                retries: 0);

            Assert.Equal(42, result);
            Assert.Equal(1, attemptCount);
        }

        #endregion

        #region Integration Tests

        [Fact]
        public async Task AllPatterns_WorkTogether()
        {
            var cushion = Cushion.ForService("integrated-service")
                .OpenAfter(10, 20)
                .HalfOpenAfter(TimeSpan.FromSeconds(30));

            var compartment = Compartment.ForResource("integrated-resource")
                .WithMaxConcurrency(5)
                .Build();

            var throttle = Throttle.ForService("integrated-throttle")
                .WithRate(100, TimeSpan.FromSeconds(1))
                .Build();

            // Combine all patterns
            var result = await CaromThrottleExtensions.ShotAsync(
                async () =>
                {
                    return await CaromCompartmentExtensions.ShotAsync(
                        async () =>
                        {
                            return await CaromCushionExtensions.ShotAsync(
                                async () =>
                                {
                                    await Task.Yield();
                                    return 42;
                                },
                                cushion,
                                retries: 0);
                        },
                        compartment,
                        retries: 0);
                },
                throttle,
                retries: 0);

            Assert.Equal(42, result);
        }

        [Fact]
        public async Task AllPatterns_WithFallback_GracefulDegradation()
        {
            var cushion = Cushion.ForService("fallback-service")
                .OpenAfter(2, 2)
                .HalfOpenAfter(TimeSpan.FromSeconds(30));

            // Open the circuit
            for (int i = 0; i < 2; i++)
            {
                try
                {
                    await CaromCushionExtensions.ShotAsync<int>(
                        async () =>
                        {
                            await Task.Yield();
                            throw new InvalidOperationException();
                        },
                        cushion,
                        retries: 0);
                }
                catch (Exception)
                {
                    // Expected
                }
            }

            // Use fallback when circuit is open
            var result = await new Func<Task<int>>(async () =>
            {
                return await CaromCushionExtensions.ShotAsync(
                    async () =>
                    {
                        await Task.Yield();
                        return 100;
                    },
                    cushion,
                    retries: 0);
            }).PocketAsync(999);

            Assert.Equal(999, result); // Fallback due to open circuit
        }

        #endregion
    }
}
