using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Carom.Extensions;

namespace Carom.Extensions.Tests
{
    /// <summary>
    /// Tests for the CaromHooks signals raised by Carom.Extensions. Hooks are
    /// process-wide mutable state, so every test saves the prior hook and restores
    /// it in a finally block, all hook-mutating tests live in this one class so
    /// xunit serializes them, and subscribers filter by a GUID service or resource
    /// key so signals from tests running in parallel cannot leak in.
    /// </summary>
    public class CaromHooksExtensionsTests
    {
        [Fact]
        public void CircuitOpened_FiresOncePerTransition_NotPerFailure()
        {
            var key = "hooks-circuit-" + Guid.NewGuid();
            var cushion = Cushion.ForService(key)
                .OpenAfter(2, 10)
                .HalfOpenAfter(TimeSpan.FromHours(1));

            var prior = CaromHooks.OnCircuitOpened;
            try
            {
                var opened = new List<CircuitOpenedSignal>();
                CaromHooks.OnCircuitOpened = s =>
                {
                    if (s.ServiceKey == key)
                    {
                        lock (opened) opened.Add(s);
                    }
                };

                // Two failures open the circuit; two more calls hit the open circuit.
                for (int i = 0; i < 4; i++)
                {
                    try
                    {
                        CaromCushionExtensions.Shot<int>(
                            static () => throw new InvalidOperationException("boom"),
                            cushion,
                            retries: 0);
                    }
                    catch (InvalidOperationException) { }
                    catch (CircuitOpenException) { }
                }

                var signal = Assert.Single(opened);
                Assert.Equal(key, signal.ServiceKey);
            }
            finally
            {
                CaromHooks.OnCircuitOpened = prior;
            }
        }

        [Fact]
        public async Task BulkheadRejected_FiresOnRejection()
        {
            var key = "hooks-bulkhead-" + Guid.NewGuid();
            var compartment = Compartment.ForResource(key)
                .WithMaxConcurrency(1)
                .Build();

            var prior = CaromHooks.OnBulkheadRejected;
            try
            {
                var rejected = new List<BulkheadRejectedSignal>();
                CaromHooks.OnBulkheadRejected = s =>
                {
                    if (s.ResourceKey == key)
                    {
                        lock (rejected) rejected.Add(s);
                    }
                };

                using var release = new SemaphoreSlim(0);
                using var entered = new ManualResetEventSlim(false);

                // Hold the single slot from another thread.
                var holder = Task.Run(() =>
                    CaromCompartmentExtensions.Shot<int>(
                        () => { entered.Set(); release.Wait(); return 1; },
                        compartment,
                        retries: 0));

                Assert.True(entered.Wait(TimeSpan.FromSeconds(5)), "Holder never entered the compartment.");

                Assert.Throws<CompartmentFullException>(() =>
                    CaromCompartmentExtensions.Shot(() => 2, compartment, retries: 0));

                var signal = Assert.Single(rejected);
                Assert.Equal(key, signal.ResourceKey);

                release.Release();
                await holder;
            }
            finally
            {
                CaromHooks.OnBulkheadRejected = prior;
            }
        }

        [Fact]
        public void RateLimitRejected_FiresOnRejection()
        {
            var key = "hooks-throttle-" + Guid.NewGuid();
            var throttle = Throttle.ForService(key)
                .WithRate(1, TimeSpan.FromHours(1))
                .WithBurst(1)
                .Build();

            var prior = CaromHooks.OnRateLimitRejected;
            try
            {
                var rejected = new List<RateLimitRejectedSignal>();
                CaromHooks.OnRateLimitRejected = s =>
                {
                    if (s.ServiceKey == key)
                    {
                        lock (rejected) rejected.Add(s);
                    }
                };

                // First call consumes the only token, second is rejected.
                CaromThrottleExtensions.Shot(() => 1, throttle, retries: 0);
                Assert.Throws<ThrottledException>(() =>
                    CaromThrottleExtensions.Shot(() => 2, throttle, retries: 0));

                var signal = Assert.Single(rejected);
                Assert.Equal(key, signal.ServiceKey);
            }
            finally
            {
                CaromHooks.OnRateLimitRejected = prior;
            }
        }
        [Fact]
        public void AThrowingCircuitHook_DoesNotReplaceTheCallersException()
        {
            var prior = CaromHooks.OnCircuitOpened;
            try
            {
                var key = "throwhook-" + Guid.NewGuid();
                CaromHooks.OnCircuitOpened = s =>
                {
                    if (s.ServiceKey == key) throw new InvalidOperationException("subscriber is broken");
                };

                var cushion = Cushion.ForService(key)
                    .OpenAfter(failures: 1, trackingLast: 1)
                    .HalfOpenAfter(TimeSpan.FromSeconds(30));

                // Unguarded, the caller sees InvalidOperationException instead of its own failure.
                Assert.Throws<InvalidTimeZoneException>(() =>
                    CaromCushionExtensions.Shot<int>(
                        () => throw new InvalidTimeZoneException("downstream is sick"),
                        cushion,
                        retries: 0));

                Assert.Equal(CircuitState.Open, Cushion.GetState(key));
            }
            finally
            {
                CaromHooks.OnCircuitOpened = prior;
            }
        }

        [Fact]
        public void AThrowingBulkheadHook_DoesNotReplaceTheRejection()
        {
            var prior = CaromHooks.OnBulkheadRejected;
            try
            {
                var key = "throwhook-" + Guid.NewGuid();
                CaromHooks.OnBulkheadRejected = s =>
                {
                    if (s.ResourceKey == key) throw new InvalidOperationException("subscriber is broken");
                };

                var comp = Compartment.ForResource(key).WithMaxConcurrency(1).Build();
                var release = new ManualResetEventSlim(false);
                var entered = new ManualResetEventSlim(false);
                var holder = Task.Run(() => CaromCompartmentExtensions.Shot(
                    () => { entered.Set(); release.Wait(); return 1; }, comp, retries: 0));
                Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));

                try
                {
                    Assert.Throws<CompartmentFullException>(() =>
                        CaromCompartmentExtensions.Shot(() => 1, comp, retries: 0));
                }
                finally
                {
                    release.Set();
                    holder.Wait(TimeSpan.FromSeconds(5));
                }
            }
            finally
            {
                CaromHooks.OnBulkheadRejected = prior;
            }
        }

        [Fact]
        public void AThrowingThrottleHook_DoesNotReplaceTheRejection()
        {
            var prior = CaromHooks.OnRateLimitRejected;
            try
            {
                var key = "throwhook-" + Guid.NewGuid();
                CaromHooks.OnRateLimitRejected = s =>
                {
                    if (s.ServiceKey == key) throw new InvalidOperationException("subscriber is broken");
                };

                var throttle = Throttle.ForService(key)
                    .WithRate(1, TimeSpan.FromMinutes(10)).WithBurst(1).Build();

                CaromThrottleExtensions.Shot(() => 1, throttle, retries: 0);
                Assert.Throws<ThrottledException>(() =>
                    CaromThrottleExtensions.Shot(() => 1, throttle, retries: 0));
            }
            finally
            {
                CaromHooks.OnRateLimitRejected = prior;
            }
        }

    }
}
