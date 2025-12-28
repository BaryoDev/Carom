using BenchmarkDotNet.Attributes;
using Carom.Extensions;

namespace Carom.Benchmarks
{
    /// <summary>
    /// Benchmarks for Bulkhead (Compartment) pattern.
    /// Validates minimal overhead when semaphore available.
    /// </summary>
    [MemoryDiagnoser]
    [SimpleJob(warmupCount: 3, iterationCount: 10)]
    public class BulkheadBenchmarks
    {
        private Compartment _compartment;
        private int _counter;

        [GlobalSetup]
        public void Setup()
        {
            _compartment = Compartment.ForResource($"benchmark-{Guid.NewGuid()}")
                .WithMaxConcurrency(100)  // High limit to avoid blocking
                .Build();
        }

        [Benchmark(Baseline = true)]
        public async Task<int> Compartment_Available()
        {
            return await CaromCompartmentExtensions.ShotAsync(
                async () =>
                {
                    await Task.Delay(1);
                    return ++_counter;
                },
                _compartment,
                retries: 0);
        }

        [Benchmark]
        public async Task<int> DirectCall_NoCompartment()
        {
            await Task.Delay(1);
            return ++_counter;
        }
    }
}
