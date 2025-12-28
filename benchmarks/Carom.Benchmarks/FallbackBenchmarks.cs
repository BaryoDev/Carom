using BenchmarkDotNet.Attributes;
using Carom.Extensions;

namespace Carom.Benchmarks
{
    /// <summary>
    /// Benchmarks for Fallback (Safety Pocket) pattern.
    /// Validates zero allocations on success path.
    /// </summary>
    [MemoryDiagnoser]
    [SimpleJob(warmupCount: 3, iterationCount: 10)]
    public class FallbackBenchmarks
    {
        private int _counter;

        [Benchmark(Baseline = true)]
        public int Pocket_SuccessPath_NoFallback()
        {
            return new Func<int>(() => ++_counter).Pocket(0);
        }

        [Benchmark]
        public int Pocket_FailurePath_WithFallback()
        {
            return new Func<int>(() => throw new InvalidOperationException()).Pocket(999);
        }

        [Benchmark]
        public int DirectCall_NoResilience()
        {
            return ++_counter;
        }

        [Benchmark]
        public int ShotWithPocket_SuccessPath()
        {
            return CaromFallbackExtensions.ShotWithPocket(
                () => ++_counter,
                fallback: 0,
                retries: 0);
        }
    }
}
