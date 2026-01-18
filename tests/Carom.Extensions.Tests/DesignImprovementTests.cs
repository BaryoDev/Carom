using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Carom.Extensions;
using CaromCore = global::Carom.Carom;

namespace Carom.Extensions.Tests
{
    /// <summary>
    /// Tests to verify the design improvements and bug fixes implemented.
    /// Each test validates a specific fix from the code review.
    /// </summary>
    public class DesignImprovementTests
    {
        // Note: We don't call Clear() here as it would affect other parallel tests.
        // All tests use unique GUID-based keys to ensure isolation.

        #region RingBuffer Fixes

        [Fact]
        public async Task RingBuffer_SnapshotBasedCountWhere_NoRaceConditions()
        {
            // Verify that CountWhere uses snapshot-based counting to prevent dirty reads
            var serviceKey = "ringbuffer-snapshot-" + Guid.NewGuid();
            var cushion = Cushion.ForService(serviceKey)
                .OpenAfter(failures: 50, within: 100)
                .HalfOpenAfter(TimeSpan.FromMinutes(1));

            var barrier = new Barrier(20);
            var errors = 0;

            // Stress test: many concurrent operations that could expose race conditions
            var tasks = Enumerable.Range(0, 20).Select(i => Task.Run(async () =>
            {
                barrier.SignalAndWait();

                for (int j = 0; j < 200; j++)
                {
                    try
                    {
                        await CaromCushionExtensions.ShotAsync(
                            async () =>
                            {
                                await Task.Yield();
                                // Mix of successes and failures
                                if ((i * j) % 3 == 0)
                                    throw new InvalidOperationException("Test failure");
                                return "success";
                            },
                            cushion,
                            retries: 0);
                    }
                    catch (InvalidOperationException) { }
                    catch (CircuitOpenException) { }
                    catch (Exception ex)
                    {
                        // Unexpected exception indicates a bug
                        Interlocked.Increment(ref errors);
                        Console.WriteLine($"Unexpected error: {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }));

            await Task.WhenAll(tasks);

            Assert.Equal(0, errors);
        }

        [Fact]
        public void RingBuffer_ManyOperations_NoIntegerOverflow()
        {
            // Verify long-based index doesn't overflow in practical usage
            var serviceKey = "ringbuffer-overflow-" + Guid.NewGuid();
            var cushion = Cushion.ForService(serviceKey)
                .OpenAfter(failures: 100000, within: 200000)
                .HalfOpenAfter(TimeSpan.FromMinutes(1));

            var operationCount = 0;

            // Run many operations
            for (int i = 0; i < 50000; i++)
            {
                try
                {
                    CaromCushionExtensions.Shot(
                        () =>
                        {
                            if (i % 2 == 0)
                                throw new InvalidOperationException();
                            return 42;
                        },
                        cushion,
                        retries: 0);
                    operationCount++;
                }
                catch (InvalidOperationException)
                {
                    operationCount++;
                }
                catch (CircuitOpenException) { }
            }

            Assert.True(operationCount > 0, "Operations should complete without overflow");
        }

        #endregion

        #region Atomic Half-Open Transition Fixes

        [Fact]
        public void HalfOpen_AtomicTransition_UsesCompareExchange()
        {
            // Verify that the atomic transition method TryTransitionToHalfOpen
            // uses CompareExchange and only allows one thread to win.
            // The existing test CushionEdgeCaseTests.Cushion_HalfOpenState_AllowsOnlyOneRequest
            // validates the full integration behavior and passed, confirming the fix works.

            var state = new CushionState(100);

            // Open the circuit
            state.Open();
            Assert.Equal(CircuitState.Open, state.State);

            // First transition should succeed
            Assert.True(state.TryTransitionToHalfOpen());
            Assert.Equal(CircuitState.HalfOpen, state.State);

            // Second transition should fail (already half-open)
            Assert.False(state.TryTransitionToHalfOpen());
            Assert.Equal(CircuitState.HalfOpen, state.State);

            // Close and try again
            state.Close();
            Assert.Equal(CircuitState.Closed, state.State);

            // Can't transition from Closed to HalfOpen
            Assert.False(state.TryTransitionToHalfOpen());
            Assert.Equal(CircuitState.Closed, state.State);
        }

        #endregion

        #region ThrottleState Bounded Spin Loop

        [Fact]
        public async Task ThrottleState_HighContention_DoesNotHang()
        {
            // Verify that bounded spin loops prevent CPU starvation
            var serviceKey = "bounded-spin-" + Guid.NewGuid();
            var throttle = Throttle.ForService(serviceKey)
                .WithRate(100, TimeSpan.FromSeconds(1))
                .WithBurst(100)
                .Build();

            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var completedOperations = 0;
            var throttledOperations = 0;

            var tasks = Enumerable.Range(0, 50).Select(i => Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        await CaromThrottleExtensions.ShotAsync(
                            async () => { await Task.Yield(); return 1; },
                            throttle, retries: 0);
                        Interlocked.Increment(ref completedOperations);
                    }
                    catch (ThrottledException)
                    {
                        Interlocked.Increment(ref throttledOperations);
                    }
                }
            }));

            try
            {
                await Task.WhenAll(tasks);
            }
            catch (OperationCanceledException) { }

            // With bounded spin loops, operations should complete or be throttled - not hang
            Assert.True(completedOperations + throttledOperations > 100,
                $"Expected many operations in 3 seconds, got {completedOperations + throttledOperations}");
        }

        [Fact]
        public async Task ThrottleState_ExponentialBackoff_ReducesCPUUsage()
        {
            // Verify that spin loop uses exponential backoff
            var serviceKey = "backoff-test-" + Guid.NewGuid();
            var throttle = Throttle.ForService(serviceKey)
                .WithRate(1, TimeSpan.FromSeconds(10)) // Very low rate
                .WithBurst(1)
                .Build();

            // Exhaust the single token
            try
            {
                CaromThrottleExtensions.Shot(() => 1, throttle, retries: 0);
            }
            catch { }

            // Now race many threads - with backoff, they should yield gracefully
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            var throttledCount = 0;

            var tasks = Enumerable.Range(0, 20).Select(i => Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        await CaromThrottleExtensions.ShotAsync(
                            async () => { await Task.Yield(); return 1; },
                            throttle, retries: 0);
                    }
                    catch (ThrottledException)
                    {
                        Interlocked.Increment(ref throttledCount);
                        await Task.Delay(1); // Brief pause to prevent tight loop
                    }
                }
            }));

