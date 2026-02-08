using System;
using System.Threading;
using System.Threading.Tasks;
using Carom.DependencyInjection;
using Carom.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Carom.DependencyInjection.Tests
{
    /// <summary>
    /// Tests for ResiliencePipeline and DI integration.
    /// </summary>
    public class ResiliencePipelineTests
    {
        [Fact]
        public void ResiliencePipeline_Execute_WorksWithNoStrategies()
        {
            var pipeline = new ResiliencePipelineBuilder("test")
                .Build();

            var result = pipeline.Execute(() => 42);

            Assert.Equal(42, result);
        }

        [Fact]
        public void ResiliencePipeline_ChainedStrategies_ExecutesInOrder()
        {
            var pipeline = new ResiliencePipelineBuilder("test")
                .AddRetry(3)
                .AddTimeout(TimeSpan.FromSeconds(30))
                .Build();

            var attempts = 0;
            var result = pipeline.Execute(() =>
            {
                attempts++;
                if (attempts < 3)
                    throw new InvalidOperationException("Transient");
                return "success";
            });

            Assert.Equal("success", result);
            Assert.Equal(3, attempts);
        }

        [Fact]
        public async Task ResiliencePipeline_ExecuteAsync_Works()
        {
            var pipeline = new ResiliencePipelineBuilder("test")
                .AddRetry(2)
                .Build();

            var attempts = 0;
            var result = await pipeline.ExecuteAsync(async ct =>
            {
                await Task.Yield();
                attempts++;
                if (attempts < 2)
                    throw new InvalidOperationException("Transient");
                return 42;
            });

            Assert.Equal(42, result);
            Assert.Equal(2, attempts);
        }

        [Fact]
        public void ResiliencePipeline_WithFallback_ReturnsDefaultOnFailure()
        {
            var pipeline = new ResiliencePipelineBuilder("test")
                .AddRetry(1)
                .AddFallback<int>(ex => -1)
                .Build();

            var result = pipeline.Execute<int>(() =>
            {
                throw new InvalidOperationException("Always fails");
            });

            Assert.Equal(-1, result);
        }

        [Fact]
        public void ResiliencePipeline_Timeout_ThrowsTimeoutException()
        {
            var pipeline = new ResiliencePipelineBuilder("test")
                .AddTimeout(TimeSpan.FromMilliseconds(50))
                .Build();

            Assert.Throws<TimeoutException>(() =>
            {
                pipeline.Execute(() =>
                {
                    Thread.Sleep(200);
                    return "never";
                });
            });
        }

        [Fact]
        public async Task ResiliencePipeline_AsyncTimeout_ThrowsTimeoutException()
        {
            var pipeline = new ResiliencePipelineBuilder("test")
                .AddTimeout(TimeSpan.FromMilliseconds(50))
                .Build();

            await Assert.ThrowsAsync<TimeoutException>(async () =>
            {
                await pipeline.ExecuteAsync(async ct =>
                {
                    await Task.Delay(200, ct);
                    return "never";
                });
            });
        }

        [Fact]
        public void ServiceCollection_AddCaromResilience_RegistersPipeline()
        {
            var services = new ServiceCollection();

            services.AddCaromResilience("payment-api", builder => builder
                .AddRetry(3)
                .AddTimeout(TimeSpan.FromSeconds(30)));

            var provider = services.BuildServiceProvider();
            var pipeline = provider.GetRequiredService<ResiliencePipeline>();

            Assert.NotNull(pipeline);
            Assert.Equal("payment-api", pipeline.Name);
        }

        [Fact]
        public void ServiceCollection_AddCaromResilienceRegistry_AllowsNamedLookup()
        {
            var services = new ServiceCollection();

            services.AddCaromResilienceRegistry();
            services.AddCaromResilience("api-1", b => b.AddRetry(1));
            services.AddCaromResilience("api-2", b => b.AddRetry(2));

            var provider = services.BuildServiceProvider();
            var registry = provider.GetRequiredService<IResiliencePipelineRegistry>();

            Assert.True(registry.TryGetPipeline("api-1", out var pipeline1));
            Assert.True(registry.TryGetPipeline("api-2", out var pipeline2));
            Assert.NotNull(pipeline1);
            Assert.NotNull(pipeline2);
        }

        [Fact]
        public void ResiliencePipelineBuilder_AddCircuitBreaker_Works()
        {
            var pipeline = new ResiliencePipelineBuilder("test")
                .AddCircuitBreaker("test-service", failureThreshold: 5, samplingWindow: 10)
                .Build();

            // Should execute successfully
            var result = pipeline.Execute(() => "success");
            Assert.Equal("success", result);
        }

        [Fact]
        public void ResiliencePipelineBuilder_AddBulkhead_Works()
        {
            var pipeline = new ResiliencePipelineBuilder("test")
                .AddBulkhead("test-resource", maxConcurrency: 5, queueDepth: 10)
                .Build();

            var result = pipeline.Execute(() => 42);
            Assert.Equal(42, result);
        }

        [Fact]
        public void ResiliencePipelineBuilder_AddRateLimit_Works()
        {
            var pipeline = new ResiliencePipelineBuilder("test")
                .AddRateLimit("test-service", maxRequests: 100, timeWindow: TimeSpan.FromMinutes(1))
                .Build();

            var result = pipeline.Execute(() => "rate-limited");
            Assert.Equal("rate-limited", result);
        }

        [Fact]
        public void ResiliencePipeline_ComplexChain_ExecutesCorrectly()
        {
            var pipeline = new ResiliencePipelineBuilder("complex")
                .AddRetry(Bounce.Times(3).WithDelay(TimeSpan.FromMilliseconds(1)))
                .AddCircuitBreaker("complex-service", 5, 10)
                .AddTimeout(TimeSpan.FromSeconds(10))
                .AddFallback<string>(ex => "fallback")
                .Build();

            // First call succeeds
            var result = pipeline.Execute<string>(() => "success");
            Assert.Equal("success", result);
        }

        [Fact]
        public async Task ResiliencePipeline_VoidAction_Works()
        {
            var pipeline = new ResiliencePipelineBuilder("test")
                .AddRetry(2)
                .Build();

            var executed = false;
            pipeline.Execute(() => { executed = true; });
            Assert.True(executed);

            var asyncExecuted = false;
            await pipeline.ExecuteAsync(async () =>
            {
                await Task.Yield();
                asyncExecuted = true;
            });
            Assert.True(asyncExecuted);
        }

        [Fact]
        public void ResiliencePipelineRegistry_GetPipeline_ThrowsWhenNotFound()
        {
            var registry = new ResiliencePipelineRegistry();

            Assert.Throws<KeyNotFoundException>(() => registry.GetPipeline("nonexistent"));
        }

        [Fact]
        public void ServiceCollection_AddCaromResilience_MultiPipeline_Works()
        {
            var services = new ServiceCollection();

            services.AddCaromResilience(config =>
            {
                config.AddPipeline("api-1", b => b.AddRetry(1));
                config.AddPipeline("api-2", b => b.AddRetry(2).AddTimeout(TimeSpan.FromSeconds(5)));
            });

            var provider = services.BuildServiceProvider();
            var pipelines = provider.GetServices<ResiliencePipeline>();

            Assert.True(pipelines.Count() >= 2);
        }
    }
}
