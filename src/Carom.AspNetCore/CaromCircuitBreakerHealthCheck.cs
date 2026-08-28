// Copyright (c) BaryoDev. All rights reserved.
// Licensed under the MPL-2.0 license. See LICENSE file in the project root for full license information.

using Carom.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Carom.AspNetCore
{
    /// <summary>
    /// Health check that reports the actual state of a Carom circuit breaker:
    /// Open maps to the registration's failure status, HalfOpen to Degraded,
    /// and Closed (or no circuit created yet) to Healthy.
    /// </summary>
    public sealed class CaromCircuitBreakerHealthCheck : IHealthCheck
    {
        private readonly string _serviceName;

        public CaromCircuitBreakerHealthCheck(string serviceName)
        {
            _serviceName = serviceName ?? throw new ArgumentNullException(nameof(serviceName));
        }

        public Task<HealthCheckResult> CheckHealthAsync(
            HealthCheckContext context,
            CancellationToken cancellationToken = default)
        {
            var state = Cushion.GetState(_serviceName);

            var result = state switch
            {
                CircuitState.Open => new HealthCheckResult(
                    context.Registration.FailureStatus,
                    $"Circuit '{_serviceName}' is open (rejecting requests)"),
                CircuitState.HalfOpen => HealthCheckResult.Degraded(
                    $"Circuit '{_serviceName}' is half-open (testing recovery)"),
                CircuitState.Closed => HealthCheckResult.Healthy(
                    $"Circuit '{_serviceName}' is closed"),
                _ => HealthCheckResult.Healthy(
                    $"Circuit '{_serviceName}' has not been used yet"),
            };

            return Task.FromResult(result);
        }
    }

    /// <summary>
    /// Health check for Carom resilience patterns.
    /// Provides basic health reporting for monitoring.
    /// </summary>
    public class CaromHealthCheck : IHealthCheck
    {
        private readonly string _name;
        private readonly Func<Task<bool>> _healthCheckFunc;

        public CaromHealthCheck(string name, Func<Task<bool>> healthCheckFunc)
        {
            _name = name ?? throw new ArgumentNullException(nameof(name));
            _healthCheckFunc = healthCheckFunc ?? throw new ArgumentNullException(nameof(healthCheckFunc));
        }

        public async Task<HealthCheckResult> CheckHealthAsync(
            HealthCheckContext context,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var isHealthy = await _healthCheckFunc().ConfigureAwait(false);
                
                return isHealthy
                    ? HealthCheckResult.Healthy($"Carom '{_name}' is healthy")
                    : HealthCheckResult.Unhealthy($"Carom '{_name}' is unhealthy");
            }
            catch (Exception ex)
            {
                return HealthCheckResult.Unhealthy($"Error checking Carom '{_name}'", ex);
            }
        }
    }
}
