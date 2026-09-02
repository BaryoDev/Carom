using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;
using Carom.AspNetCore;
using Carom.Extensions;

namespace Carom.AspNetCore.Tests;

/// <summary>
/// The health check promises to mirror the real circuit state: Closed and never-used are
/// Healthy, Open is the registration's failure status, HalfOpen is Degraded. Circuits are
/// driven through the public Cushion API with unique service keys, and the half-open state
/// is observed by holding the probe on a TaskCompletionSource, never by sleeping.
/// </summary>
public class CaromCircuitBreakerHealthCheckTests
{
    private static string UniqueKey(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private static HealthCheckContext ContextFor(IHealthCheck check, HealthStatus? failureStatus = null) =>
        new()
        {
            Registration = new HealthCheckRegistration("carom_test", check, failureStatus, tags: null),
        };

    private static async Task OpenCircuitAsync(Cushion cushion)
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CaromCushionExtensions.ShotAsync<int>(
                () => throw new InvalidOperationException("dependency down"),
                cushion,
                retries: 0));
    }

    [Fact]
    public void NullServiceNameIsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => new CaromCircuitBreakerHealthCheck(null!));
    }

    [Fact]
    public async Task UnknownServiceKeyReportsHealthyNotUsedYet()
    {
        var key = UniqueKey("hc-unknown");
        var check = new CaromCircuitBreakerHealthCheck(key);

        var result = await check.CheckHealthAsync(ContextFor(check));

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Contains("has not been used yet", result.Description);
        Assert.Empty(result.Data);
    }

    [Fact]
    public async Task ClosedCircuitReportsHealthy()
    {
        var key = UniqueKey("hc-closed");
        var cushion = Cushion.ForService(key).OpenAfter(2, 2).HalfOpenAfter(TimeSpan.FromMinutes(1));
        await CaromCushionExtensions.ShotAsync(() => Task.FromResult(1), cushion, retries: 0);
        var check = new CaromCircuitBreakerHealthCheck(key);

        var result = await check.CheckHealthAsync(ContextFor(check));

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Contains($"Circuit '{key}' is closed", result.Description);
    }

    [Fact]
    public async Task OpenCircuitReportsTheRegistrationDefaultUnhealthy()
    {
        var key = UniqueKey("hc-open");
        var cushion = Cushion.ForService(key).OpenAfter(1, 1).HalfOpenAfter(TimeSpan.FromMinutes(1));
        await OpenCircuitAsync(cushion);
        var check = new CaromCircuitBreakerHealthCheck(key);

        // A registration built without a failure status defaults to Unhealthy.
        var result = await check.CheckHealthAsync(ContextFor(check));

        Assert.Equal(CircuitState.Open, Cushion.GetState(key));
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("open (rejecting requests)", result.Description);
    }

    [Fact]
    public async Task OpenCircuitHonoursACustomFailureStatus()
    {
        var key = UniqueKey("hc-open-degraded");
        var cushion = Cushion.ForService(key).OpenAfter(1, 1).HalfOpenAfter(TimeSpan.FromMinutes(1));
        await OpenCircuitAsync(cushion);
        var check = new CaromCircuitBreakerHealthCheck(key);

        var result = await check.CheckHealthAsync(ContextFor(check, HealthStatus.Degraded));

        Assert.Equal(HealthStatus.Degraded, result.Status);
    }

    [Fact]
    public async Task HalfOpenProbeReportsDegradedThenHealthyWhenItCloses()
    {
        var key = UniqueKey("hc-halfopen");
        // One tick of half-open delay has always elapsed by the next call, so the probe
        // is admitted immediately and no wall clock is ever asserted.
        var cushion = Cushion.ForService(key).OpenAfter(1, 1).HalfOpenAfter(TimeSpan.FromTicks(1));
        await OpenCircuitAsync(cushion);
        var check = new CaromCircuitBreakerHealthCheck(key);

        var probeEntered = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProbe = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int>? probe = null;

        // If the probe loses the admission check it fails fast with CircuitOpenException;
        // elapsed time only grows, so retrying the call terminates without sleeping.
        // The attempt cap keeps a circuit that never admits from hanging the suite.
        const int maxAdmissionAttempts = 100;
        var admissionAttempts = 0;
        while (probe == null)
        {
            admissionAttempts++;
            Assert.True(admissionAttempts <= maxAdmissionAttempts,
                $"the circuit never admitted a half-open probe in {maxAdmissionAttempts} attempts; " +
                "every candidate failed fast with CircuitOpenException");
            var candidate = CaromCushionExtensions.ShotAsync(
                async () =>
                {
                    probeEntered.TrySetResult(null);
                    await releaseProbe.Task;
                    return 99;
                },
                cushion,
                retries: 0);
            var winner = await Task.WhenAny(probeEntered.Task, candidate);
            if (winner == candidate && !probeEntered.Task.IsCompleted)
            {
                // Not admitted yet: observe the fast failure and try again.
                await Assert.ThrowsAsync<CircuitOpenException>(() => candidate);
                continue;
            }
            probe = candidate;
        }

        await probeEntered.Task;
        Assert.Equal(CircuitState.HalfOpen, Cushion.GetState(key));

        var during = await check.CheckHealthAsync(ContextFor(check));
        Assert.Equal(HealthStatus.Degraded, during.Status);
        Assert.Contains("half-open (testing recovery)", during.Description);

        releaseProbe.SetResult(null);
        Assert.Equal(99, await probe);

        Assert.Equal(CircuitState.Closed, Cushion.GetState(key));
        var after = await check.CheckHealthAsync(ContextFor(check));
        Assert.Equal(HealthStatus.Healthy, after.Status);
    }
}
