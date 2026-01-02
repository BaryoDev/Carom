using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using Carom.Extensions;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Carom.Benchmarks
{
    /// <summary>
    /// Enhanced benchmarks with realistic scenarios and different load levels.
    /// Tests performance under light, medium, and heavy load conditions.
    /// </summary>
    [MemoryDiagnoser]
    [SimpleJob(warmupCount: 3, iterationCount: 10)]
    public class EnhancedResilienceBenchmarks
    {
        private readonly Random _random = new Random(42);
        private int _counter;

        #region Light Load - Single Operation Benchmarks

        /// <summary>
        /// Light load: Single successful retry operation
        /// Simulates typical API call that succeeds on first attempt
        /// </summary>
        [Benchmark(Baseline = true)]
        [BenchmarkCategory("LightLoad")]
        public int LightLoad_SingleSuccess()
        {
            return global::Carom.Carom.Shot(() => ++_counter, retries: 3);
        }

        /// <summary>
        /// Light load: Single retry needed (transient failure)
        /// Simulates API call that succeeds on second attempt
        /// </summary>
        [Benchmark]
        [BenchmarkCategory("LightLoad")]
        public int LightLoad_TransientFailure()
        {
            var attempts = 0;
            return global::Carom.Carom.Shot(() =>
            {
                attempts++;
                if (attempts == 1)
                {
                    throw new InvalidOperationException("Transient failure");
                }
                return ++_counter;
            }, retries: 3, baseDelay: TimeSpan.FromMilliseconds(1));
        }

        /// <summary>
        /// Light load: Async operation with minimal delay
        /// Simulates fast async API call
        /// </summary>
        [Benchmark]
        [BenchmarkCategory("LightLoad")]
        public async Task<int> LightLoad_AsyncSuccess()
        {
            return await global::Carom.Carom.ShotAsync(
                async () =>
                {
                    await Task.Yield();
                    return ++_counter;
                },
                retries: 3);
        }

        #endregion

        #region Medium Load - Multiple Operations

        /// <summary>
        /// Medium load: 10 sequential operations
        /// Simulates batch processing with moderate throughput
        /// </summary>
        [Benchmark]
        [BenchmarkCategory("MediumLoad")]
        public int MediumLoad_Sequential()
        {
            var result = 0;
            for (int i = 0; i < 10; i++)
            {
                result = global::Carom.Carom.Shot(() => ++_counter, retries: 3);
            }
            return result;
        }

        /// <summary>
        /// Medium load: 10 operations with intermittent failures
        /// Simulates realistic API with 30% transient failure rate
        /// </summary>
        [Benchmark]
        [BenchmarkCategory("MediumLoad")]
        public async Task<int> MediumLoad_IntermittentFailures()
        {
            var result = 0;
            for (int i = 0; i < 10; i++)
            {
                var localI = i;
                result = await global::Carom.Carom.ShotAsync(
                    async () =>
                    {
                        await Task.Yield();
                        // 30% failure rate on first attempt
                        if (localI % 3 == 0 && _counter % 2 == 0)
                        {
                            throw new InvalidOperationException("Intermittent failure");
                        }
                        return ++_counter;
                    },
                    retries: 3,
                    baseDelay: TimeSpan.FromMilliseconds(1));
            }
            return result;
        }

        /// <summary>
        /// Medium load: 10 parallel operations
        /// Simulates concurrent API calls
        /// </summary>
        [Benchmark]
        [BenchmarkCategory("MediumLoad")]
        public async Task<int> MediumLoad_Parallel()
        {
            var tasks = new List<Task<int>>();
            for (int i = 0; i < 10; i++)
            {
                tasks.Add(global::Carom.Carom.ShotAsync(
                    async () =>
                    {
                        await Task.Yield();
                        return Interlocked.Increment(ref _counter);
                    },
                    retries: 3));
            }
            var results = await Task.WhenAll(tasks);
            return results[results.Length - 1];
        }

        #endregion

        #region Heavy Load - High Throughput

        /// <summary>
        /// Heavy load: 100 sequential operations
        /// Simulates high-throughput batch processing
        /// </summary>
        [Benchmark]
        [BenchmarkCategory("HeavyLoad")]
        public int HeavyLoad_Sequential()
        {
            var result = 0;
            for (int i = 0; i < 100; i++)
            {
                result = global::Carom.Carom.Shot(() => ++_counter, retries: 3);
            }
            return result;
        }

        /// <summary>
        /// Heavy load: 50 parallel operations with varying delays
        /// Simulates realistic microservices scenario with network latency
        /// </summary>
        [Benchmark]
        [BenchmarkCategory("HeavyLoad")]
        public async Task<int> HeavyLoad_ParallelVaryingLatency()
        {
            var tasks = new List<Task<int>>();
            for (int i = 0; i < 50; i++)
            {
                int taskId = i;
                tasks.Add(global::Carom.Carom.ShotAsync(
                    async () =>
                    {
                        // Simulate varying network latency (1-5ms)
                        var delayMs = (taskId % 5) + 1;
                        await Task.Delay(delayMs);
                        return Interlocked.Increment(ref _counter);
                    },
                    retries: 3,
                    baseDelay: TimeSpan.FromMilliseconds(1)));
            }
            var results = await Task.WhenAll(tasks);
            return results[results.Length - 1];
        }

        /// <summary>
        /// Heavy load: 100 operations with mixed success/failure
        /// Simulates high-throughput with realistic failure patterns
        /// </summary>
        [Benchmark]
        [BenchmarkCategory("HeavyLoad")]
        public async Task<int> HeavyLoad_MixedResults()
        {
            var successCount = 0;
            var tasks = new List<Task>();
            for (int i = 0; i < 100; i++)
            {
                int taskId = i;
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        await global::Carom.Carom.ShotAsync(
                            async () =>
                            {
                                await Task.Yield();
                                // 10% failure rate
                                if (taskId % 10 == 0)
                                {
                                    throw new InvalidOperationException("Transient failure");
                                }
                                Interlocked.Increment(ref successCount);
                            },
                            retries: 2,
                            baseDelay: TimeSpan.FromMilliseconds(1));
                    }
                    catch
                    {
                        // Expected for some operations
                    }
                }));
            }
            await Task.WhenAll(tasks);
            return successCount;
        }

        #endregion

        #region Memory Allocation Tests

        /// <summary>
        /// Tests memory allocation for simple retry operation
        /// </summary>
        [Benchmark]
        [BenchmarkCategory("Memory")]
        public int Memory_SimpleRetry()
        {
            return global::Carom.Carom.Shot(() => ++_counter, retries: 3);
        }

        /// <summary>
        /// Tests memory allocation for retry with timeout
        /// </summary>
        [Benchmark]
        [BenchmarkCategory("Memory")]
        public async Task<int> Memory_RetryWithTimeout()
        {
            return await global::Carom.Carom.ShotAsync(
                async () =>
                {
                    await Task.Yield();
                    return ++_counter;
                },
                retries: 3,
                timeout: TimeSpan.FromSeconds(5));
        }

        /// <summary>
        /// Tests memory allocation for retry with custom predicate
        /// </summary>
        [Benchmark]
        [BenchmarkCategory("Memory")]
        public int Memory_RetryWithPredicate()
        {
            return global::Carom.Carom.Shot(
                () => ++_counter,
                retries: 3,
                shouldBounce: ex => ex is InvalidOperationException);
        }

        #endregion

        #region Critical Path Tests

        /// <summary>
        /// Simulates critical path: authentication flow with retry
        /// Fast path optimization test
        /// </summary>
        [Benchmark]
        [BenchmarkCategory("CriticalPath")]
        public async Task<string> CriticalPath_Authentication()
        {
            return await global::Carom.Carom.ShotAsync(
                async () =>
                {
                    await Task.Yield();
                    // Simulate auth token retrieval
                    return $"token_{_counter++}";
                },
                retries: 2,
                baseDelay: TimeSpan.FromMilliseconds(50));
        }

        /// <summary>
        /// Simulates critical path: database query with retry
        /// </summary>
        [Benchmark]
        [BenchmarkCategory("CriticalPath")]
        public async Task<int> CriticalPath_DatabaseQuery()
        {
            return await global::Carom.Carom.ShotAsync(
                async () =>
                {
                    // Simulate database query
                    await Task.Delay(2);
                    return ++_counter;
                },
                retries: 3,
                baseDelay: TimeSpan.FromMilliseconds(10));
        }

        /// <summary>
        /// Simulates critical path: external API call with fallback
        /// </summary>
        [Benchmark]
        [BenchmarkCategory("CriticalPath")]
        public async Task<int> CriticalPath_ExternalApiWithFallback()
        {
            var result = await new Func<Task<int>>(async () =>
            {
                return await global::Carom.Carom.ShotAsync(
                    async () =>
                    {
                        await Task.Delay(1);
                        // Simulate 20% failure rate
                        if (_counter % 5 == 0)
                        {
                            throw new InvalidOperationException("API unavailable");
                        }
                        return ++_counter;
                    },
                    retries: 2,
                    baseDelay: TimeSpan.FromMilliseconds(10));
            }).PocketAsync(999);
            
            return result;
        }

        #endregion
    }
}
