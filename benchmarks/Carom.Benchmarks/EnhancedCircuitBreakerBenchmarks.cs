using BenchmarkDotNet.Attributes;
using Carom.Extensions;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Carom.Benchmarks
{
    /// <summary>
    /// Enhanced Circuit Breaker (Cushion) benchmarks with realistic scenarios.
    /// Tests performance under different circuit states and load conditions.
    /// </summary>
    [MemoryDiagnoser]
    [SimpleJob(warmupCount: 3, iterationCount: 10)]
    public class EnhancedCircuitBreakerBenchmarks
    {
        private Cushion _closedCircuit;
        private Cushion _openCircuit;
        private int _counter;

        [GlobalSetup]
        public void Setup()
        {
            // Circuit that stays closed
            _closedCircuit = Cushion.ForService($"closed-{Guid.NewGuid()}")
                .OpenAfter(100, 200)
                .HalfOpenAfter(TimeSpan.FromSeconds(30));

            // Circuit that will be open
            _openCircuit = Cushion.ForService($"open-{Guid.NewGuid()}")
                .OpenAfter(2, 2)
                .HalfOpenAfter(TimeSpan.FromSeconds(30));

            // Open the circuit
            for (int i = 0; i < 2; i++)
            {
                try
                {
                    CaromCushionExtensions.Shot<int>(
                        () => throw new InvalidOperationException(),
                        _openCircuit,
                        retries: 0);
                }
                catch
                {
                    // Expected
                }
            }
        }

        #region Circuit Closed State Tests

        /// <summary>
        /// Baseline: Single successful operation with circuit closed
        /// </summary>
        [Benchmark(Baseline = true)]
        [BenchmarkCategory("ClosedCircuit")]
        public int ClosedCircuit_SingleSuccess()
        {
            return CaromCushionExtensions.Shot(
                () => ++_counter,
                _closedCircuit,
                retries: 0);
        }

        /// <summary>
        /// Sequential operations with circuit closed
        /// </summary>
        [Benchmark]
        [BenchmarkCategory("ClosedCircuit")]
        public int ClosedCircuit_Sequential()
        {
            var result = 0;
            for (int i = 0; i < 10; i++)
            {
                result = CaromCushionExtensions.Shot(
                    () => ++_counter,
                    _closedCircuit,
                    retries: 0);
            }
            return result;
        }

        /// <summary>
        /// Parallel operations with circuit closed
        /// Tests concurrent access to circuit state
        /// </summary>
        [Benchmark]
        [BenchmarkCategory("ClosedCircuit")]
        public async Task<int> ClosedCircuit_Parallel()
        {
            var tasks = new List<Task<int>>();
            for (int i = 0; i < 10; i++)
            {
                tasks.Add(CaromCushionExtensions.ShotAsync(
                    async () =>
                    {
                        await Task.Yield();
                        return Interlocked.Increment(ref _counter);
                    },
                    _closedCircuit,
                    retries: 0));
            }
            var results = await Task.WhenAll(tasks);
            return results[results.Length - 1];
        }

        /// <summary>
        /// Circuit closed with intermittent failures (staying below threshold)
        /// </summary>
        [Benchmark]
        [BenchmarkCategory("ClosedCircuit")]
        public async Task<int> ClosedCircuit_IntermittentFailures()
        {
            var successCount = 0;
            for (int i = 0; i < 20; i++)
            {
                try
                {
                    await CaromCushionExtensions.ShotAsync(
                        async () =>
                        {
                            await Task.Yield();
                            // 10% failure rate - below threshold
                            if (i % 10 == 0)
                            {
                                throw new InvalidOperationException("Intermittent failure");
                            }
                            return ++successCount;
                        },
                        _closedCircuit,
                        retries: 0);
                }
                catch
                {
                    // Expected occasional failure
                }
            }
            return successCount;
        }

        #endregion

        #region Circuit Open State Tests

        /// <summary>
        /// Fast rejection when circuit is open
        /// Should be extremely fast as it doesn't execute the action
        /// </summary>
        [Benchmark]
        [BenchmarkCategory("OpenCircuit")]
        public int OpenCircuit_FastRejection()
        {
            try
            {
                return CaromCushionExtensions.Shot(
                    () => ++_counter,
                    _openCircuit,
                    retries: 0);
            }
            catch (CircuitOpenException)
            {
                return -1;
            }
        }

        /// <summary>
        /// Multiple rejections when circuit is open
        /// Tests overhead of repeated circuit state checks
        /// </summary>
        [Benchmark]
        [BenchmarkCategory("OpenCircuit")]
        public int OpenCircuit_MultipleRejections()
        {
            var rejectionCount = 0;
            for (int i = 0; i < 10; i++)
            {
                try
                {
                    CaromCushionExtensions.Shot(
                        () => ++_counter,
                        _openCircuit,
                        retries: 0);
                }
                catch (CircuitOpenException)
                {
                    rejectionCount++;
                }
            }
            return rejectionCount;
        }

        /// <summary>
        /// Parallel rejections when circuit is open
        /// Tests concurrent rejection performance
        /// </summary>
        [Benchmark]
        [BenchmarkCategory("OpenCircuit")]
        public async Task<int> OpenCircuit_ParallelRejections()
        {
            var rejectionCount = 0;
            var tasks = new List<Task>();
            for (int i = 0; i < 20; i++)
            {
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        await CaromCushionExtensions.ShotAsync(
                            async () =>
                            {
                                await Task.Yield();
                                return ++_counter;
                            },
                            _openCircuit,
                            retries: 0);
                    }
                    catch (CircuitOpenException)
                    {
                        Interlocked.Increment(ref rejectionCount);
                    }
                }));
            }
            await Task.WhenAll(tasks);
            return rejectionCount;
        }

        #endregion

        #region Realistic Scenarios

        /// <summary>
        /// Simulates API gateway with circuit breaker
        /// Multiple services with varying reliability
        /// </summary>
        [Benchmark]
        [BenchmarkCategory("Realistic")]
        public async Task<int> Realistic_ApiGateway()
        {
            var cushion = Cushion.ForService($"api-gateway-{Guid.NewGuid()}")
                .OpenAfter(5, 10)
                .HalfOpenAfter(TimeSpan.FromSeconds(30));

            var successCount = 0;
            for (int i = 0; i < 20; i++)
            {
                try
                {
                    await CaromCushionExtensions.ShotAsync(
                        async () =>
                        {
                            await Task.Delay(1);
                            // 15% failure rate
                            if (i % 7 == 0)
                            {
                                throw new InvalidOperationException("Service unavailable");
                            }
                            return ++successCount;
                        },
                        cushion,
                        retries: 2,
                        baseDelay: TimeSpan.FromMilliseconds(10));
                }
                catch
                {
                    // Circuit may open, causing cascade of failures
                }
            }
            return successCount;
        }

        /// <summary>
        /// Simulates database connection pool with circuit breaker
        /// Tests behavior under database availability issues
        /// </summary>
        [Benchmark]
        [BenchmarkCategory("Realistic")]
        public async Task<int> Realistic_DatabaseConnection()
        {
            var cushion = Cushion.ForService($"database-{Guid.NewGuid()}")
                .OpenAfter(3, 5)
                .HalfOpenAfter(TimeSpan.FromSeconds(30));

            var queryCount = 0;
            var tasks = new List<Task>();
            
            for (int i = 0; i < 15; i++)
            {
                int queryId = i;
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        await CaromCushionExtensions.ShotAsync<int>(
                            async () =>
                            {
                                await Task.Delay(2);
                                // Simulate occasional connection timeout
                                if (queryId % 8 == 0)
                                {
                                    throw new TimeoutException("Connection timeout");
                                }
                                Interlocked.Increment(ref queryCount);
                                return 1;
                            },
                            cushion,
                            retries: 1,
                            baseDelay: TimeSpan.FromMilliseconds(5));
                    }
                    catch
                    {
                        // Expected for some queries
                    }
                }));
            }

            await Task.WhenAll(tasks);
            return queryCount;
        }

        /// <summary>
        /// Simulates microservices communication with circuit breaker
        /// Tests cascading failure prevention
        /// </summary>
        [Benchmark]
        [BenchmarkCategory("Realistic")]
        public async Task<int> Realistic_MicroservicesChain()
        {
            var cushion1 = Cushion.ForService($"service-1-{Guid.NewGuid()}")
                .OpenAfter(4, 8)
                .HalfOpenAfter(TimeSpan.FromSeconds(30));

            var cushion2 = Cushion.ForService($"service-2-{Guid.NewGuid()}")
                .OpenAfter(4, 8)
                .HalfOpenAfter(TimeSpan.FromSeconds(30));

            var successCount = 0;
            for (int i = 0; i < 10; i++)
            {
                try
                {
                    // Call service 1, which calls service 2
                    await CaromCushionExtensions.ShotAsync(
                        async () =>
                        {
                            return await CaromCushionExtensions.ShotAsync(
                                async () =>
                                {
                                    await Task.Delay(1);
                                    // 20% failure in service 2
                                    if (i % 5 == 0)
                                    {
                                        throw new InvalidOperationException("Service 2 error");
                                    }
                                    return ++successCount;
                                },
                                cushion2,
                                retries: 1,
                                baseDelay: TimeSpan.FromMilliseconds(5));
                        },
                        cushion1,
                        retries: 1,
                        baseDelay: TimeSpan.FromMilliseconds(5));
                }
                catch
                {
                    // Expected for some operations
                }
            }
            return successCount;
        }

        #endregion

        #region Memory and Allocation Tests

        /// <summary>
        /// Tests memory allocation for circuit breaker operations
        /// Should show minimal allocations beyond the operation itself
        /// </summary>
        [Benchmark]
        [BenchmarkCategory("Memory")]
        public int Memory_CircuitBreakerOverhead()
        {
            return CaromCushionExtensions.Shot(
                () => ++_counter,
                _closedCircuit,
                retries: 0);
        }

        /// <summary>
        /// Tests memory allocation with bounce configuration
        /// </summary>
        [Benchmark]
        [BenchmarkCategory("Memory")]
        public int Memory_WithBounce()
        {
            var bounce = Bounce.Times(3).WithDelay(TimeSpan.FromMilliseconds(10));
            return CaromCushionExtensions.Shot(
                () => ++_counter,
                _closedCircuit,
                bounce);
        }

        #endregion
    }
}
