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
- **Tiny Footprint** (21.5KB core, 53.5KB extensions)
- **Fully Composable** (all patterns work together)

## 📦 Packages

Sizes are the Release-built assemblies. Tests keep the core and extensions figures honest.

| Package | Size | Purpose |
|---------|------|---------|
| **Carom** | 21.5KB | Core retry + timeout |
| **Carom.Extensions** | 53.5KB | Circuit Breaker, Fallback, Bulkhead, Rate Limiting |
| **Carom.Http** | 13KB | HTTP integration |
| **Carom.AspNetCore** | 9KB | ASP.NET Core health checks |
| **Carom.EntityFramework** | 10KB | EF Core retry |
| **Carom.Telemetry.OpenTelemetry** | 7KB | OpenTelemetry instruments, called by your code (Carom does not emit automatically yet) |

## 🚀 Quick Start

### Installation

```bash
dotnet add package Carom
dotnet add package Carom.Extensions
```

### Basic Usage

```csharp
using Carom;

// Simple retry with exponential backoff
var result = await Carom.ShotAsync(() => api.CallAsync(), retries: 3);

// With timeout
var bounce = Bounce.Times(5).WithTimeout(TimeSpan.FromSeconds(30));
var data = await Carom.ShotAsync(() => apiClient.FetchAsync(), bounce);
```

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
circuit on a healthy service. Retries run inside the breaker, so one logical call
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

## �� Documentation

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
- **Small on disk**: Carom.dll is 21.5 KB and Carom.Extensions.dll 53.5 KB. Polly.Core.dll (net8.0) is 237 KB.
- **Zero package dependencies on every target**: Polly.Core has none on net8.0 but needs four packages on netstandard2.0 and five on .NET Framework.

Details and methodology in [docs/BENCHMARKS.md](docs/BENCHMARKS.md).
