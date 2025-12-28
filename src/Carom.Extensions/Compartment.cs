using System;
using System.Threading;
using System.Threading.Tasks;

namespace Carom.Extensions
{
    /// <summary>
    /// Bulkhead configuration for isolating resources and preventing cascading failures.
    /// The "compartment" contains failures to protect the overall system.
    /// </summary>
    public readonly struct Compartment
    {
        /// <summary>
        /// The unique identifier for this resource (e.g., "database", "api-client").
        /// </summary>
        public string ResourceKey { get; }

        /// <summary>
        /// Maximum number of concurrent executions allowed.
        /// </summary>
        public int MaxConcurrency { get; }

        /// <summary>
        /// Maximum number of queued requests waiting for execution.
        /// </summary>
        public int QueueDepth { get; }

        internal Compartment(string resourceKey, int maxConcurrency, int queueDepth)
        {
            if (string.IsNullOrWhiteSpace(resourceKey))
                throw new ArgumentException("Resource key cannot be null or empty", nameof(resourceKey));
            if (maxConcurrency < 1)
                throw new ArgumentException("Max concurrency must be at least 1", nameof(maxConcurrency));
            if (queueDepth < 0)
                throw new ArgumentException("Queue depth cannot be negative", nameof(queueDepth));

            ResourceKey = resourceKey;
            MaxConcurrency = maxConcurrency;
            QueueDepth = queueDepth;
        }

        /// <summary>
        /// Creates a compartment builder for the specified resource.
        /// </summary>
        public static CompartmentBuilder ForResource(string resourceKey) =>
            new CompartmentBuilder(resourceKey);

        /// <summary>
        /// Executes a synchronous action with bulkhead protection.
        /// </summary>
        internal T Execute<T>(Func<T> action)
        {
            var state = CompartmentStore.GetOrCreate(ResourceKey, this);

            if (!state.TryEnter())
            {
                throw new CompartmentFullException(ResourceKey, MaxConcurrency);
            }

            try
            {
                return action();
            }
            finally
            {
                state.Release();
            }
        }

        /// <summary>
        /// Executes an asynchronous action with bulkhead protection.
        /// </summary>
        internal async Task<T> ExecuteAsync<T>(Func<Task<T>> action, CancellationToken ct = default)
        {
            var state = CompartmentStore.GetOrCreate(ResourceKey, this);

            if (!await state.TryEnterAsync(ct).ConfigureAwait(false))
            {
                throw new CompartmentFullException(ResourceKey, MaxConcurrency);
            }

            try
            {
                return await action().ConfigureAwait(false);
            }
            finally
            {
                state.Release();
            }
        }
    }

    /// <summary>
    /// Fluent builder for Compartment configuration.
    /// </summary>
    public class CompartmentBuilder
    {
        private readonly string _resourceKey;
        private int _maxConcurrency = 10;
        private int _queueDepth = 0;

        internal CompartmentBuilder(string resourceKey)
        {
            _resourceKey = resourceKey;
        }

        /// <summary>
        /// Sets the maximum number of concurrent executions.
        /// </summary>
        public CompartmentBuilder WithMaxConcurrency(int max)
        {
            _maxConcurrency = max;
            return this;
        }

        /// <summary>
        /// Sets the maximum queue depth for waiting requests.
        /// </summary>
        public CompartmentBuilder WithQueueDepth(int depth)
        {
            _queueDepth = depth;
            return this;
        }

        /// <summary>
        /// Builds the Compartment configuration.
        /// </summary>
        public Compartment Build() =>
            new Compartment(_resourceKey, _maxConcurrency, _queueDepth);
    }
}
