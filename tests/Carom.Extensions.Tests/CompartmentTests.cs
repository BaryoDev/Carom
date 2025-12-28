using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Carom.Extensions;

namespace Carom.Extensions.Tests
{
    public class CompartmentTests
    {
        public CompartmentTests()
        {
            CompartmentStore.Clear();
        }

        #region Configuration Tests

        [Fact]
        public void ForResource_CreatesBuilder_WithValidResourceKey()
        {
            var builder = Compartment.ForResource("test-resource");
            Assert.NotNull(builder);
        }

        [Fact]
        public void Build_ThrowsArgumentException_WhenResourceKeyIsEmpty()
        {
            Assert.Throws<ArgumentException>(() => 
                Compartment.ForResource("")
                    .WithMaxConcurrency(10)
                    .Build());
        }

        [Fact]
        public void Build_ThrowsArgumentException_WhenMaxConcurrencyIsZero()
        {
            Assert.Throws<ArgumentException>(() => 
                Compartment.ForResource("test")
                    .WithMaxConcurrency(0)
                    .Build());
        }

        [Fact]
        public void Build_ThrowsArgumentException_WhenQueueDepthIsNegative()
        {
            Assert.Throws<ArgumentException>(() => 
                Compartment.ForResource("test")
                    .WithQueueDepth(-1)
                    .Build());
        }

        #endregion

        #region Concurrency Control Tests

        [Fact]
        public async Task ExecuteAsync_AllowsConcurrentExecutions_UpToLimit()
        {
            var compartment = Compartment.ForResource("concurrent-test")
                .WithMaxConcurrency(3)
                .Build();

            var activeCount = 0;
            var maxObserved = 0;
            var tasks = Enumerable.Range(0, 10).Select(async i =>
            {
                try
                {
                    await CaromCompartmentExtensions.ShotAsync(
                        async () =>
                        {
                            var current = Interlocked.Increment(ref activeCount);
                            var max = maxObserved;
                            while (current > max)
                            {
                                max = Interlocked.CompareExchange(ref maxObserved, current, max);
                                if (max == current) break;
                                max = maxObserved;
                            }

                            await Task.Delay(50);
                            Interlocked.Decrement(ref activeCount);
                            return i;
                        },
                        compartment,
                        retries: 0);
                }
                catch (CompartmentFullException)
                {
                    // Expected when full
                }
            });

            await Task.WhenAll(tasks);

            Assert.True(maxObserved <= 3, $"Max concurrent should be <= 3, was {maxObserved}");
        }

        [Fact]
        public async Task Execute_ThrowsCompartmentFullException_WhenFull()
        {
            var compartment = Compartment.ForResource("full-test")
                .WithMaxConcurrency(1)
                .Build();

            var gate = new ManualResetEventSlim(false);
            var task = Task.Run(() =>
            {
                CaromCompartmentExtensions.Shot(
                    () =>
                    {
                        gate.Wait();
                        return 1;
                    },
                    compartment,
                    retries: 0);
            });

            Thread.Sleep(50); // Let first task acquire

            var ex = Assert.Throws<CompartmentFullException>(() =>
            {
                CaromCompartmentExtensions.Shot(() => 2, compartment, retries: 0);
            });

            Assert.Equal("full-test", ex.ResourceKey);
            Assert.Equal(1, ex.MaxConcurrency);

            gate.Set();
            await task;
        }

        [Fact]
        public async Task ExecuteAsync_ReleasesSlot_AfterCompletion()
        {
            var compartment = Compartment.ForResource("release-test")
                .WithMaxConcurrency(1)
                .Build();

            // First execution
            var result1 = await CaromCompartmentExtensions.ShotAsync(
                async () =>
                {
                    await Task.Delay(10);
                    return 42;
                },
                compartment,
                retries: 0);

            // Second execution should succeed (slot released)
            var result2 = await CaromCompartmentExtensions.ShotAsync(
                async () =>
                {
                    await Task.Delay(10);
                    return 99;
                },
                compartment,
                retries: 0);

            Assert.Equal(42, result1);
            Assert.Equal(99, result2);
        }

        #endregion

        #region Exception Handling Tests

        [Fact]
        public async Task ExecuteAsync_ReleasesSlot_OnException()
        {
            var compartment = Compartment.ForResource("exception-test")
                .WithMaxConcurrency(1)
                .Build();

            // First execution throws
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await CaromCompartmentExtensions.ShotAsync<int>(
                    async () =>
                    {
                        await Task.Delay(10);
                        throw new InvalidOperationException("Test error");
                    },
                    compartment,
                    retries: 0);
            });

            // Second execution should succeed (slot was released)
            var result = await CaromCompartmentExtensions.ShotAsync(
                async () =>
                {
                    await Task.Delay(10);
                    return 42;
                },
                compartment,
                retries: 0);

            Assert.Equal(42, result);
        }

        #endregion

        #region Integration Tests

        [Fact]
        public async Task Shot_WithBounce_WorksWithCompartment()
        {
            var compartment = Compartment.ForResource("bounce-test")
                .WithMaxConcurrency(5)
                .Build();

            var bounce = Bounce.Times(2)
                .WithDelay(TimeSpan.FromMilliseconds(10));

            var result = await CaromCompartmentExtensions.ShotAsync(
                async () =>
                {
                    await Task.Delay(10);
                    return "success";
                },
                compartment,
                bounce);

            Assert.Equal("success", result);
        }

        #endregion

        #region CompartmentFullException Tests

        [Fact]
        public void CompartmentFullException_ContainsResourceKey()
        {
            var exception = new CompartmentFullException("my-resource", 10);
            
            Assert.Equal("my-resource", exception.ResourceKey);
            Assert.Equal(10, exception.MaxConcurrency);
            Assert.Contains("my-resource", exception.Message);
            Assert.Contains("10", exception.Message);
        }

        [Fact]
        public void CompartmentFullException_WithInnerException_PreservesInner()
        {
            var inner = new InvalidOperationException("Original error");
            var exception = new CompartmentFullException("my-resource", 5, inner);
            
            Assert.Equal("my-resource", exception.ResourceKey);
            Assert.Same(inner, exception.InnerException);
        }

        #endregion
    }
}
