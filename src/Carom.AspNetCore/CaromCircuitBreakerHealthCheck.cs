using Microsoft.Extensions.Diagnostics.HealthChecks;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Carom.AspNetCore
{
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
