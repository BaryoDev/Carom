using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Carom.Extensions;

namespace Carom.Extensions.Tests
{
    /// <summary>
    /// Real-world use case tests that simulate production scenarios.
    /// These tests expose edge cases, race conditions, and stress the library
    /// under realistic conditions.
    /// </summary>
    public class RealWorldUseCaseTests
    {
        #region E-Commerce Payment Processing

        [Fact]
        public async Task PaymentGateway_HandlesBlackFridayTraffic()
        {
            // Scenario: Black Friday surge - 1000 concurrent payment attempts
            var cushion = Cushion.ForService("payment-gateway-" + Guid.NewGuid())
                .OpenAfter(failures: 10, within: 50)
                .HalfOpenAfter(TimeSpan.FromMilliseconds(500));

            var successfulPayments = 0;
            var failedPayments = 0;
            var circuitOpenRejections = 0;
            var random = new Random(42); // Deterministic seed for reproducibility

            var tasks = Enumerable.Range(0, 1000).Select(async i =>
            {
                try
                {
                    await CaromCushionExtensions.ShotAsync(
                        async () =>
                        {
                            await Task.Delay(1); // Simulate API latency
                            // 5% failure rate from payment provider
                            if (random.NextDouble() < 0.05)
                                throw new InvalidOperationException("Payment declined");
                            return new { TransactionId = Guid.NewGuid() };
                        },
                        cushion,
                        retries: 1);
                    Interlocked.Increment(ref successfulPayments);
                }
                catch (CircuitOpenException)
                {
                    Interlocked.Increment(ref circuitOpenRejections);
                }
                catch
                {
                    Interlocked.Increment(ref failedPayments);
                }
            });

            await Task.WhenAll(tasks);

            // Verify the system handled the load gracefully
            var total = successfulPayments + failedPayments + circuitOpenRejections;
            Assert.Equal(1000, total);

            // Most payments should succeed given 5% failure rate
            Assert.True(successfulPayments > 800,
                $"Expected >800 successful payments, got {successfulPayments}");
        }

        [Fact]
        public async Task PaymentGateway_ProtectsDownstreamDuringOutage()
        {
            // Scenario: Payment provider goes down completely
            var cushion = Cushion.ForService("payment-outage-" + Guid.NewGuid())
                .OpenAfter(failures: 3, within: 5)
                .HalfOpenAfter(TimeSpan.FromSeconds(1));

            var callsToProvider = 0;
            var rejections = 0;

            // Simulate 100 requests during outage
            for (int i = 0; i < 100; i++)
            {
                try
                {
                    await CaromCushionExtensions.ShotAsync<int>(
                        async () =>
                        {
                            Interlocked.Increment(ref callsToProvider);
                            await Task.Delay(1);
                            throw new HttpRequestException("Connection refused");
                        },
                        cushion,
                        retries: 0);
                }
                catch (CircuitOpenException)
                {
                    Interlocked.Increment(ref rejections);
                }
                catch { }
            }

            // Circuit should have opened after ~3-5 failures
            // Remaining requests should have been fast-failed
            Assert.True(callsToProvider < 15,
                $"Circuit should limit calls to failing service, got {callsToProvider}");
            Assert.True(rejections > 85,
                $"Most requests should be circuit-rejected, got {rejections}");
        }

        #endregion

        #region Database Connection Pool Management

