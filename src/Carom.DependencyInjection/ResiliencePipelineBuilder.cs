// Copyright (c) BaryoDev. All rights reserved.
// Licensed under the MPL-2.0 license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using Carom.Extensions;

namespace Carom.DependencyInjection
{
    /// <summary>
    /// Fluent builder for creating resilience pipelines.
    /// Strategies are executed in the order they are added.
    /// </summary>
    public class ResiliencePipelineBuilder
    {
        private readonly string _name;
        private readonly List<IResilienceStrategy> _strategies = new List<IResilienceStrategy>();

        /// <summary>
        /// Creates a new builder for the specified pipeline name.
        /// </summary>
        /// <param name="name">The pipeline name (used for logging and identification).</param>
        public ResiliencePipelineBuilder(string name)
        {
            _name = name ?? throw new ArgumentNullException(nameof(name));
        }

        /// <summary>
        /// Adds retry strategy with the specified number of retries.
        /// </summary>
        /// <param name="retries">Number of retry attempts (default: 3).</param>
        /// <returns>This builder for chaining.</returns>
        public ResiliencePipelineBuilder AddRetry(int retries = 3)
        {
            return AddRetry(Bounce.Times(retries));
        }

        /// <summary>
        /// Adds retry strategy with custom Bounce configuration.
        /// </summary>
        /// <param name="config">The Bounce configuration.</param>
        /// <returns>This builder for chaining.</returns>
        public ResiliencePipelineBuilder AddRetry(Bounce config)
        {
            _strategies.Add(new RetryStrategy(config));
            return this;
        }

        /// <summary>
        /// Adds circuit breaker strategy.
        /// </summary>
        /// <param name="serviceKey">Unique key for the service/resource being protected.</param>
        /// <param name="failureThreshold">Number of failures before opening the circuit.</param>
        /// <param name="samplingWindow">Size of the sampling window.</param>
        /// <returns>This builder for chaining.</returns>
        public ResiliencePipelineBuilder AddCircuitBreaker(string serviceKey, int failureThreshold, int samplingWindow)
        {
            var config = Cushion.ForService(serviceKey)
                .OpenAfter(failureThreshold, samplingWindow)
                .HalfOpenAfter(TimeSpan.FromSeconds(30));
            return AddCircuitBreaker(serviceKey, config);
        }

        /// <summary>
        /// Adds circuit breaker strategy with custom Cushion configuration.
        /// </summary>
        /// <param name="serviceKey">Unique key for the service/resource being protected.</param>
        /// <param name="config">The Cushion configuration.</param>
        /// <returns>This builder for chaining.</returns>
        public ResiliencePipelineBuilder AddCircuitBreaker(string serviceKey, Cushion config)
        {
            _strategies.Add(new CircuitBreakerStrategy(serviceKey, config));
            return this;
        }

        /// <summary>
        /// Adds timeout strategy.
        /// </summary>
        /// <param name="timeout">The timeout duration.</param>
        /// <returns>This builder for chaining.</returns>
        public ResiliencePipelineBuilder AddTimeout(TimeSpan timeout)
        {
            _strategies.Add(new TimeoutStrategy(timeout));
            return this;
        }

        /// <summary>
        /// Adds fallback strategy for the specified result type.
        /// </summary>
        /// <typeparam name="TResult">The result type to provide fallback for.</typeparam>
        /// <param name="fallback">Function that provides the fallback value given the exception.</param>
        /// <returns>This builder for chaining.</returns>
        public ResiliencePipelineBuilder AddFallback<TResult>(Func<Exception, TResult> fallback)
        {
            _strategies.Add(new FallbackStrategy<TResult>(fallback));
            return this;
        }

        /// <summary>
        /// Adds fallback strategy with a constant value.
        /// </summary>
        /// <typeparam name="TResult">The result type to provide fallback for.</typeparam>
        /// <param name="fallbackValue">The fallback value to return on failure.</param>
        /// <returns>This builder for chaining.</returns>
        public ResiliencePipelineBuilder AddFallback<TResult>(TResult fallbackValue)
        {
            return AddFallback<TResult>(_ => fallbackValue);
        }

        /// <summary>
        /// Adds bulkhead (concurrency limiting) strategy.
        /// </summary>
        /// <param name="resourceKey">Unique key for the resource being protected.</param>
        /// <param name="maxConcurrency">Maximum concurrent executions.</param>
        /// <param name="queueDepth">Maximum queue depth for waiting requests (default: 0).</param>
        /// <returns>This builder for chaining.</returns>
        public ResiliencePipelineBuilder AddBulkhead(string resourceKey, int maxConcurrency, int queueDepth = 0)
        {
            var config = Compartment.ForResource(resourceKey)
                .WithMaxConcurrency(maxConcurrency)
                .WithQueueDepth(queueDepth)
                .Build();
            return AddBulkhead(resourceKey, config);
        }

        /// <summary>
        /// Adds bulkhead strategy with custom Compartment configuration.
        /// </summary>
        /// <param name="resourceKey">Unique key for the resource being protected.</param>
        /// <param name="config">The Compartment configuration.</param>
        /// <returns>This builder for chaining.</returns>
        public ResiliencePipelineBuilder AddBulkhead(string resourceKey, Compartment config)
        {
            _strategies.Add(new BulkheadStrategy(resourceKey, config));
            return this;
        }

        /// <summary>
        /// Adds rate limiting strategy.
        /// </summary>
        /// <param name="serviceKey">Unique key for the service being rate limited.</param>
        /// <param name="maxRequests">Maximum requests allowed in the time window.</param>
        /// <param name="timeWindow">The time window for rate limiting.</param>
        /// <returns>This builder for chaining.</returns>
        public ResiliencePipelineBuilder AddRateLimit(string serviceKey, int maxRequests, TimeSpan timeWindow)
        {
            var config = Throttle.ForService(serviceKey)
                .WithRate(maxRequests, timeWindow)
                .Build();
            return AddRateLimit(serviceKey, config);
        }

        /// <summary>
        /// Adds rate limiting strategy with custom Throttle configuration.
        /// </summary>
        /// <param name="serviceKey">Unique key for the service being rate limited.</param>
        /// <param name="config">The Throttle configuration.</param>
        /// <returns>This builder for chaining.</returns>
        public ResiliencePipelineBuilder AddRateLimit(string serviceKey, Throttle config)
        {
            _strategies.Add(new RateLimitStrategy(serviceKey, config));
            return this;
        }

        /// <summary>
        /// Builds the resilience pipeline.
        /// </summary>
        /// <returns>The configured ResiliencePipeline.</returns>
        public ResiliencePipeline Build()
        {
            return new ResiliencePipeline(_name, _strategies.ToArray());
        }
    }
}
