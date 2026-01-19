# Carom 🎱

**A lean, fast, and safe resilience library for .NET**

[![NuGet](https://img.shields.io/nuget/v/Carom.svg)](https://www.nuget.org/packages/Carom/)
[![License](https://img.shields.io/badge/license-MPL--2.0-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-8.0%2B-512BD4)](https://dotnet.microsoft.com/)

Carom is a zero-dependency resilience library that enforces best practices by default. Named after the billiards shot where the ball bounces before reaching its target, Carom helps your code gracefully handle failures.

## 🎯 Why Carom?

- **Zero Dependencies** (core packages)
- **Minimal Allocations** (<100 bytes on hot path)
- **Safe by Default** (mandatory decorrelated jitter)
- **Tiny Footprint** (13KB core, 20KB extensions)
- **Fully Composable** (all patterns work together)

## 📦 Packages

| Package | Version | Size | Purpose |
|---------|---------|------|---------|
| **Carom** | v1.3.0 | 13KB | Core retry + timeout |
| **Carom.Extensions** | v1.4.0 | 20KB | Circuit Breaker, Fallback, Bulkhead, Rate Limiting |
| **Carom.Http** | v1.0.0 | 11KB | HTTP integration |
| **Carom.AspNetCore** | v1.0.0 | 10KB | ASP.NET Core health checks |
| **Carom.EntityFramework** | v1.0.0 | 10KB | EF Core retry |
| **Carom.Telemetry.OpenTelemetry** | v1.0.0 | 9KB | OpenTelemetry metrics |

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
    .OpenAfter(failures: 5, within: 10)
    .HalfOpenAfter(TimeSpan.FromSeconds(30));

var payment = await CaromCushionExtensions.ShotAsync(
    () => paymentApi.Charge(), 
    cushion);
```

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
- [Assembly Signing](docs/ASSEMBLY_SIGNING.md)
- [Changelog](CHANGELOG.md)

## 🤝 Contributing

Contributions welcome! Please read our contributing guidelines first.

## 📄 License

MPL-2.0 - see [LICENSE](LICENSE) for details.

## 🙏 Acknowledgments

Built with the [Baryo.Dev](https://github.com/BaryoDev) philosophy: zero dependencies, minimal allocations, safe by default.

---

**Made with ❤️ by Baryo.Dev**

## 📊 Performance

Carom is **significantly faster** than Polly v8:

- **175,000x faster** startup (0.02ns vs 3,857ns)
- **15x faster** hot path (10.9ns vs 167.8ns)  
- **4.8x faster** async operations (45ns vs 216ns)

See [detailed benchmarks](docs/BENCHMARKS.md) for complete analysis.
