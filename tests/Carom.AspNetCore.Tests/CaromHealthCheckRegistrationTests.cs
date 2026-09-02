using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Xunit;
using Carom.AspNetCore;
using Carom.Extensions;

namespace Carom.AspNetCore.Tests;

/// <summary>
/// The delegate-based health check and the AddCaromCircuitBreaker registration helper.
/// </summary>
public class CaromHealthCheckRegistrationTests
{
    private static HealthCheckContext ContextFor(IHealthCheck check) =>
        new()
        {
            Registration = new HealthCheckRegistration("carom_test", check, failureStatus: null, tags: null),
        };

    [Fact]
    public void CaromHealthCheckRejectsNullArguments()
    {
        Assert.Throws<ArgumentNullException>(() => new CaromHealthCheck(null!, () => Task.FromResult(true)));
        Assert.Throws<ArgumentNullException>(() => new CaromHealthCheck("db", null!));
    }

    [Fact]
    public async Task CaromHealthCheckReportsHealthyWhenTheProbeSaysSo()
    {
        var check = new CaromHealthCheck("db", () => Task.FromResult(true));

        var result = await check.CheckHealthAsync(ContextFor(check));

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Contains("'db' is healthy", result.Description);
    }

    [Fact]
    public async Task CaromHealthCheckReportsUnhealthyWhenTheProbeSaysNo()
    {
        var check = new CaromHealthCheck("db", () => Task.FromResult(false));

        var result = await check.CheckHealthAsync(ContextFor(check));

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("'db' is unhealthy", result.Description);
    }

    [Fact]
    public async Task CaromHealthCheckTurnsAProbeExceptionIntoUnhealthyInsteadOfThrowing()
    {
        var boom = new InvalidOperationException("probe blew up");
        var check = new CaromHealthCheck("db", () => Task.FromException<bool>(boom));

        var result = await check.CheckHealthAsync(ContextFor(check));

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Same(boom, result.Exception);
    }

    [Fact]
    public void AddCaromCircuitBreakerRegistersWithTheDocumentedDefaults()
    {
        var services = new ServiceCollection();
        services.AddHealthChecks().AddCaromCircuitBreaker("payments");
        using var provider = services.BuildServiceProvider();

        var registration = Assert.Single(
            provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations);

        Assert.Equal("carom_payments", registration.Name);
        Assert.Equal(HealthStatus.Unhealthy, registration.FailureStatus);
        Assert.Empty(registration.Tags);
        Assert.IsType<CaromCircuitBreakerHealthCheck>(registration.Factory(provider));
    }

    [Fact]
    public void AddCaromCircuitBreakerHonoursNameStatusAndTags()
    {
        var services = new ServiceCollection();
        services.AddHealthChecks().AddCaromCircuitBreaker(
            "payments", name: "cb", failureStatus: HealthStatus.Degraded, tags: new[] { "ready" });
        using var provider = services.BuildServiceProvider();

        var registration = Assert.Single(
            provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations);

        Assert.Equal("cb", registration.Name);
        Assert.Equal(HealthStatus.Degraded, registration.FailureStatus);
        Assert.Contains("ready", registration.Tags);
    }

    [Fact]
    public async Task ARegisteredCheckReportsAnOpenCircuitThroughTheHealthCheckService()
    {
        var key = $"hc-service-{Guid.NewGuid():N}";
        var cushion = Cushion.ForService(key).OpenAfter(1, 1).HalfOpenAfter(TimeSpan.FromMinutes(1));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CaromCushionExtensions.ShotAsync<int>(
                () => throw new InvalidOperationException("dependency down"),
                cushion,
                retries: 0));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHealthChecks().AddCaromCircuitBreaker(key);
        using var provider = services.BuildServiceProvider();

        var report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync();

        var entry = report.Entries[$"carom_{key}"];
        Assert.Equal(HealthStatus.Unhealthy, entry.Status);
        Assert.Equal(HealthStatus.Unhealthy, report.Status);
    }
}
