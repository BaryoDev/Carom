using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using System;
using System.Threading.Tasks;

namespace Carom.AspNetCore
{
    /// <summary>
    /// Extension methods for integrating Carom with ASP.NET Core.
    /// </summary>
    public static class CaromServiceCollectionExtensions
    {
        /// <summary>
        /// Adds a health check for a Carom circuit breaker.
        /// </summary>
        /// <param name="builder">The health checks builder.</param>
        /// <param name="serviceName">The name of the service/circuit to monitor.</param>
        /// <param name="name">Optional health check name (defaults to "carom_{serviceName}").</param>
        /// <param name="failureStatus">The health status to report on failure (defaults to Unhealthy).</param>
        /// <param name="tags">Optional tags for the health check.</param>
        /// <returns>The health checks builder for chaining.</returns>
        public static IHealthChecksBuilder AddCaromCircuitBreaker(
            this IHealthChecksBuilder builder,
            string serviceName,
            string? name = null,
            HealthStatus? failureStatus = null,
            string[]? tags = null)
        {
            name ??= $"carom_{serviceName}";
            
            return builder.AddCheck(
                name,
                new CaromHealthCheck(serviceName, () => Task.FromResult(true)),
                failureStatus,
                tags ?? Array.Empty<string>());
        }
    }
}
