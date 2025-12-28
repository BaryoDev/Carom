using System;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Carom.Telemetry.OpenTelemetry
{
    /// <summary>
    /// OpenTelemetry instrumentation for Carom resilience patterns.
    /// </summary>
    public static class CaromTelemetry
    {
        /// <summary>
        /// The activity source name for Carom operations.
        /// </summary>
        public const string ActivitySourceName = "Carom";

        /// <summary>
        /// The meter name for Carom metrics.
        /// </summary>
        public const string MeterName = "Carom";

        private static readonly ActivitySource ActivitySource = new(ActivitySourceName, "1.0.0");
        private static readonly Meter Meter = new(MeterName, "1.0.0");

        // Counters
        private static readonly Counter<long> RetryCounter = Meter.CreateCounter<long>(
            "carom.retry.count",
            description: "Number of retry attempts");

        private static readonly Counter<long> CircuitBreakerOpenCounter = Meter.CreateCounter<long>(
            "carom.circuit_breaker.open.count",
            description: "Number of times circuit breaker opened");

        private static readonly Counter<long> BulkheadRejectionCounter = Meter.CreateCounter<long>(
            "carom.bulkhead.rejection.count",
            description: "Number of bulkhead rejections");

        private static readonly Counter<long> RateLimitRejectionCounter = Meter.CreateCounter<long>(
            "carom.rate_limit.rejection.count",
            description: "Number of rate limit rejections");

        // Histograms
        private static readonly Histogram<double> RetryDelayHistogram = Meter.CreateHistogram<double>(
            "carom.retry.delay",
            unit: "ms",
            description: "Retry delay duration in milliseconds");

        /// <summary>
        /// Creates an activity for a Carom operation.
        /// </summary>
        /// <param name="operationName">The operation name.</param>
        /// <param name="kind">The activity kind.</param>
        /// <returns>The created activity, or null if not enabled.</returns>
        public static Activity? StartActivity(string operationName, ActivityKind kind = ActivityKind.Internal)
        {
            return ActivitySource.StartActivity(operationName, kind);
        }

        /// <summary>
        /// Records a retry attempt.
        /// </summary>
        /// <param name="attempt">The attempt number.</param>
        /// <param name="delayMs">The delay in milliseconds.</param>
        /// <param name="exceptionType">The exception type that triggered the retry.</param>
        public static void RecordRetry(int attempt, double delayMs, string? exceptionType = null)
        {
            var tags = new TagList { { "attempt", attempt }, { "exception_type", exceptionType ?? "unknown" } };
            RetryCounter.Add(1, tags);
            RetryDelayHistogram.Record(delayMs);
        }

        /// <summary>
        /// Records a circuit breaker opening.
        /// </summary>
        /// <param name="serviceName">The service name.</param>
        public static void RecordCircuitBreakerOpen(string serviceName)
        {
            var tags = new TagList { { "service", serviceName } };
            CircuitBreakerOpenCounter.Add(1, tags);
        }

        /// <summary>
        /// Records a bulkhead rejection.
        /// </summary>
        /// <param name="resourceKey">The resource key.</param>
        public static void RecordBulkheadRejection(string resourceKey)
        {
            var tags = new TagList { { "resource", resourceKey } };
            BulkheadRejectionCounter.Add(1, tags);
        }

        /// <summary>
        /// Records a rate limit rejection.
        /// </summary>
        /// <param name="serviceKey">The service key.</param>
        public static void RecordRateLimitRejection(string serviceKey)
        {
            var tags = new TagList { { "service", serviceKey } };
            RateLimitRejectionCounter.Add(1, tags);
        }
    }
}
