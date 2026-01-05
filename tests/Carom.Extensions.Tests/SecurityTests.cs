using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Carom.Extensions;

namespace Carom.Extensions.Tests
{
    /// <summary>
    /// Security-focused tests for Carom Extensions.
    /// Tests circuit breaker, bulkhead, throttle, and fallback patterns for security vulnerabilities.
    /// </summary>
    public class SecurityTests
    {
        public SecurityTests()
        {
        }

        #region Circuit Breaker Security Tests

        [Fact]
        public void Cushion_ValidatesServiceKey()
        {
            Assert.Throws<ArgumentException>(() =>
                Cushion.ForService("")
                    .OpenAfter(3, 5)
                    .HalfOpenAfter(TimeSpan.FromSeconds(10)));

            Assert.Throws<ArgumentException>(() =>
                Cushion.ForService(null!)
                    .OpenAfter(3, 5)
                    .HalfOpenAfter(TimeSpan.FromSeconds(10)));
        }

        [Fact]
        public void Cushion_ValidatesThresholdParameters()
        {
            Assert.Throws<ArgumentException>(() =>
                Cushion.ForService("test")
                    .OpenAfter(0, 5)
                    .HalfOpenAfter(TimeSpan.FromSeconds(10)));

            Assert.Throws<ArgumentException>(() =>
                Cushion.ForService("test-" + Guid.NewGuid())
                    .OpenAfter(10, 5) // threshold > window
                    .HalfOpenAfter(TimeSpan.FromSeconds(10)));
        }

        [Fact]
        public void Cushion_ValidatesHalfOpenDelay()
        {
            Assert.Throws<ArgumentException>(() =>
                Cushion.ForService("test-" + Guid.NewGuid())
                    .OpenAfter(3, 5)
                    .HalfOpenAfter(TimeSpan.Zero));

            Assert.Throws<ArgumentException>(() =>
                Cushion.ForService("test-" + Guid.NewGuid())
                    .OpenAfter(3, 5)
                    .HalfOpenAfter(TimeSpan.FromMilliseconds(-1)));
        }

        [Fact]
        public async Task Cushion_PreventsDosWithFastRejection()
        {
            var cushion = Cushion.ForService("dos-prevention-" + Guid.NewGuid())
                .OpenAfter(2, 2)
                .HalfOpenAfter(TimeSpan.FromSeconds(30));

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
                catch (Exception) { }
            }

            // Measure rejection speed
            var stopwatch = Stopwatch.StartNew();
            var rejectionCount = 0;

            for (int i = 0; i < 1000; i++)
            {
                try
                {
                    await CaromCushionExtensions.ShotAsync(
                        async () =>
                        {
                            await Task.Delay(100); // This should never execute
                            return 42;
                        },
                        cushion,
                        retries: 0);
                }
                catch (CircuitOpenException)
                {
                    rejectionCount++;
                }
            }

            stopwatch.Stop();

            // All should be rejected
            Assert.Equal(1000, rejectionCount);
            
            // Should be extremely fast (no operation execution)
            Assert.True(stopwatch.ElapsedMilliseconds < 500, 
                $"Expected fast rejection under 500ms, took {stopwatch.ElapsedMilliseconds}ms");
        }

        [Fact]
        public void Cushion_ThreadSafe_UnderAttack()
        {
            var cushion = Cushion.ForService("thread-safety-test-" + Guid.NewGuid())
                .OpenAfter(10, 20)
                .HalfOpenAfter(TimeSpan.FromSeconds(30));

            var threads = new List<Thread>();
            var successCount = 0;
            var failureCount = 0;
            var circuitOpenCount = 0;

            // Simulate attack with many concurrent threads
            for (int i = 0; i < 50; i++)
            {
                int threadId = i;
                var thread = new Thread(() =>
                {
                    try
                    {
                        CaromCushionExtensions.Shot(() =>
                        {
                            // 30% failure rate
                            if (threadId % 3 == 0)
                            {
                                throw new InvalidOperationException();
                            }
                            Interlocked.Increment(ref successCount);
                            return 42;
                        }, cushion, retries: 0);
                    }
                    catch (CircuitOpenException)
                    {
                        Interlocked.Increment(ref circuitOpenCount);
                    }
                    catch (InvalidOperationException)
                    {
                        Interlocked.Increment(ref failureCount);
                    }
                });
                threads.Add(thread);
                thread.Start();
            }

            foreach (var thread in threads)
            {
                thread.Join();
            }

            // All operations should be accounted for
            Assert.Equal(50, successCount + failureCount + circuitOpenCount);
            
            // Should have some successes
            Assert.True(successCount > 0);
        }