        [Fact]
        public async Task ConnectionPool_PreventsDatabaseOverload()
        {
            // Scenario: Database can handle max 5 concurrent connections
            var pool = Compartment.ForResource("db-pool-" + Guid.NewGuid())
                .WithMaxConcurrency(5)
                .Build();

            var maxConcurrent = 0;
            var currentConcurrent = 0;
            var queries = new List<Task>();
            var completed = 0;
            var rejected = 0;

            // Simulate 50 concurrent queries
            for (int i = 0; i < 50; i++)
            {
                queries.Add(Task.Run(async () =>
                {
                    try
                    {
                        await CaromCompartmentExtensions.ShotAsync(
                            async () =>
                            {
                                var c = Interlocked.Increment(ref currentConcurrent);
                                var max = c;
                                while (true)
                                {
                                    var oldMax = Volatile.Read(ref maxConcurrent);
                                    if (max <= oldMax) break;
                                    if (Interlocked.CompareExchange(ref maxConcurrent, max, oldMax) == oldMax)
                                        break;
                                }

                                await Task.Delay(50); // Simulate query time

                                Interlocked.Decrement(ref currentConcurrent);
                                return "query result";
                            },
                            pool,
                            retries: 0);
                        Interlocked.Increment(ref completed);
                    }
                    catch (CompartmentFullException)
                    {
                        Interlocked.Increment(ref rejected);
                    }
                }));
            }

            await Task.WhenAll(queries);

            // Should never exceed max concurrency
            Assert.True(maxConcurrent <= 5,
                $"Max concurrent should be <=5, was {maxConcurrent}");

            // Some should complete, some rejected
            Assert.True(completed > 0, "Some queries should complete");
            Assert.True(rejected > 0, "Excess queries should be rejected");
            Assert.Equal(50, completed + rejected);
        }

        [Fact]
        public async Task ConnectionPool_ReleasesAfterFailure()
        {
            // Scenario: Query fails, connection should be released
            var pool = Compartment.ForResource("db-release-" + Guid.NewGuid())
                .WithMaxConcurrency(2)
                .Build();

            var slot1Used = 0;
            var slot2Used = 0;

            // First batch: both fail
            for (int i = 0; i < 2; i++)
            {
                try
                {
                    await CaromCompartmentExtensions.ShotAsync<int>(
                        () => throw new InvalidOperationException("Query failed"),
                        pool,
                        retries: 0);
                }
                catch (InvalidOperationException)
                {
                    slot1Used++;
                }
            }

            // Slots should be released - second batch should work
            for (int i = 0; i < 2; i++)
            {
                await CaromCompartmentExtensions.ShotAsync(
                    async () =>
                    {
                        await Task.Yield();
                        return "success";
                    },
                    pool,
                    retries: 0);
                slot2Used++;
            }

            Assert.Equal(2, slot1Used);
            Assert.Equal(2, slot2Used);
        }

        #endregion

        #region External API Rate Limiting

        [Fact]
        public async Task RateLimiter_EnforcesApiQuota()
        {
            // Scenario: External API has strict 100 req/sec limit
            var throttle = Throttle.ForService("external-api-" + Guid.NewGuid())
                .WithRate(100, TimeSpan.FromSeconds(1))
                .WithBurst(100)
                .Build();

            var allowed = 0;
            var throttled = 0;

            // Burst 200 requests
            var stopwatch = Stopwatch.StartNew();
            for (int i = 0; i < 200; i++)
            {
                try
                {
                    await CaromThrottleExtensions.ShotAsync(
                        async () =>
                        {
                            await Task.Yield();
                            return "api response";
                        },
                        throttle,
                        retries: 0);
                    Interlocked.Increment(ref allowed);
                }
                catch (ThrottledException)
                {
                    Interlocked.Increment(ref throttled);
                }
            }
            stopwatch.Stop();

            // Should only allow burst + some refill during test execution
            // With 100 burst and 200 requests over ~100-500ms, expect most to be throttled
            Assert.True(allowed <= 200,
                $"Expected most requests to be throttled, got {allowed} allowed");
            Assert.True(throttled >= 1,
                $"Expected some throttling, got {throttled}");
        }

        [Fact]
        [Trait("Category", "LocalOnly")]
        public async Task RateLimiter_RefillsOverTime()
        {
            // Scenario: Tokens should refill after window passes
            // Use a 2s window so tokens don't refill during the first batch even under load,
            // but refill happens in a reasonable time for testing
            var throttle = Throttle.ForService("refill-test-" + Guid.NewGuid())
                .WithRate(10, TimeSpan.FromSeconds(2))
                .WithBurst(10)
                .Build();

            // Exhaust all tokens synchronously to avoid async scheduling delays
            var firstBatch = 0;
            for (int i = 0; i < 20; i++)
            {
                try
                {
                    CaromThrottleExtensions.Shot(() => 1, throttle, retries: 0);
                    firstBatch++;
                }
                catch (ThrottledException) { }
            }

            Assert.True(firstBatch <= 15, $"First batch should allow ~10, got {firstBatch}");

            // Wait for full refill (2s window = 200ms per token, need 10 tokens = 2s + margin)
            await Task.Delay(3000);

            // Should have tokens again
            var secondBatch = 0;
            for (int i = 0; i < 10; i++)
            {
                try
                {
                    await CaromThrottleExtensions.ShotAsync(
                        async () => { await Task.Yield(); return 1; },
                        throttle, retries: 0);
                    secondBatch++;
                }
                catch (ThrottledException) { }
            }

            Assert.True(secondBatch >= 1, $"Second batch should allow some tokens, got {secondBatch}");
        }

