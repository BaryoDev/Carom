using BenchmarkDotNet.Attributes;
using Carom.Extensions;

namespace Carom.Benchmarks
{
    /// <summary>
    /// Benchmarks for Rate Limiting (Throttle) pattern.
    /// Validates minimal overhead for token check.
    /// </summary>
    [MemoryDiagnoser]
    [SimpleJob(warmupCount: 3, iterationCount: 10)]
    public class RateLimitBenchmarks
    {
        private Throttle _throttle;
        private int _counter;

        [GlobalSetup]
        public void Setup()
        {
            _throttle = Throttle.ForService($"benchmark-{Guid.NewGuid()}")
                .WithRate(1000000, TimeSpan.FromSeconds(1))  // Very high limit
                .WithBurst(1000000)
                .Build();
        }

        [Benchmark(Baseline = true)]
        public async Task<int> Throttle_TokenAvailable()
        {
            return await CaromThrottleExtensions.ShotAsync(
                async () =>
                {
                    await Task.Delay(1);
                    return ++_counter;
                },
                _throttle,
                retries: 0);
        }

        [Benchmark]
        public async Task<int> DirectCall_NoThrottle()
        {
            await Task.Delay(1);
            return ++_counter;
        }
    }
}