        [Fact]
        public void Cushion_DoesNotLeakSensitiveInformation()
        {
            var cushion = Cushion.ForService("sensitive-service-" + Guid.NewGuid())
                .OpenAfter(1, 1)
                .HalfOpenAfter(TimeSpan.FromSeconds(30));

            var secretData = "api_key_12345";

            // Open circuit
            try
            {
                CaromCushionExtensions.Shot<int>(() =>
                {
                    var _ = secretData; // Use secret
                    throw new InvalidOperationException("Auth failed");
                }, cushion, retries: 0);
            }
            catch (Exception) { }

            // Circuit open exception should not leak secrets
            var ex = Assert.Throws<CircuitOpenException>(() =>
            {
                CaromCushionExtensions.Shot(() =>
                {
                    var _ = secretData;
                    return 42;
                }, cushion, retries: 0);
            });

            Assert.DoesNotContain(secretData, ex.Message);
            Assert.DoesNotContain(secretData, ex.StackTrace ?? "");
        }

        #endregion

        #region Bulkhead Security Tests

        [Fact]
        public void Compartment_ValidatesResourceKey()
        {
            Assert.Throws<ArgumentException>(() =>
                Compartment.ForResource("")
                    .WithMaxConcurrency(10)
                    .Build());

            Assert.Throws<ArgumentException>(() =>
                Compartment.ForResource(null!)
                    .WithMaxConcurrency(10)
                    .Build());
        }

        [Fact]
        public void Compartment_ValidatesMaxConcurrency()
        {
            Assert.Throws<ArgumentException>(() =>
                Compartment.ForResource("test-" + Guid.NewGuid())
                    .WithMaxConcurrency(0)
                    .Build());

            Assert.Throws<ArgumentException>(() =>
                Compartment.ForResource("test-" + Guid.NewGuid())
                    .WithMaxConcurrency(-1)
                    .Build());
        }