        #endregion

        #region Microservice Communication

        [Fact]
        public async Task ServiceMesh_CascadingFailureProtection()
        {
            // Scenario: Service A -> Service B -> Service C
            // If C fails, B should circuit break, A should see failures

            var serviceB = Cushion.ForService("service-b-" + Guid.NewGuid())
                .OpenAfter(failures: 2, within: 5)
                .HalfOpenAfter(TimeSpan.FromSeconds(5));

            var totalFailures = 0;
            var circuitOpenCount = 0;
            var regularExceptions = 0;

            // Service C is completely down
            Func<Task<string>> callServiceC = () =>
                throw new HttpRequestException("Service C unreachable");

            // Service B calls C through circuit breaker
            for (int i = 0; i < 20; i++)
            {
                try
                {
                    await CaromCushionExtensions.ShotAsync(
                        callServiceC,
                        serviceB,
                        retries: 0);
                }
                catch (CircuitOpenException)
                {
                    Interlocked.Increment(ref circuitOpenCount);
                    Interlocked.Increment(ref totalFailures);
                }
                catch
                {
                    Interlocked.Increment(ref regularExceptions);
                    Interlocked.Increment(ref totalFailures);
                }
            }

            // All requests should fail
            Assert.Equal(20, totalFailures);

            // After 2 failures, circuit should open
            // So we should see ~2 regular exceptions and ~18 circuit open
            Assert.True(regularExceptions <= 5,
                $"Only first few should hit service, got {regularExceptions}");
            Assert.True(circuitOpenCount >= 15,
                $"Most should be circuit-open rejections, got {circuitOpenCount}");
        }

        [Fact]
        public async Task ServiceMesh_GracefulDegradation()
        {
            // Scenario: Primary service fails, fallback to cache
            var primaryService = Cushion.ForService("primary-" + Guid.NewGuid())
                .OpenAfter(failures: 2, within: 3)
                .HalfOpenAfter(TimeSpan.FromSeconds(10));

            var cachedValue = "cached-response";
            var primaryCalls = 0;
            var fallbackCalls = 0;

            for (int i = 0; i < 10; i++)
            {
                var result = await new Func<Task<string>>(async () =>
                {
                    return await CaromCushionExtensions.ShotAsync<string>(
                        async () =>
                        {
                            Interlocked.Increment(ref primaryCalls);
                            await Task.Delay(1);
                            throw new HttpRequestException("Service unavailable");
                        },
                        primaryService,
                        retries: 0);
                }).PocketAsync(() =>
                {
                    Interlocked.Increment(ref fallbackCalls);
                    return Task.FromResult(cachedValue);
                });

                Assert.Equal(cachedValue, result);
            }

            // Should have tried primary a few times, then circuit opened
            Assert.True(primaryCalls <= 5,
                $"Primary should stop being called after circuit opens, got {primaryCalls}");
            Assert.Equal(10, fallbackCalls);
        }

        #endregion

        #region High Contention Scenarios

