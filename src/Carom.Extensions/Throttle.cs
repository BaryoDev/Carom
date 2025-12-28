using System;
using System.Threading;
using System.Threading.Tasks;

namespace Carom.Extensions
{
    /// <summary>
    /// Rate limiter configuration using token bucket algorithm.
    /// The "throttle" controls the flow rate to prevent overwhelming services.
    /// </summary>
    public readonly struct Throttle
    {
        /// <summary>
        /// The unique identifier for this service (e.g., "api-client", "database").
        /// </summary>
        public string ServiceKey { get; }

        /// <summary>
        /// Maximum number of requests allowed per time window.
        /// </summary>
        public int MaxRequests { get; }

        /// <summary>
        /// Time window for rate limiting.
        /// </summary>
        public TimeSpan TimeWindow { get; }

        /// <summary>
        /// Maximum burst size (tokens that can accumulate).
        /// </summary>
        public int BurstSize { get; }

        internal Throttle(string serviceKey, int maxRequests, TimeSpan timeWindow, int burstSize)
        {
            if (string.IsNullOrWhiteSpace(serviceKey))
                throw new ArgumentException("Service key cannot be null or empty", nameof(serviceKey));
            if (maxRequests < 1)
                throw new ArgumentException("Max requests must be at least 1", nameof(maxRequests));
            if (timeWindow <= TimeSpan.Zero)
                throw new ArgumentException("Time window must be positive", nameof(timeWindow));
            if (burstSize < maxRequests)
                throw new ArgumentException("Burst size must be >= max requests", nameof(burstSize));

            ServiceKey = serviceKey;
            MaxRequests = maxRequests;
            TimeWindow = timeWindow;
            BurstSize = burstSize;
        }

        /// <summary>
        /// Creates a throttle builder for the specified service.
        /// </summary>
        public static ThrottleBuilder ForService(string serviceKey) =>
            new ThrottleBuilder(serviceKey);

        /// <summary>
        /// Executes a synchronous action with rate limiting.
        /// </summary>
        internal T Execute<T>(Func<T> action)
        {
            var state = ThrottleStore.GetOrCreate(ServiceKey, this);

            if (!state.TryAcquire())
            {
                throw new ThrottledException(ServiceKey, MaxRequests, TimeWindow);
            }

            return action();
        }

        /// <summary>
        /// Executes an asynchronous action with rate limiting.
        /// </summary>
        internal async Task<T> ExecuteAsync<T>(Func<Task<T>> action)
        {
            var state = ThrottleStore.GetOrCreate(ServiceKey, this);

            if (!state.TryAcquire())
            {
                throw new ThrottledException(ServiceKey, MaxRequests, TimeWindow);
            }

            return await action().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Fluent builder for Throttle configuration.
    /// </summary>
    public class ThrottleBuilder
    {
        private readonly string _serviceKey;
        private int _maxRequests = 100;
        private TimeSpan _timeWindow = TimeSpan.FromSeconds(1);
        private int _burstSize = 100;

        internal ThrottleBuilder(string serviceKey)
        {
            _serviceKey = serviceKey;
        }

        /// <summary>
        /// Sets the rate limit (requests per time window).
        /// </summary>
        public ThrottleBuilder WithRate(int maxRequests, TimeSpan per)
        {
            _maxRequests = maxRequests;
            _timeWindow = per;
            _burstSize = Math.Max(_burstSize, maxRequests);
            return this;
        }

        /// <summary>
        /// Sets the maximum burst size (tokens that can accumulate).
        /// </summary>
        public ThrottleBuilder WithBurst(int burstSize)
        {
            _burstSize = burstSize;
            return this;
        }

        /// <summary>
        /// Builds the Throttle configuration.
        /// </summary>
        public Throttle Build() =>
            new Throttle(_serviceKey, _maxRequests, _timeWindow, _burstSize);
    }
}
