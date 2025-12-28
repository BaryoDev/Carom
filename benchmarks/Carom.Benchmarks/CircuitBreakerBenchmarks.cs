using BenchmarkDotNet.Attributes;
using Carom.Extensions;
using Polly;

namespace Carom.Benchmarks
{
    /// <summary>
    /// Benchmarks for Circuit Breaker (Cushion) comparing Carom vs Polly.
    /// </summary>
    [MemoryDiagnoser]
    [SimpleJob(warmupCount: 3, iterationCount: 10)]
    public class CircuitBreakerBenchmarks
    {
        private Cushion _caromCushion;
        private ResiliencePipeline _pollyPipeline = null!;
        private int _counter;

        [GlobalSetup]
        public void Setup()
        {
            _caromCushion = Cushion.ForService($"benchmark-{Guid.NewGuid()}")
                .OpenAfter(5, 10)
                .HalfOpenAfter(TimeSpan.FromSeconds(30));

            _pollyPipeline = new ResiliencePipelineBuilder()
                .Build();  // Simple pipeline for comparison
        }

        [Benchmark(Baseline = true)]
        public int Carom_CircuitClosed_Success()
        {
            return CaromCushionExtensions.Shot(() => ++_counter, _caromCushion, retries: 0);
        }

        [Benchmark]
        public int Polly_Pipeline_Success()
        {
            return _pollyPipeline.Execute(() => ++_counter);
        }

        [Benchmark]
        public int Carom_DirectShot_NoCircuit()
        {
            return global::Carom.Carom.Shot(() => ++_counter, retries: 0);
        }
    }
}