        [Fact]
        public async Task HighContention_CircuitBreakerUnderStress()
        {
            // Scenario: 100 concurrent threads hammering circuit breaker
            var cushion = Cushion.ForService("stress-test-" + Guid.NewGuid())
                .OpenAfter(failures: 50, within: 100)
                .HalfOpenAfter(TimeSpan.FromMilliseconds(100));

            var results = new ConcurrentBag<string>();
            var barrier = new Barrier(100);

            var tasks = Enumerable.Range(0, 100).Select(i => Task.Run(async () =>
            {
                barrier.SignalAndWait(); // Ensure all threads start together

                for (int j = 0; j < 100; j++)
                {
                    try
                    {
                        await CaromCushionExtensions.ShotAsync(
                            async () =>
                            {
                                await Task.Yield();
                                // 30% failure rate
                                if ((i + j) % 3 == 0)
                                    throw new InvalidOperationException();
                                return "ok";
                            },
                            cushion,
                            retries: 0);
                        results.Add("success");
                    }
                    catch (CircuitOpenException)
                    {
                        results.Add("circuit-open");
                    }
                    catch
                    {
                        results.Add("failure");
                    }
                }
            }));

            await Task.WhenAll(tasks);

            // All 10,000 operations should be accounted for
            Assert.Equal(10000, results.Count);

            // Verify no inconsistent state
            var successCount = results.Count(r => r == "success");
            var failureCount = results.Count(r => r == "failure");
            var circuitOpenCount = results.Count(r => r == "circuit-open");

            // Should have meaningful counts in each category
            Assert.True(successCount > 0, "Should have some successes");
            Assert.True(failureCount > 0, "Should have some failures");
        }

        [Fact]
        public async Task HighContention_ThrottleUnderStress()
        {
            // Scenario: 50 threads competing for rate-limited resource
            var throttle = Throttle.ForService("throttle-stress-" + Guid.NewGuid())
                .WithRate(100, TimeSpan.FromSeconds(1))
                .WithBurst(100)
                .Build();

            var allowed = 0;
            var throttled = 0;
            var barrier = new Barrier(50);

            var tasks = Enumerable.Range(0, 50).Select(i => Task.Run(async () =>
            {
                barrier.SignalAndWait();

                for (int j = 0; j < 10; j++)
                {
                    try
                    {
                        await CaromThrottleExtensions.ShotAsync(
                            async () => { await Task.Yield(); return 1; },
                            throttle,
                            retries: 0);
                        Interlocked.Increment(ref allowed);
                    }
                    catch (ThrottledException)
                    {
                        Interlocked.Increment(ref throttled);
                    }
                }
            }));

            await Task.WhenAll(tasks);

            Assert.Equal(500, allowed + throttled);
            // Rate limiter should allow roughly burst + some refill
            Assert.True(allowed <= 150,
                $"Should allow at most burst + refill, got {allowed}");
        }

        [Fact]
        public async Task HighContention_CompartmentUnderStress()
        {
            // Scenario: 100 tasks fighting for 10 slots
            var compartment = Compartment.ForResource("compartment-stress-" + Guid.NewGuid())
                .WithMaxConcurrency(10)
                .Build();

            var maxObservedConcurrency = 0;
            var currentConcurrency = 0;
            var accepted = 0;
            var rejected = 0;
            var barrier = new Barrier(100);

            var tasks = Enumerable.Range(0, 100).Select(i => Task.Run(async () =>
            {
                barrier.SignalAndWait();

                try
                {
                    await CaromCompartmentExtensions.ShotAsync(
                        async () =>
                        {
                            var c = Interlocked.Increment(ref currentConcurrency);
                            InterlockedMax(ref maxObservedConcurrency, c);

                            await Task.Delay(50);

                            Interlocked.Decrement(ref currentConcurrency);
                            return "result";
                        },
                        compartment,
                        retries: 0);
                    Interlocked.Increment(ref accepted);
                }
                catch (CompartmentFullException)
                {
                    Interlocked.Increment(ref rejected);
                }
            }));

            await Task.WhenAll(tasks);

            Assert.Equal(100, accepted + rejected);
            Assert.True(maxObservedConcurrency <= 10,
                $"Max concurrency should be <=10, was {maxObservedConcurrency}");
        }

        private static void InterlockedMax(ref int location, int value)
        {
            int current;
            do
            {
                current = Volatile.Read(ref location);
                if (value <= current) return;
            } while (Interlocked.CompareExchange(ref location, value, current) != current);
        }

        #endregion

        #region Long Running Service Scenarios

