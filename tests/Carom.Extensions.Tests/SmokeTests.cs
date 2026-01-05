using System;
using System.Threading.Tasks;
using Xunit;
using Carom.Extensions;

namespace Carom.Extensions.Tests
{
    /// <summary>
    /// Smoke tests for critical Carom Extensions functionality.
    /// These tests verify that basic operations work correctly for all patterns.
    /// </summary>
    public class SmokeTests
    {
        public SmokeTests()
        {
        }

        #region Circuit Breaker Smoke Tests

        [Fact]
        public void Smoke_CircuitBreaker_ClosedState_Works()
        {
            var cushion = Cushion.ForService("smoke-test-cb-" + Guid.NewGuid())
                .OpenAfter(5, 10)
                .HalfOpenAfter(TimeSpan.FromSeconds(30));
            
            var result = CaromCushionExtensions.Shot(() => 42, cushion, retries: 0);
            Assert.Equal(42, result);
        }

        [Fact]
        public void Smoke_CircuitBreaker_OpensOnFailures()
        {
            var cushion = Cushion.ForService("smoke-test-open-" + Guid.NewGuid())
                .OpenAfter(2, 2)
                .HalfOpenAfter(TimeSpan.FromSeconds(30));
            
            // Cause circuit to open
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
            
            // Circuit should be open
            Assert.Throws<CircuitOpenException>(() =>
                CaromCushionExtensions.Shot(() => 42, cushion, retries: 0));
        }

        #endregion

        #region Bulkhead Smoke Tests

        [Fact]
        public async Task Smoke_Bulkhead_AllowsExecution()
        {
            var compartment = Compartment.ForResource("smoke-test-bulkhead-" + Guid.NewGuid())
                .WithMaxConcurrency(10)
                .Build();
            
            var result = await CaromCompartmentExtensions.ShotAsync(
                async () =>
                {
                    await Task.Yield();
                    return 42;
                },
                compartment,
                retries: 0);
            
            Assert.Equal(42, result);
        }

        [Fact]
        public void Smoke_Bulkhead_RejectsWhenFull()
        {
            var compartment = Compartment.ForResource("smoke-test-full-" + Guid.NewGuid())
                .WithMaxConcurrency(1)
                .Build();
            
            var semaphore = new System.Threading.SemaphoreSlim(0);
            var entrySignal = new System.Threading.ManualResetEventSlim(false);
            
            // Fill the compartment
            var task = Task.Run(() =>
                CaromCompartmentExtensions.Shot<int>(
                    () => { entrySignal.Set(); semaphore.Wait(); return 1; },
                    compartment,
                    retries: 0));
            
            entrySignal.Wait(); // Wait until first task has acquired the slot
            
            // Second call should be rejected
            Assert.Throws<CompartmentFullException>(() =>
                CaromCompartmentExtensions.Shot(() => 2, compartment, retries: 0));
            
            semaphore.Release();
            task.Wait();
        }

        #endregion

        #region Rate Limiting Smoke Tests

        [Fact]
        public void Smoke_RateLimiting_AllowsRequests()
        {
            var throttle = Throttle.ForService("smoke-test-throttle-" + Guid.NewGuid())
                .WithRate(100, TimeSpan.FromSeconds(1))
                .Build();
            
            var result = CaromThrottleExtensions.Shot(() => 42, throttle, retries: 0);
            Assert.Equal(42, result);
        }

        [Fact]
        public void Smoke_RateLimiting_EnforcesLimit()
        {
            var throttle = Throttle.ForService("smoke-test-limit-" + Guid.NewGuid())
                .WithRate(2, TimeSpan.FromSeconds(10))
                .WithBurst(2)
                .Build();
            
            // First 2 should succeed
            CaromThrottleExtensions.Shot(() => 1, throttle, retries: 0);
            CaromThrottleExtensions.Shot(() => 2, throttle, retries: 0);
            
            // Third should be throttled
            Assert.Throws<ThrottledException>(() =>
                CaromThrottleExtensions.Shot(() => 3, throttle, retries: 0));
        }

        #endregion

        #region Fallback Smoke Tests

        [Fact]
        public void Smoke_Fallback_ReturnsValue_OnSuccess()
        {
            var result = new Func<int>(() => 42).Pocket(0);
            Assert.Equal(42, result);
        }

        [Fact]
        public void Smoke_Fallback_ReturnsFallback_OnFailure()
        {
            var result = new Func<int>(() => throw new Exception()).Pocket(999);
            Assert.Equal(999, result);
        }

        [Fact]
        public async Task Smoke_FallbackAsync_Works()
        {
            var result = await new Func<Task<int>>(async () =>
            {
                await Task.Yield();
                throw new Exception();
            }).PocketAsync(999);
            
            Assert.Equal(999, result);
        }

        #endregion

        #region Integration Smoke Tests

        [Fact]
        public async Task Smoke_AllPatterns_WorkTogether()
        {
            var cushion = Cushion.ForService("smoke-integration-" + Guid.NewGuid())
                .OpenAfter(10, 20)
                .HalfOpenAfter(TimeSpan.FromSeconds(30));
            
            var throttle = Throttle.ForService("smoke-integration-throttle-" + Guid.NewGuid())
                .WithRate(100, TimeSpan.FromSeconds(1))
                .Build();
            
            var result = await CaromThrottleExtensions.ShotAsync(
                async () =>
                {
                    return await CaromCushionExtensions.ShotAsync(
                        async () =>
                        {
                            await Task.Yield();
                            return 42;
                        },
                        cushion,
                        retries: 2);
                },
                throttle,
                retries: 0);
            
            Assert.Equal(42, result);
        }

        [Fact]
        public async Task Smoke_PatternsWithFallback_Works()
        {
            var cushion = Cushion.ForService("smoke-fallback-" + Guid.NewGuid())
                .OpenAfter(5, 10)
                .HalfOpenAfter(TimeSpan.FromSeconds(30));
            
            var result = await new Func<Task<int>>(async () =>
            {
                return await CaromCushionExtensions.ShotAsync(
                    async () =>
                    {
                        await Task.Yield();
                        return 42;
                    },
                    cushion,
                    retries: 2);
            }).PocketAsync(999);
            
            Assert.True(result == 42 || result == 999);
        }

        #endregion
    }
}
