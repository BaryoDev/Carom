using BenchmarkDotNet.Attributes;

namespace Carom.Benchmarks
{
    /// <summary>
    /// Benchmarks for Timeout feature.
    /// Validates zero allocations when timeout not specified.
    /// </summary>
    [MemoryDiagnoser]
    [SimpleJob(warmupCount: 3, iterationCount: 10)]
    public class TimeoutBenchmarks
    {
        private int _counter;

        [Benchmark(Baseline = true)]
        public async Task<int> ShotAsync_WithoutTimeout()
        {
            return await Carom.ShotAsync(
                async () =>
                {
                    await Task.Delay(1);
                    return ++_counter;
                },
                retries: 0);
        }

        [Benchmark]
        public async Task<int> ShotAsync_WithTimeout()
        {
            return await Carom.ShotAsync(
                async () =>
                {
                    await Task.Delay(1);
                    return ++_counter;
                },
                retries: 0,
                timeout: TimeSpan.FromSeconds(5));
        }

        [Benchmark]
        public async Task<int> Bounce_WithoutTimeout()
        {
            var bounce = Bounce.Times(0);
            return await Carom.ShotAsync(
                async () =>
                {
                    await Task.Delay(1);
                    return ++_counter;
                },
                bounce);
        }

        [Benchmark]
        public async Task<int> Bounce_WithTimeout()
        {
            var bounce = Bounce.Times(0).WithTimeout(TimeSpan.FromSeconds(5));
            return await Carom.ShotAsync(
                async () =>
                {
                    await Task.Delay(1);
                    return ++_counter;
                },
                bounce);
        }
    }
}
