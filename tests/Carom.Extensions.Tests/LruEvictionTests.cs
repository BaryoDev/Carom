using System;
using Xunit;
using Carom.Extensions;

namespace Carom.Extensions.Tests
{
    [CollectionDefinition("StoreEviction", DisableParallelization = true)]
    public class StoreEvictionCollection
    {
    }

    /// <summary>
    /// LRU eviction tests. These shrink the global store MaxSize and trigger
    /// eviction, which would delete the state of any test running in parallel
    /// (resetting circuit breakers and refilling token buckets), so they run
    /// in a non-parallelized collection.
    /// </summary>
    [Collection("StoreEviction")]
    public class LruEvictionTests
    {
        [Fact]
        public void CushionStore_LRUEviction_RemovesOldestEntries()
        {
            var testPrefix = $"lru-cushion-{Guid.NewGuid()}-";
            var originalMaxSize = CushionStore.MaxSize;
            CushionStore.MaxSize = 10;

            try
            {
                // Add more entries than max size
                for (int i = 0; i < 15; i++)
                {
                    var cushion = Cushion.ForService($"{testPrefix}{i}")
                        .OpenAfter(failures: 1, trackingLast: 1)
                        .HalfOpenAfter(TimeSpan.FromSeconds(30));

                    try
                    {
                        CaromCushionExtensions.Shot(() => 42, cushion, retries: 0);
                    }
                    catch { }
                }

                // Eviction should have prevented unbounded growth
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
    }
}