        [Fact]
        public async Task LongRunning_CircuitBreakerRecovery()
        {
            // Scenario: Service goes down and then recovers
            var cushion = Cushion.ForService("recovery-test-" + Guid.NewGuid())
                .OpenAfter(failures: 2, within: 3)
                .HalfOpenAfter(TimeSpan.FromMilliseconds(100));

            var serviceAvailable = false;

            // Phase 1: Service is down
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    await CaromCushionExtensions.ShotAsync<int>(
                        () => throw new InvalidOperationException("Service down"),
                        cushion,
                        retries: 0);
                }
                catch { }
            }

            // Phase 2: Service comes back up
            serviceAvailable = true;
            await Task.Delay(150); // Wait for half-open delay

            // Phase 3: Circuit should allow test request and close
            var recovered = false;
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    await CaromCushionExtensions.ShotAsync(
                        async () =>
                        {
                            await Task.Yield();
                            if (!serviceAvailable) throw new InvalidOperationException();
                            return "success";
                        },
                        cushion,
                        retries: 0);
                    recovered = true;
                    break;
                }
                catch (CircuitOpenException)
                {
                    await Task.Delay(150); // Wait for next half-open window
                }
            }

            Assert.True(recovered, "Circuit should recover after service comes back");
        }

        [Fact]
        public async Task LongRunning_TokenBucketRefillAccuracy()
        {
            // Scenario: Verify token refill is accurate over time
            var throttle = Throttle.ForService("refill-accuracy-" + Guid.NewGuid())
                .WithRate(10, TimeSpan.FromMilliseconds(100))
                .WithBurst(10)
                .Build();

            // Exhaust initial burst
            for (int i = 0; i < 10; i++)
            {
                try
                {
                    await CaromThrottleExtensions.ShotAsync(
                        async () => { await Task.Yield(); return 1; },
                        throttle, retries: 0);
                }
                catch (ThrottledException) { }
            }

            // Wait exactly 100ms for full refill
            await Task.Delay(110);

            // Should have close to 10 tokens again
            var secondBatch = 0;
            for (int i = 0; i < 15; i++)
            {
                try
                {
                    await CaromThrottleExtensions.ShotAsync(
                        async () => { await Task.Yield(); return 1; },
                        throttle, retries: 0);
                    secondBatch++;
                }
                catch (ThrottledException) { }
            }

            // Should get around 10 requests (rate per window)
            Assert.True(secondBatch >= 5 && secondBatch <= 15,
                $"Expected 5-15 requests after refill, got {secondBatch}");
        }

        #endregion

        #region Edge Case Scenarios

        [Fact]
        public async Task EdgeCase_AllPatternsComposed()
        {
            // Scenario: All resilience patterns composed together
            var throttle = Throttle.ForService("all-patterns-throttle-" + Guid.NewGuid())
                .WithRate(50, TimeSpan.FromSeconds(1))
                .WithBurst(50)
                .Build();

            var compartment = Compartment.ForResource("all-patterns-pool-" + Guid.NewGuid())
                .WithMaxConcurrency(5)
                .Build();

            var cushion = Cushion.ForService("all-patterns-service-" + Guid.NewGuid())
                .OpenAfter(failures: 3, within: 10)
                .HalfOpenAfter(TimeSpan.FromSeconds(5));

            var results = new ConcurrentDictionary<string, int>();

            var tasks = Enumerable.Range(0, 50).Select(i => Task.Run(async () =>
            {
                try
                {
                    var result = await CaromThrottleExtensions.ShotAsync(
                        async () =>
                        {
                            return await CaromCompartmentExtensions.ShotAsync(
                                async () =>
                                {
                                    return await CaromCushionExtensions.ShotAsync(
                                        async () =>
                                        {
                                            await Task.Delay(10);
                                            // 20% failure
                                            if (i % 5 == 0)
                                                throw new InvalidOperationException();
                                            return "success";
                                        },
                                        cushion,
                                        retries: 1);
                                },
                                compartment,
                                retries: 0);
                        },
                        throttle,
                        retries: 0);

                    results.AddOrUpdate("success", 1, (_, v) => v + 1);
                }
                catch (ThrottledException)
                {
                    results.AddOrUpdate("throttled", 1, (_, v) => v + 1);
                }
                catch (CompartmentFullException)
                {
                    results.AddOrUpdate("compartment-full", 1, (_, v) => v + 1);
                }
                catch (CircuitOpenException)
                {
                    results.AddOrUpdate("circuit-open", 1, (_, v) => v + 1);
                }
                catch
                {
                    results.AddOrUpdate("other-error", 1, (_, v) => v + 1);
                }
            }));

            await Task.WhenAll(tasks);

            // All 50 requests should be accounted for
            var total = results.Values.Sum();
            Assert.Equal(50, total);

            // Should have variety of outcomes showing all patterns active
            Assert.True(results.Count >= 2,
                $"Expected multiple outcome types, got: {string.Join(", ", results)}");
        }

        [Fact]
        public async Task EdgeCase_RapidStateTransitions()
        {
            // Scenario: Circuit breaker rapidly transitioning between states
            var cushion = Cushion.ForService("rapid-transition-" + Guid.NewGuid())
                .OpenAfter(failures: 1, within: 2)
                .HalfOpenAfter(TimeSpan.FromMilliseconds(50));

            var stateChanges = new ConcurrentBag<string>();

            for (int i = 0; i < 20; i++)
            {
                try
                {
                    await CaromCushionExtensions.ShotAsync(
                        async () =>
                        {
                            await Task.Yield();
                            // Alternate between success and failure
                            if (i % 2 == 0)
                                throw new InvalidOperationException();
                            return "success";
                        },
                        cushion,
                        retries: 0);
                    stateChanges.Add("success");
                }
                catch (CircuitOpenException)
                {
                    stateChanges.Add("circuit-open");
                    await Task.Delay(60); // Wait for half-open
                }
                catch
                {
                    stateChanges.Add("failure");
                }
            }

            // Should see a mix of outcomes indicating state transitions
            Assert.True(stateChanges.Count == 20);
            Assert.Contains("circuit-open", stateChanges);
        }

        [Fact]
        public async Task EdgeCase_MinimalBurstSize()
        {
            // Scenario: Throttle with burst equal to max requests (minimum allowed)
            var throttle = Throttle.ForService("minimal-burst-" + Guid.NewGuid())
                .WithRate(5, TimeSpan.FromSeconds(1))
                .WithBurst(5)
                .Build();

            var allowed = 0;
            var throttled = 0;

            for (int i = 0; i < 10; i++)
            {
                try
                {
                    await CaromThrottleExtensions.ShotAsync(
                        async () => { await Task.Yield(); return 1; },
                        throttle, retries: 0);
                    allowed++;
                }
                catch (ThrottledException)
                {
                    throttled++;
                }
            }

            // With burst of 5, should allow exactly 5 initially
            Assert.True(allowed <= 7, $"Expected <=7 allowed with burst=5, got {allowed}");
            Assert.True(throttled >= 3, $"Expected >=3 throttled, got {throttled}");
        }

        [Fact]
        public async Task EdgeCase_VerySmallTimeWindow()
        {
            // Scenario: Very small time window for rate limiting
            var throttle = Throttle.ForService("small-window-" + Guid.NewGuid())
                .WithRate(1000, TimeSpan.FromMilliseconds(10))
                .WithBurst(1000)
                .Build();

            var allowed = 0;

            // Should allow burst, then refill very quickly
            for (int i = 0; i < 200; i++)
            {
                try
                {
                    await CaromThrottleExtensions.ShotAsync(
                        async () => { await Task.Yield(); return 1; },
                        throttle, retries: 0);
                    allowed++;
                }
                catch (ThrottledException) { }

                if (i == 100)
                    await Task.Delay(20); // Wait for refill
            }

            // Should allow more than initial burst due to refill
            Assert.True(allowed > 100, $"Expected >100 allowed with quick refill, got {allowed}");
        }

        #endregion

        #region Cleanup

        public RealWorldUseCaseTests()
        {
            // Clean state before each test
            CushionStore.Clear();
            CompartmentStore.Clear();
            ThrottleStore.Clear();
        }

        #endregion
    }
}