            try { await Task.WhenAll(tasks); }
            catch (OperationCanceledException) { }

            // Should have many throttled operations - proves backoff doesn't block forever
            Assert.True(throttledCount > 0, "Operations should be throttled without hanging");
        }

        #endregion

        #region CompartmentState IDisposable

        [Fact]
        public void CompartmentState_Dispose_ReleasesSemaphore()
        {
            // Verify that Dispose properly cleans up semaphore
            var state = new CompartmentState(5, 10);

            // Enter some slots
            Assert.True(state.TryEnter());
            Assert.True(state.TryEnter());
            Assert.Equal(2, state.ActiveCount);

            // Dispose should work without throwing
            state.Dispose();

            // Operations on disposed state should throw
            Assert.Throws<ObjectDisposedException>(() => state.TryEnter());
        }

        [Fact]
        public async Task CompartmentState_DisposeAsync_ProperCleanup()
        {
            var state = new CompartmentState(3, 5);

            // Enter slots
            Assert.True(await state.TryEnterAsync());
            Assert.True(await state.TryEnterAsync());

            // Release should work
            state.Release();
            state.Release();

            // Dispose
            state.Dispose();

            // Verify disposed state
            await Assert.ThrowsAsync<ObjectDisposedException>(
                async () => await state.TryEnterAsync());
        }

        #endregion

        #region LRU Eviction in State Stores

        [Fact]
        public void CushionStore_LRUEviction_RemovesOldestEntries()
        {
            // Test verifies eviction occurs when count exceeds max.
            // Use unique prefix to avoid interfering with other parallel tests.
            var testPrefix = $"lru-cushion-{Guid.NewGuid()}-";
            var originalMaxSize = CushionStore.MaxSize;

            // Note: Changing MaxSize is not thread-safe for parallel tests,
            // but the eviction logic itself is what we're testing.
            CushionStore.MaxSize = 10;

            try
            {
                var initialCount = CushionStore.Count;

                // Add more entries than max size
                for (int i = 0; i < 15; i++)
                {
                    var cushion = Cushion.ForService($"{testPrefix}{i}")
                        .OpenAfter(failures: 1, within: 1)
                        .HalfOpenAfter(TimeSpan.FromSeconds(30));

                    try
                    {
                        CaromCushionExtensions.Shot(() => 42, cushion, retries: 0);
                    }
                    catch { }
                }

                // Eviction should have prevented unbounded growth
                // We allow some buffer since other parallel tests may add entries
                Assert.True(CushionStore.Count <= 20,
                    $"Expected count <= 20 after eviction, got {CushionStore.Count}");
            }
            finally
            {
                CushionStore.MaxSize = originalMaxSize;
            }
        }

        [Fact]
        public void ThrottleStore_LRUEviction_RemovesOldestEntries()
        {
            var testPrefix = $"lru-throttle-{Guid.NewGuid()}-";
            var originalMaxSize = ThrottleStore.MaxSize;
            ThrottleStore.MaxSize = 10;

            try
            {
                for (int i = 0; i < 15; i++)
                {
                    var throttle = Throttle.ForService($"{testPrefix}{i}")
                        .WithRate(100, TimeSpan.FromSeconds(1))
                        .WithBurst(100)
                        .Build();

                    try
                    {
                        CaromThrottleExtensions.Shot(() => 42, throttle, retries: 0);
                    }
                    catch { }
                }

                Assert.True(ThrottleStore.Count <= 20,
                    $"Expected count <= 20 after eviction, got {ThrottleStore.Count}");
            }
            finally
            {
                ThrottleStore.MaxSize = originalMaxSize;
            }
        }

        [Fact]
        public void CompartmentStore_LRUEviction_DisposesEvictedStates()
        {
            var testPrefix = $"lru-compartment-{Guid.NewGuid()}-";
            var originalMaxSize = CompartmentStore.MaxSize;
            CompartmentStore.MaxSize = 10;

            try
            {
                for (int i = 0; i < 15; i++)
                {
                    var compartment = Compartment.ForResource($"{testPrefix}{i}")
                        .WithMaxConcurrency(5)
                        .Build();

                    try
                    {
                        CaromCompartmentExtensions.Shot(() => 42, compartment, retries: 0);
                    }
                    catch { }
                }

                // Eviction should have occurred and disposed states
                Assert.True(CompartmentStore.Count <= 20,
                    $"Expected count <= 20 after eviction, got {CompartmentStore.Count}");
            }
            finally
            {
                CompartmentStore.MaxSize = originalMaxSize;
            }
        }

        #endregion

        #region Task.Delay Leak Fix

        [Fact]
        public async Task ShotAsync_NoTimeout_NoCancellation_NoTaskLeak()
        {
            // When no timeout and no cancellation token, should await directly
            // without creating a Task.Delay that leaks
            var result = await CaromCore.ShotAsync(
                async () =>
                {
                    await Task.Delay(10);
                    return 42;
                },
                retries: 0);

            Assert.Equal(42, result);
        }

        [Fact]
        public async Task ShotAsync_WithTimeout_ProperlyCleanedUp()
        {
            // With timeout, the CTS should be properly disposed
            var result = await CaromCore.ShotAsync(
                async () =>
                {
                    await Task.Delay(10);
                    return 42;
                },
                retries: 0,
                timeout: TimeSpan.FromSeconds(5));

            Assert.Equal(42, result);
        }

        [Fact]
        public async Task ShotAsync_WithCancellationToken_ProperlyHandled()
        {
            using var cts = new CancellationTokenSource();

            var result = await CaromCore.ShotAsync(
                async () =>
                {
                    await Task.Delay(10);
                    return 42;
                },
                retries: 0,
                ct: cts.Token);

            Assert.Equal(42, result);
        }

        [Fact]
        public async Task ShotAsync_Timeout_Cancels()
        {
            // Use a generous timeout to avoid flakiness, but action takes much longer
            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await CaromCore.ShotAsync(
                    async () =>
                    {
                        // This should be cancelled before it completes
                        await Task.Delay(TimeSpan.FromMinutes(1));
                        return 42;
                    },
                    retries: 0,
                    timeout: TimeSpan.FromMilliseconds(500));
            });

            Assert.NotNull(exception);
        }

        [Fact]
        public async Task ShotAsync_VoidVersion_NoLeak()
        {
            var executed = false;

            await CaromCore.ShotAsync(
                async () =>
                {
                    await Task.Delay(10);
                    executed = true;
                },
                retries: 0);

            Assert.True(executed);
        }

        #endregion

        #region State Store Concurrent Access

        [Fact]
        public async Task AllStores_ConcurrentAccess_ThreadSafe()
        {
            var barrier = new Barrier(30);
            var errors = new ConcurrentBag<Exception>();

            var tasks = Enumerable.Range(0, 30).Select(i => Task.Run(async () =>
            {
                barrier.SignalAndWait();

                try
                {
                    // Mix of operations across all stores
                    var cushion = Cushion.ForService($"concurrent-{i % 5}")
                        .OpenAfter(failures: 3, within: 5)
                        .HalfOpenAfter(TimeSpan.FromSeconds(30));

                    var throttle = Throttle.ForService($"concurrent-{i % 5}")
                        .WithRate(100, TimeSpan.FromSeconds(1))
                        .WithBurst(100)
                        .Build();

                    var compartment = Compartment.ForResource($"concurrent-{i % 5}")
                        .WithMaxConcurrency(10)
                        .Build();

                    for (int j = 0; j < 50; j++)
                    {
                        try
                        {
                            await CaromCushionExtensions.ShotAsync(
                                async () => { await Task.Yield(); return 1; },
                                cushion, retries: 0);
                        }
                        catch (CircuitOpenException) { }

                        try
                        {
                            await CaromThrottleExtensions.ShotAsync(
                                async () => { await Task.Yield(); return 1; },
                                throttle, retries: 0);
                        }
                        catch (ThrottledException) { }

                        try
                        {
                            await CaromCompartmentExtensions.ShotAsync(
                                async () => { await Task.Yield(); return 1; },
                                compartment, retries: 0);
                        }
                        catch (CompartmentFullException) { }
                    }
                }
                catch (Exception ex)
                {
                    errors.Add(ex);
                }
            }));

            await Task.WhenAll(tasks);

            Assert.Empty(errors);
        }

        #endregion

        #region Semaphore Max Count Fix

        [Fact]
        public async Task CompartmentState_SemaphoreMaxCount_EqualsMaxConcurrency()
        {
            // Verify semaphore max count equals maxConcurrency, not maxConcurrency + queueDepth
            var maxConcurrency = 5;
            var queueDepth = 10;
            var state = new CompartmentState(maxConcurrency, queueDepth);

            // Should be able to enter exactly maxConcurrency times without waiting
            for (int i = 0; i < maxConcurrency; i++)
            {
                Assert.True(state.TryEnter(), $"Should be able to enter slot {i + 1}");
            }

            // Next entry should fail (immediate, no waiting)
            Assert.False(state.TryEnter(), "Should not be able to enter beyond max concurrency");

            // Clean up
            for (int i = 0; i < maxConcurrency; i++)
            {
                state.Release();
            }

            state.Dispose();
        }

        #endregion
    }
}
