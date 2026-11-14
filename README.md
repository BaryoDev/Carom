# Carom 🎱

**A lean, fast, and safe resilience library for .NET**

[![NuGet](https://img.shields.io/nuget/v/Carom.svg)](https://www.nuget.org/packages/Carom/)
[![License](https://img.shields.io/badge/license-MPL--2.0-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-netstandard2.0%20%7C%20net8.0%20%7C%20net10.0-512BD4)](https://dotnet.microsoft.com/)

Carom is a zero-dependency resilience library that enforces best practices by default. Named after the billiards shot where the ball bounces before reaching its target, Carom helps your code gracefully handle failures.

Runs on .NET 10 and .NET 8. The core packages target `netstandard2.0`, so .NET Framework and older .NET are supported too; the ASP.NET Core, EF Core and OpenTelemetry packages target `net8.0;net10.0`. Tests run on both runtimes in CI.

## 🎯 Why Carom?

- **Zero Dependencies** (core packages)
- **Zero Allocations** (0 bytes on the successful hot path, test-enforced)
- **Safe by Default** (mandatory decorrelated jitter)
- **Tiny Footprint** (27.5KB core, 57.5KB extensions)
- **Fully Composable** (all patterns work together)

## 📦 Packages

Sizes are the Release-built assemblies. Tests fail if the core grows past 28KB or the extensions past 60KB.

| Package | Size | Purpose |
|---------|------|---------|
| **Carom** | 27.5KB | Core retry + timeout |
| **Carom.Extensions** | 57.5KB | Circuit Breaker, Fallback, Bulkhead, Rate Limiting |
| **Carom.Http** | 12.5KB | HTTP integration |
| **Carom.DependencyInjection** | 21.5KB | Named resilience pipelines for `IServiceCollection` |
| **Carom.AspNetCore** | 8.5KB | ASP.NET Core health checks |
| **Carom.EntityFramework** | 9.5KB | EF Core retry |
| **Carom.Telemetry.OpenTelemetry** | 9.5KB | OpenTelemetry metrics for retries, circuit opens and rejections, after one `CaromTelemetry.Subscribe()` call |

## 🚀 Quick Start

### Installation

```bash
dotnet add package Carom
dotnet add package Carom.Extensions
```

### Basic Usage

<!-- sample: basic-usage -->
```csharp
using Carom;
using Retry = Carom.Carom;

// Simple retry with exponential backoff
var result = await Retry.ShotAsync(() => api.CallAsync(), retries: 3);

// With timeout
var bounce = Bounce.Times(5).WithTimeout(TimeSpan.FromSeconds(30));
var data = await Retry.ShotAsync(() => apiClient.FetchAsync(), bounce);
```

The class `Carom` shares its name with the namespace, so with `using Carom;` at the top of a
file a bare `Carom.ShotAsync` does not resolve. The alias above fixes that, and
`global::Carom.Carom.ShotAsync` works too. The class keeps its name.

### Per-try timeout and Retry-After

`WithTimeout` bounds the whole call. To bound each try as well, use `CaromAttemptTimeoutExtensions`
from `Carom.Extensions`: a try that runs out is cancelled and fails with a
`TimeoutRejectedException`, which is retried like any other failure. The overall timeout and
cancellation still end the call. It needs a `Func<CancellationToken, Task>` action.

<!-- sample: attempt-timeout -->
```csharp
using Carom.Extensions;

// Each try gets 2 seconds, the whole call gets 10
var bounce = Bounce.Times(3).WithTimeout(TimeSpan.FromSeconds(10));

var data = await CaromAttemptTimeoutExtensions.ShotAsync(
    ct => apiClient.FetchAsync(ct), bounce, attemptTimeout: TimeSpan.FromSeconds(2));
```

`WithDelayHint` lets the failure name the wait before the next retry, such as a server's
`Retry-After`. The hint replaces the computed jitter for that retry, is clamped to
`MaxDelay`, and is still cut short by the overall timeout and by cancellation. Return null
to keep the computed backoff.

<!-- sample: delay-hint -->
```csharp
// Wait the Retry-After the server sent, up to MaxDelay
var bounce = Bounce.Times(3)
    .WithMaxDelay(TimeSpan.FromSeconds(60))
    .WithDelayHint(ex => ex is RateLimitedException limited ? limited.RetryAfter : null);

var data = await Retry.ShotAsync(() => apiClient.FetchAsync(), bounce);
```

To watch retries, subscribe to the `CaromHooks.Retrying` event. Add and remove are atomic and
one subscriber cannot remove another. The old settable `CaromHooks.OnRetry` property is
obsolete and will go in the next major version.

<!-- sample: retry-event -->
```csharp
CaromHooks.Retrying += signal =>
    Console.WriteLine($"retry {signal.Attempt} in {signal.Delay}");
```

These samples are compiled by `tests/Carom.ReadmeSamples.Tests`, which also fails when the
README text drifts from the compiled copy.

### Circuit Breaker

```csharp
using Carom.Extensions;

var cushion = Cushion.ForService("payment-api")
    .OpenAfter(failures: 5, trackingLast: 10)
    .WithinLast(TimeSpan.FromMinutes(1))
    .When(ex => ex is HttpRequestException)
    .HalfOpenAfter(TimeSpan.FromSeconds(30));

var payment = await CaromCushionExtensions.ShotAsync(
    () => paymentApi.Charge(), 
    cushion);
```

The circuit opens as soon as 5 failures are recorded. `trackingLast` bounds how far back
failures are counted, and `WithinLast` expires them by age, so an old incident cannot
combine with a fresh failure to trip the breaker. `When` decides which exceptions count
as the dependency's fault: without it, a bug in your own calling code would open the
circuit on a healthy service. The default counts everything except cancellation, but a
`Bounce.WithTimeout` timeout (`TimeoutRejectedException`) counts as a failure, so a hanging
dependency opens the breaker. Retries run inside the breaker, so one logical call
records one outcome no matter how many attempts it took.

### Fallback

```csharp
var config = await new Func<Task<AppConfig>>(() => configService.LoadAsync())
    .PocketAsync(AppConfig.Default);
```

### Bulkhead

```csharp
var dbCompartment = Compartment.ForResource("database")
    .WithMaxConcurrency(10)
    .Build();

var query = await CaromCompartmentExtensions.ShotAsync(
    () => db.QueryAsync(sql), 
    dbCompartment);
```

### Rate Limiting

```csharp
var apiThrottle = Throttle.ForService("external-api")
    .WithRate(100, TimeSpan.FromSeconds(1))
    .WithBurst(20)
    .Build();

var apiResult = await CaromThrottleExtensions.ShotAsync(
    () => apiClient.CallAsync(), 
    apiThrottle);
```

## 🎓 Patterns

| Pattern | Class | Purpose |
|---------|-------|---------|
| **Retry** | `Carom` | Exponential backoff with jitter |
| **Timeout** | `Bounce.WithTimeout()` | Operation timeout |
| **Circuit Breaker** | `Cushion` | Prevent cascade failures |
| **Fallback** | `Pocket/PocketAsync` | Graceful degradation |
| **Bulkhead** | `Compartment` | Concurrency control |
| **Rate Limiting** | `Throttle` | Token bucket algorithm |

## Documentation

- [Security Policy](docs/SECURITY.md)
- [Changelog](CHANGELOG.md)

## 🤝 Contributing

Contributions welcome! Please read [CONTRIBUTING.md](CONTRIBUTING.md) first.

## 📄 License

MPL-2.0 - see [LICENSE](LICENSE) for details.

## 🙏 Acknowledgments

Built with the [Baryo.Dev](https://github.com/BaryoDev) philosophy: zero dependencies, minimal allocations, safe by default.

---

**Made with ❤️ by Baryo.Dev**

## 📊 Performance

Speed is not the pitch. Per successful call, both Carom and Polly cost nanoseconds, invisible next to the network or database call being wrapped. What Carom offers is small, allocation-free, dependency-free resilience for hosts where the standard stack is too much: .NET Framework and netstandard2.0 services, size-constrained deployments, and libraries that should not impose a dependency graph.

The claims we do make are measured (Apple M1, .NET 8, against Polly 8.4.2) and enforced by `tests/Carom.Tests/PublishedClaimsTests.cs`:

- **Zero allocations on the successful hot path**: Carom 0 B per call. Polly v8 allocates 24 B per call, the Polly v7 API 248 B.
- **Small on disk**: Carom.dll is 27.5 KB and Carom.Extensions.dll 57.5 KB. Polly.Core.dll (net8.0) is 237 KB.
- **Zero package dependencies on every target**: Polly.Core has none on net8.0 but needs four packages on netstandard2.0 and five on .NET Framework.

Details and methodology in [docs/BENCHMARKS.md](docs/BENCHMARKS.md).