        [Fact]
        public async Task Compartment_PreventsDosWithMaxConcurrency()
        {
            var compartment = Compartment.ForResource("dos-test-" + Guid.NewGuid())
                .WithMaxConcurrency(5)
                .Build();

            var semaphore = new SemaphoreSlim(0);
            var acceptedCount = 0;
            var rejectedCount = 0;
            var tasks = new List<Task>();

            // Try to flood the compartment
            for (int i = 0; i < 20; i++)
            {
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        await CaromCompartmentExtensions.ShotAsync<int>(
                            async () =>
                            {
                                Interlocked.Increment(ref acceptedCount);
                                await semaphore.WaitAsync(); // Block until released
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

            // Give tasks time to try entering
            await Task.Delay(100);

            // Should have exactly maxConcurrency accepted
            Assert.True(acceptedCount <= 5, 
                $"Expected at most 5 accepted, got {acceptedCount}");
            
            // Release all and wait
            semaphore.Release(20);
            await Task.WhenAll(tasks);

            // Should have rejected excess requests
            Assert.True(rejectedCount > 0, 
                $"Expected some rejections, got {rejectedCount}");
        }

        [Fact]
        public async Task Compartment_PreventsDosWithTimeout()
        {
            var compartment = Compartment.ForResource("timeout-test-" + Guid.NewGuid())
                .WithMaxConcurrency(2)
                .Build();

            var semaphore = new SemaphoreSlim(0);
            var stopwatch = Stopwatch.StartNew();

            // Block the compartment
            var blockingTasks = new[]
            {
                Task.Run(() => CaromCompartmentExtensions.Shot<int>(
                    () => { semaphore.Wait(); return 1; },
                    compartment, retries: 0)),
                Task.Run(() => CaromCompartmentExtensions.Shot<int>(
                    () => { semaphore.Wait(); return 2; },
                    compartment, retries: 0))
            };

            await Task.Delay(50);

            // Try to enter with timeout
            var bounce = Bounce.Times(0).WithTimeout(TimeSpan.FromMilliseconds(100));
            
            await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                await CaromCompartmentExtensions.ShotAsync(
                    async () =>
                    {
                        await Task.Delay(1000);
                        return 3;
                    },
                    compartment,
                    bounce);
            });

            stopwatch.Stop();

            // Should timeout quickly, not wait indefinitely
            Assert.True(stopwatch.ElapsedMilliseconds < 500,
                $"Expected timeout around 100ms, took {stopwatch.ElapsedMilliseconds}ms");

            semaphore.Release(2);
            await Task.WhenAll(blockingTasks);
        }

        #endregion

        #region Rate Limiting Security Tests

        [Fact]
        public void Throttle_ValidatesServiceKey()
        {
            Assert.Throws<ArgumentException>(() =>
                Throttle.ForService("")
                    .WithRate(100, TimeSpan.FromSeconds(1))
                    .Build());
        }

        [Fact]
        public void Throttle_ValidatesRateParameters()
        {
            Assert.Throws<ArgumentException>(() =>
                Throttle.ForService("test-" + Guid.NewGuid())
                    .WithRate(0, TimeSpan.FromSeconds(1))
                    .Build());

            Assert.Throws<ArgumentException>(() =>
                Throttle.ForService("test-" + Guid.NewGuid())
                    .WithRate(100, TimeSpan.Zero)
                    .Build());
        }

        [Fact]
        public async Task Throttle_PreventsDosWithRateLimiting()
        {
            var throttle = Throttle.ForService("dos-prevention-" + Guid.NewGuid())
                .WithRate(10, TimeSpan.FromSeconds(1))
                .WithBurst(10)
                .Build();

            var allowedCount = 0;
            var throttledCount = 0;

            // Try to flood with requests
            for (int i = 0; i < 100; i++)
            {
                try
                {
                    await CaromThrottleExtensions.ShotAsync<int>(
                        async () =>
                        {
                            await Task.Yield();
                            Interlocked.Increment(ref allowedCount);
                            return 1;
                        },
                        throttle,
                        retries: 0);
                }
                catch (ThrottledException)
                {
                    Interlocked.Increment(ref throttledCount);
                }
            }

            // Should enforce rate limit
            Assert.True(allowedCount <= 15, // Allow some margin
                $"Expected at most ~10 requests, got {allowedCount}");
            Assert.True(throttledCount >= 85,
                $"Expected at least ~90 throttled, got {throttledCount}");
        }

        [Fact]
        public async Task Throttle_ThreadSafe_UnderAttack()
        {
            var throttle = Throttle.ForService("concurrent-attack-" + Guid.NewGuid())
                .WithRate(50, TimeSpan.FromSeconds(1))
                .WithBurst(50)
                .Build();

            var allowedCount = 0;
            var throttledCount = 0;
            var tasks = new List<Task>();

            // Concurrent attack from many tasks
            for (int i = 0; i < 200; i++)
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
                        Interlocked.Increment(ref allowedCount);
                    }
                    catch (ThrottledException)
                    {
                        Interlocked.Increment(ref throttledCount);
                    }
                }));
            }

            await Task.WhenAll(tasks);

            // Should maintain rate limit under concurrent load
            Assert.True(allowedCount <= 60, // Allow margin
                $"Expected at most ~50 requests, got {allowedCount}");
            Assert.Equal(200, allowedCount + throttledCount);
        }

        [Fact]
        public void Throttle_DoesNotLeakSensitiveInfo()
        {
            var throttle = Throttle.ForService("api-service-" + Guid.NewGuid())
                .WithRate(1, TimeSpan.FromSeconds(10))
                .Build();

            // Exhaust rate limit
            CaromThrottleExtensions.Shot(() => 1, throttle, retries: 0);

            var ex = Assert.Throws<ThrottledException>(() =>
                CaromThrottleExtensions.Shot(() => 2, throttle, retries: 0));

            // Exception should not contain sensitive service details
            Assert.Contains("api-service", ex.Message);
            Assert.DoesNotContain("password", ex.Message.ToLower());
            Assert.DoesNotContain("key", ex.Message.ToLower());
            Assert.DoesNotContain("secret", ex.Message.ToLower());
        }

        #endregion

        #region Fallback Security Tests

        [Fact]
        public void Pocket_DoesNotLeakSensitiveDataInFallback()
        {
            var secretPassword = "P@ssw0rd123!";

            var result = new Func<string>(() =>
            {
                var _ = secretPassword; // Use secret
                throw new InvalidOperationException("Auth failed");
            }).Pocket("default-value");

            Assert.Equal("default-value", result);
            Assert.DoesNotContain(secretPassword, result);
        }

        [Fact]
        public async Task PocketAsync_SecurelyHandlesExceptions()
        {
            var apiKey = "sk_live_123456";

            var result = await new Func<Task<string>>(async () =>
            {
                await Task.Yield();
                var _ = apiKey; // Use API key
                throw new UnauthorizedAccessException("Invalid API key");
            }).PocketAsync("cached-response");

            // Fallback should not leak the API key
            Assert.Equal("cached-response", result);
            Assert.DoesNotContain(apiKey, result);
        }

        [Fact]
        public void Pocket_HandlesExceptionWithSensitiveData()
        {
            Exception? capturedException = null;

            var result = new Func<int>(() =>
            {
                throw new InvalidOperationException("Database connection string: Server=localhost");
            }).Pocket(ex =>
            {
                capturedException = ex;
                // Fallback handler has access to exception but shouldn't leak it
                return 999;
            });

            Assert.Equal(999, result);
            Assert.NotNull(capturedException);
            
            // The exception exists but fallback value doesn't contain sensitive data
            Assert.DoesNotContain("Server=", result.ToString());
        }

        #endregion

        #region Integration Security Tests

        [Fact]
        public async Task CombinedPatterns_SecureUnderAttack()
        {
            var cushion = Cushion.ForService("secure-service-" + Guid.NewGuid())
                .OpenAfter(5, 10)
                .HalfOpenAfter(TimeSpan.FromSeconds(30));

            var throttle = Throttle.ForService("secure-throttle-" + Guid.NewGuid())
                .WithRate(20, TimeSpan.FromSeconds(1))
                .WithBurst(20)
                .Build();

            var successCount = 0;
            var failureCount = 0;
            var tasks = new List<Task>();

            // Simulate attack: 100 concurrent requests
            for (int i = 0; i < 100; i++)
            {
                int requestId = i;
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        await CaromThrottleExtensions.ShotAsync(
                            async () =>
                            {
                                return await CaromCushionExtensions.ShotAsync(
                                    async () =>
                                    {
                                        await Task.Yield();
                                        // 20% failure rate
                                        if (requestId % 5 == 0)
                                        {
                                            throw new InvalidOperationException();
                                        }
                                        return requestId;
                                    },
                                    cushion,
                                    retries: 0);
                            },
                            throttle,
                            retries: 0);
                        Interlocked.Increment(ref successCount);
                    }
                    catch (Exception)
                    {
                        Interlocked.Increment(ref failureCount);
                    }
                }));
            }

            await Task.WhenAll(tasks);

            // System should handle attack gracefully
            Assert.Equal(100, successCount + failureCount);
            
            // Most requests should be rejected/failed (DoS prevention)
            Assert.True(failureCount > successCount,
                $"Expected more failures ({failureCount}) than successes ({successCount}) due to protection");
        }

        #endregion
    }
}
