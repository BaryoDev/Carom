// Copyright (c) BaryoDev. All rights reserved.
// Licensed under the MPL-2.0 license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;

namespace Carom.DependencyInjection
{
    /// <summary>
    /// Extension methods for registering Carom resilience pipelines with dependency injection.
    /// </summary>
    public static class CaromServiceCollectionExtensions
    {
        /// <summary>
        /// Adds a named resilience pipeline to the service collection.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="name">The unique name for the pipeline.</param>
        /// <param name="configure">Action to configure the pipeline builder.</param>
        /// <returns>The service collection for chaining.</returns>
        public static IServiceCollection AddCaromResilience(
            this IServiceCollection services,
            string name,
            Action<ResiliencePipelineBuilder> configure)
        {
            if (services == null) throw new ArgumentNullException(nameof(services));
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("Name cannot be null or empty", nameof(name));
            if (configure == null) throw new ArgumentNullException(nameof(configure));

            var builder = new ResiliencePipelineBuilder(name);
            configure(builder);
            var pipeline = builder.Build();

            // Register the pipeline as a singleton
            services.AddSingleton(pipeline);

            // Register the pipeline with the registry after it's built
            services.AddSingleton<IPipelineRegistration>(new PipelineRegistration(name, pipeline));

            return services;
        }

        /// <summary>
        /// Adds the resilience pipeline registry to the service collection.
        /// Call this once before adding individual pipelines if you need to resolve pipelines by name.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <returns>The service collection for chaining.</returns>
        public static IServiceCollection AddCaromResilienceRegistry(this IServiceCollection services)
        {
            if (services == null) throw new ArgumentNullException(nameof(services));

            services.AddSingleton<ResiliencePipelineRegistry>();
            services.AddSingleton<IResiliencePipelineRegistry>(sp =>
            {
                var registry = sp.GetRequiredService<ResiliencePipelineRegistry>();

                // Register all pipelines that were added
                foreach (var registration in sp.GetServices<IPipelineRegistration>())
                {
                    registry.Register(registration.Name, registration.Pipeline);
                }

                return registry;
            });

            return services;
        }

        /// <summary>
        /// Adds multiple named resilience pipelines to the service collection.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="configureAll">Action to configure multiple pipelines.</param>
        /// <returns>The service collection for chaining.</returns>
        public static IServiceCollection AddCaromResilience(
            this IServiceCollection services,
            Action<IResiliencePipelineConfigurator> configureAll)
        {
            if (services == null) throw new ArgumentNullException(nameof(services));
            if (configureAll == null) throw new ArgumentNullException(nameof(configureAll));

            var configurator = new ResiliencePipelineConfigurator(services);
            configureAll(configurator);

            return services;
        }
    }

    /// <summary>
    /// Interface for configuring multiple resilience pipelines.
    /// </summary>
    public interface IResiliencePipelineConfigurator
    {
        /// <summary>
        /// Adds a named resilience pipeline.
        /// </summary>
        /// <param name="name">The unique name for the pipeline.</param>
        /// <param name="configure">Action to configure the pipeline builder.</param>
        /// <returns>This configurator for chaining.</returns>
        IResiliencePipelineConfigurator AddPipeline(string name, Action<ResiliencePipelineBuilder> configure);
    }

    /// <summary>
    /// Interface for resolving resilience pipelines by name.
    /// </summary>
    public interface IResiliencePipelineRegistry
    {
        /// <summary>
        /// Gets a pipeline by name.
        /// </summary>
        /// <param name="name">The pipeline name.</param>
        /// <returns>The resilience pipeline.</returns>
        /// <exception cref="KeyNotFoundException">If no pipeline with the given name exists.</exception>
        ResiliencePipeline GetPipeline(string name);

        /// <summary>
        /// Tries to get a pipeline by name.
        /// </summary>
        /// <param name="name">The pipeline name.</param>
        /// <param name="pipeline">The resilience pipeline if found.</param>
        /// <returns>True if the pipeline was found.</returns>
        bool TryGetPipeline(string name, out ResiliencePipeline? pipeline);
    }

    /// <summary>
    /// Registry for storing and retrieving named resilience pipelines.
    /// </summary>
    public class ResiliencePipelineRegistry : IResiliencePipelineRegistry
    {
        private readonly Dictionary<string, ResiliencePipeline> _pipelines = new Dictionary<string, ResiliencePipeline>(StringComparer.OrdinalIgnoreCase);
        private readonly object _lock = new object();

        /// <summary>
        /// Registers a pipeline with the given name.
        /// </summary>
        /// <param name="name">The pipeline name.</param>
        /// <param name="pipeline">The pipeline to register.</param>
        public void Register(string name, ResiliencePipeline pipeline)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("Name cannot be null or empty", nameof(name));
            if (pipeline == null) throw new ArgumentNullException(nameof(pipeline));

            lock (_lock)
            {
                _pipelines[name] = pipeline;
            }
        }

        /// <inheritdoc />
        public ResiliencePipeline GetPipeline(string name)
        {
            if (TryGetPipeline(name, out var pipeline))
            {
                return pipeline!;
            }
            throw new KeyNotFoundException($"No resilience pipeline registered with name '{name}'");
        }

        /// <inheritdoc />
        public bool TryGetPipeline(string name, out ResiliencePipeline? pipeline)
        {
            lock (_lock)
            {
                return _pipelines.TryGetValue(name, out pipeline);
            }
        }
    }

    internal class ResiliencePipelineConfigurator : IResiliencePipelineConfigurator
    {
        private readonly IServiceCollection _services;

        public ResiliencePipelineConfigurator(IServiceCollection services)
        {
            _services = services;
        }

        public IResiliencePipelineConfigurator AddPipeline(string name, Action<ResiliencePipelineBuilder> configure)
        {
            _services.AddCaromResilience(name, configure);
            return this;
        }
    }

    /// <summary>
    /// Internal interface for pipeline registration.
    /// </summary>
    internal interface IPipelineRegistration
    {
        string Name { get; }
        ResiliencePipeline Pipeline { get; }
    }

    /// <summary>
    /// Internal class for storing pipeline registration info.
    /// </summary>
    internal class PipelineRegistration : IPipelineRegistration
    {
        public string Name { get; }
        public ResiliencePipeline Pipeline { get; }

        public PipelineRegistration(string name, ResiliencePipeline pipeline)
        {
            Name = name;
            Pipeline = pipeline;
        }
    }
}
