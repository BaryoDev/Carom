# Carom Package Architecture: Modular, Zero-Dependency Core

**Philosophy**: Keep the core package absolutely zero-dependency and ultra-performant. Build specialized packages for additional features.

---

## Package Strategy Overview

```
Carom Ecosystem
├── Carom (Core) ⭐ ZERO DEPENDENCIES
├── Carom.Http (HTTP Client Integration)
├── Carom.Extensions (Circuit Breaker, Fallback, Advanced)
├── Carom.Telemetry.OpenTelemetry (Optional OTel Integration)
├── Carom.AspNetCore (Middleware, DI Extensions)
└── Carom.EntityFramework (EF Core Resilience)
```

---

## 1. Carom (Core Package) - The Foundation

**NuGet**: `Carom`
**Dependencies**: **ZERO** (only netstandard2.0 BCL)
**Size Target**: <50KB
**Purpose**: Pure retry logic with decorrelated jitter

### What's Included

✅ **Retry Engine**
- `Carom.Shot()` / `Carom.ShotAsync()`
- Synchronous and async overloads
- Decorrelated jitter (mandatory by default)
- Exception filtering via `Func<Exception, bool>`

✅ **Configuration**
- `Bounce` struct (fluent API)
- `JitterStrategy` (internal, decorrelated jitter)
- Zero allocations on hot path

✅ **Zero Dependencies**
- No NuGet packages
- Only System.* namespaces (BCL)
- netstandard2.0 compatible

### What's NOT Included

❌ HTTP-specific logic → `Carom.Http`
❌ Circuit Breaker → `Carom.Extensions`
❌ Fallback → `Carom.Extensions`
❌ Telemetry hooks → `Carom.Extensions`
❌ DI integration → `Carom.AspNetCore`

### Current State

**Status**: ✅ Complete (v1.0.0)

**Files**:
- [Carom.cs](src/Carom/Carom.cs) - Core retry engine
- [Bounce.cs](src/Carom/Bounce.cs) - Configuration struct
- [JitterStrategy.cs](src/Carom/JitterStrategy.cs) - Jitter calculation

**Performance**:
- Zero startup overhead
- Zero allocations on success path
- Thread-safe jitter generation

---

## 2. Carom.Http - HTTP Client Integration

**NuGet**: `Carom.Http`
**Dependencies**: `Carom` + `System.Net.Http` (already in BCL)
**Size Target**: <20KB
**Purpose**: Drop-in HttpClient resilience

### What's Included

✅ **CaromHttpHandler**
- DelegatingHandler for automatic HTTP retries
- Transient error detection (503, 504, 502, 429, 408)
- Respects `Retry-After` headers (future enhancement)

✅ **Extension Methods**
- `AddCaromHandler()` for HttpClientFactory
- Fluent configuration

### Current State

**Status**: ✅ Complete (v1.0.0)

**Files**:
- [CaromHttpHandler.cs](src/Carom.Http/CaromHttpHandler.cs)
- [CaromHttpExtensions.cs](src/Carom.Http/CaromHttpExtensions.cs)

### Future Enhancements

- ⬜ Respect `Retry-After` header
- ⬜ Circuit breaker integration (when `Carom.Extensions` exists)
- ⬜ Hedging support for idempotent requests (v2.0+)

---

## 3. Carom.Extensions - Advanced Resilience Patterns

**NuGet**: `Carom.Extensions`
**Dependencies**: `Carom` only
**Size Target**: <100KB
**Purpose**: Circuit Breaker, Fallback, Timeout, Telemetry

### What's Included

#### 3.1 Circuit Breaker ("Cushion")

✅ **Passive Circuit Breaker**
- No background threads
- Atomic state transitions via `Interlocked`
- Sliding window failure tracking
- Half-open state testing

**API Design**:
```csharp
var cushion = Cushion.ForService("payment-api")
    .OpenAfter(failures: 5, within: 10)
    .HalfOpenAfter(TimeSpan.FromSeconds(30));

var result = await Carom.ShotAsync(() => api.Pay(), cushion);
```

**Implementation**:
```csharp
// Cushion.cs (new file)
public readonly struct Cushion
{
    public string ServiceKey { get; }
    public int FailureThreshold { get; }
    public int SamplingWindow { get; }
    public TimeSpan HalfOpenDelay { get; }

    // Static state store (per service)
    private static readonly ConcurrentDictionary<string, CushionState> States = new();
}

// CushionState.cs (internal)
internal struct CushionState
{
    public CircuitState State; // Closed, Open, HalfOpen
    public int FailureCount;
    public int SuccessCount;
    public long LastFailureTimestamp; // Interlocked operations
}
```

#### 3.2 Fallback ("Safety Pocket")

✅ **Zero-Allocation Fallback**
- Extension methods on `T` and `Task<T>`
- Lazy evaluation of fallback functions
- Composable with retry

**API Design**:
```csharp
// Inline value fallback
var data = Carom.Shot(() => cache.Get(key))
    .Pocket(fallbackValue);

// Lazy fallback function
var user = await Carom.ShotAsync(() => primaryDb.GetUser(id))
    .PocketAsync(async () => await backupDb.GetUser(id));
```

**Implementation**:
```csharp
// CaromExtensions.cs
public static class CaromExtensions
{
    public static T Pocket<T>(this T result, T fallback) => result;

    public static T Pocket<T>(this Func<T> action, T fallback)
    {
        try { return action(); }
        catch { return fallback; }
    }

    // Async variants...
}
```

#### 3.3 Timeout ("Shot Clock")

✅ **CancellationToken-Based Timeout**
- Wraps existing async operations
- Integrates with existing CT parameter
- Throws `TimeoutRejectedException`

**API Design**:
```csharp
// Simple timeout
await Carom.ShotAsync(
    () => api.SlowOp(),
    timeout: TimeSpan.FromSeconds(5)
);

// Bounce with timeout
var bounce = Bounce.Times(3)
    .WithTimeout(TimeSpan.FromSeconds(5));
```

**Implementation**:
```csharp
// Enhance Carom.cs
public static async Task<T> ShotAsync<T>(
    Func<Task<T>> action,
    TimeSpan? timeout = null,
    int retries = 3,
    // ... existing params
)
{
    using var cts = timeout.HasValue
        ? new CancellationTokenSource(timeout.Value)
        : null;

    var ct = cts?.Token ?? CancellationToken.None;
    // ... existing retry logic with ct
}
```

#### 3.4 Telemetry ("Scoreboard")

✅ **Event-Based Telemetry**
- Zero allocations if no subscribers
- Struct event args (stack-allocated)
- No OpenTelemetry dependency

**API Design**:
```csharp
// Global telemetry
Carom.Telemetry.OnShotRetry += (sender, e) =>
{
    logger.LogWarning("Retry {Attempt} after {Delay}ms: {Exception}",
        e.Attempt, e.Delay.TotalMilliseconds, e.Exception.Message);
};

// Per-shot telemetry
var result = await Carom.ShotAsync(
    () => api.Call(),
    onRetry: (attempt, delay, ex) => logger.LogWarning(...)
);
```

**Implementation**:
```csharp
// TelemetryEvents.cs
public static class CaromTelemetry
{
    public static event EventHandler<ShotStartedEventArgs>? OnShotStarted;
    public static event EventHandler<ShotRetryEventArgs>? OnShotRetry;
    public static event EventHandler<ShotCompletedEventArgs>? OnShotCompleted;
    public static event EventHandler<CircuitOpenedEventArgs>? OnCircuitOpened;
}

public readonly struct ShotRetryEventArgs
{
    public int Attempt { get; }
    public TimeSpan Delay { get; }
    public Exception Exception { get; }
}
```

### Why This is a Separate Package

- Keeps core package minimal (<50KB)
- Circuit breaker requires `ConcurrentDictionary` state
- Telemetry events add size
- Optional for users who only need retry

**Status**: 🚧 Planned for v1.1-1.3

---

## 4. Carom.Telemetry.OpenTelemetry - OTel Integration

**NuGet**: `Carom.Telemetry.OpenTelemetry`
**Dependencies**: `Carom.Extensions` + `OpenTelemetry.Api`
**Size Target**: <30KB
**Purpose**: First-party OpenTelemetry integration

### What's Included

✅ **ActivitySource Integration**
- Traces for retry attempts
- Spans for circuit breaker state changes
- Metrics for success/failure rates

✅ **Metrics Provider**
- `carom.retry.attempts` counter
- `carom.retry.delay` histogram
- `carom.circuit.state` gauge

**API Design**:
```csharp
// Program.cs
services.AddOpenTelemetry()
    .WithTracing(builder => builder.AddCarom())
    .WithMetrics(builder => builder.AddCarom());
```

**Implementation**:
```csharp
// CaromOpenTelemetryExtensions.cs
public static class CaromOpenTelemetryExtensions
{
    private static readonly ActivitySource ActivitySource =
        new("Carom", "1.0.0");

    public static TracerProviderBuilder AddCarom(this TracerProviderBuilder builder)
    {
        CaromTelemetry.OnShotRetry += (s, e) =>
        {
            using var activity = ActivitySource.StartActivity("carom.retry");
            activity?.SetTag("retry.attempt", e.Attempt);
            // ... more tags
        };

        return builder.AddSource("Carom");
    }
}
```

### Why This is a Separate Package

- Adds OpenTelemetry dependency (violates core zero-dep rule)
- Not everyone uses OTel
- Keeps core package pure

**Status**: 🚧 Planned for v1.4

---

## 5. Carom.AspNetCore - Middleware & DI Integration

**NuGet**: `Carom.AspNetCore`
**Dependencies**: `Carom.Extensions` + `Microsoft.AspNetCore.Http`
**Size Target**: <40KB
**Purpose**: ASP.NET Core middleware, DI, HttpClientFactory

### What's Included

✅ **Middleware**
- Automatic retry for transient controller exceptions
- Circuit breaker per endpoint
- Request/response telemetry

✅ **DI Extensions**
- `services.AddCarom()` configuration
- Named circuit breakers
- Scoped/singleton resilience policies

✅ **HttpClientFactory Integration**
- `AddHttpClient().AddCaromHandler()`
- Named client configurations

**API Design**:
```csharp
// Startup.cs / Program.cs
services.AddCarom(options =>
{
    options.DefaultRetries = 3;
    options.DefaultBaseDelay = TimeSpan.FromMilliseconds(100);
    options.EnableTelemetry = true;
});

// Named circuit breakers
services.AddCaromCircuitBreaker("payment-api", options =>
{
    options.FailureThreshold = 5;
    options.SamplingWindow = 10;
});

// HttpClient integration
services.AddHttpClient("external-api")
    .AddCaromHandler(retries: 3);
```

**Middleware**:
```csharp
// Middleware/CaromMiddleware.cs
app.UseCarom(options =>
{
    options.RetryOnStatus(503, 504);
    options.CircuitBreakerPerEndpoint = true;
});
```

### Why This is a Separate Package

- ASP.NET Core dependency (not all .NET apps use it)
- Keeps core package portable (Blazor, Unity, console apps)
- Middleware is framework-specific

**Status**: 🚧 Planned for v1.5

---

## 6. Carom.EntityFramework - EF Core Resilience

**NuGet**: `Carom.EntityFramework`
**Dependencies**: `Carom.Extensions` + `Microsoft.EntityFrameworkCore`
**Size Target**: <30KB
**Purpose**: Database retry strategies for EF Core

### What's Included

✅ **DbContext Extensions**
- Transient SQL error detection (timeout, deadlock, connection loss)
- Circuit breaker for database connections
- Retry strategies for SaveChanges

✅ **Execution Strategy Integration**
- Drop-in replacement for `SqlServerRetryingExecutionStrategy`
- Decorrelated jitter (EF's default is linear)

**API Design**:
```csharp
// DbContext configuration
services.AddDbContext<MyDbContext>(options =>
{
    options.UseSqlServer(connectionString)
        .UseCaromExecutionStrategy(retries: 5);
});

// Manual retry
await dbContext.ExecuteWithCaromAsync(async () =>
{
    await dbContext.Orders.AddAsync(order);
    await dbContext.SaveChangesAsync();
});
```

**Implementation**:
```csharp
// CaromExecutionStrategy.cs
public class CaromExecutionStrategy : IExecutionStrategy
{
    private readonly Bounce _bounce;

    public bool RetriesOnFailure => true;

    public TResult Execute<TState, TResult>(
        TState state,
        Func<DbContext, TState, TResult> operation,
        Func<DbContext, TState, ExecutionResult<TResult>> verifySucceeded)
    {
        return Carom.Shot(() => operation(_context, state), _bounce);
    }
}
```

### Why This is a Separate Package

- EF Core dependency (not all apps use EF)
- Database-specific retry logic
- Keeps core package ORM-agnostic

**Status**: 🚧 Planned for v1.6

---

## Package Dependency Graph

```
Carom (Core) ⭐ ZERO DEPS
    ├── Carom.Http
    ├── Carom.Extensions
    │   ├── Carom.Telemetry.OpenTelemetry
    │   ├── Carom.AspNetCore
    │   └── Carom.EntityFramework
    └── (Future: Carom.Grpc, Carom.Azure, etc.)
```

**Key Principle**: Every package depends on `Carom` (core), but NEVER the reverse.

---

## Developer Experience: Why Developers Will Prefer Carom

### 1. Pay-for-What-You-Use Model

**Polly Approach**:
```bash
dotnet add package Polly
# Gets: Retry + Circuit Breaker + Bulkhead + Rate Limiter + Hedging
# Even if you only need retry
```

**Carom Approach**:
```bash
# Minimal: Just retry with jitter
dotnet add package Carom

# HTTP: Retry + HTTP handler
dotnet add package Carom.Http

# Advanced: Retry + Circuit Breaker + Fallback + Timeout
dotnet add package Carom.Extensions

# Full stack: ASP.NET Core integration
dotnet add package Carom.AspNetCore
```

**Developer Win**: Smaller package size, faster restore, less cognitive load.

---

### 2. Simpler API for Common Cases

**Polly v8 - Simple Retry**:
```csharp
// Step 1: Build pipeline
var pipeline = new ResiliencePipelineBuilder()
    .AddRetry(new RetryStrategyOptions
    {
        MaxRetryAttempts = 3,
        Delay = TimeSpan.FromMilliseconds(100),
        BackoffType = DelayBackoffType.Exponential,
        UseJitter = true
    })
    .Build();

// Step 2: Execute
var result = await pipeline.ExecuteAsync(
    async ct => await api.GetData(),
    CancellationToken.None);
```

**Carom - Simple Retry**:
```csharp
// One line, safe jitter by default
var result = await Carom.ShotAsync(() => api.GetData());
```

**Developer Win**: 10x less code for the 80% use case.

---

### 3. Physics-Based Mental Model

**Polly**: Policy, Pipeline, Strategy, Context, Bulkhead, Hedging
**Carom**: Shot, Bounce, Cushion, Pocket, Shot Clock

**Developer Win**: Intuitive metaphor, easier to remember, fun to use.

---

### 4. Zero Cold Start for Serverless

**AWS Lambda with Polly**:
```csharp
// This runs EVERY cold start (50-200ms penalty)
var pipeline = new ResiliencePipelineBuilder()
    .AddRetry(...)
    .AddCircuitBreaker(...)
    .Build();

public async Task<APIGatewayProxyResponse> Handler(APIGatewayProxyRequest request)
{
    return await pipeline.ExecuteAsync(...);
}
```

**AWS Lambda with Carom**:
```csharp
// Zero setup, static methods only
public async Task<APIGatewayProxyResponse> Handler(APIGatewayProxyRequest request)
{
    return await Carom.ShotAsync(() => api.Process(request));
}
```

**Developer Win**: Faster cold starts = lower latency = lower cost.

---

### 5. No Dependency Hell

**Polly Dependency Tree** (example):
```
Polly 8.6.5
├── System.ComponentModel.Annotations
├── System.Threading.Tasks.Extensions
├── Microsoft.Extensions.Logging.Abstractions
└── (Transitive dependencies)
```

**Carom Core Dependency Tree**:
```
Carom 1.0.0
└── (None - only BCL)
```

**Developer Win**: No version conflicts, easier to audit, zero security debt.

---

### 6. Telemetry Without Lock-In

**Polly Approach**:
- Built-in OpenTelemetry (requires OTel dependency)
- Or manual logging via `OnRetry` callbacks

**Carom Approach**:
```csharp
// Option 1: Events (no dependency)
Carom.Telemetry.OnShotRetry += (s, e) => logger.Log(...);

// Option 2: OpenTelemetry (separate package)
dotnet add package Carom.Telemetry.OpenTelemetry
```

**Developer Win**: Use any logging framework, no forced dependencies.

---

### 7. Safe by Default

**Polly**:
```csharp
// No jitter by default (DANGEROUS!)
.AddRetry(new RetryStrategyOptions
{
    MaxRetryAttempts = 3,
    Delay = TimeSpan.FromMilliseconds(100),
    UseJitter = false // Default is false!
})
```

**Carom**:
```csharp
// Jitter is MANDATORY by default
await Carom.ShotAsync(() => api.Call());

// You have to explicitly opt-out (not recommended)
await Carom.ShotAsync(() => api.Call(),
    Bounce.Times(3).WithoutJitter());
```

**Developer Win**: Prevents production thundering herds without configuration.

---

### 8. Benchmark-Driven Development

**Polly**: Benchmarks exist, but not in CI
**Carom**: Every PR runs benchmarks vs Polly, fails if slower

**Developer Win**: Performance guarantees, not promises.

---

## Package Release Strategy

### Version Alignment

All packages share the **same major.minor version**:
- `Carom 1.2.0`
- `Carom.Http 1.2.0`
- `Carom.Extensions 1.2.0`

**Why**: Simplifies compatibility, users know versions work together.

### Release Cadence

- **Core**: Only on breaking changes or critical bugs
- **Extensions**: Feature releases every quarter
- **Integrations**: As needed based on framework updates

### Backward Compatibility

- **Core**: Semantic versioning, no breaking changes in minor releases
- **Extensions**: Can break in minor releases (pre-v2.0)
- **Integrations**: Follow ASP.NET Core / EF Core breaking changes

---

## Security Model

### Supply Chain Security

| Package | Dependencies | Audit Frequency | Scan Tools |
|:--------|:-------------|:----------------|:-----------|
| Carom | **ZERO** | N/A | N/A |
| Carom.Http | BCL only | N/A | N/A |
| Carom.Extensions | Carom only | N/A | N/A |
| Carom.Telemetry.OTel | OpenTelemetry.Api | Weekly | Dependabot |
| Carom.AspNetCore | Microsoft.AspNetCore.Http | Weekly | Dependabot |
| Carom.EntityFramework | Microsoft.EntityFrameworkCore | Weekly | Dependabot |

**Key Insight**: Only integration packages have external dependencies, and they're all Microsoft-owned.

### Code Signing

- All packages signed with strong name
- NuGet package signature verification
- PGP signatures for release artifacts

### Vulnerability Disclosure

- Security email: security@baryo.dev
- Response SLA: 48 hours
- Public disclosure: After fix + 30 days

---

## Migration Path from Polly

### Step 1: Retry Only

**Before (Polly)**:
```csharp
var pipeline = new ResiliencePipelineBuilder()
    .AddRetry(new RetryStrategyOptions { MaxRetryAttempts = 3 })
    .Build();

await pipeline.ExecuteAsync(async ct => await api.Call());
```

**After (Carom)**:
```bash
dotnet remove package Polly
dotnet add package Carom
```
```csharp
await Carom.ShotAsync(() => api.Call());
```

---

### Step 2: Retry + Circuit Breaker

**Before (Polly)**:
```csharp
var pipeline = new ResiliencePipelineBuilder()
    .AddRetry(new RetryStrategyOptions { MaxRetryAttempts = 3 })
    .AddCircuitBreaker(new CircuitBreakerStrategyOptions { ... })
    .Build();
```

**After (Carom)**:
```bash
dotnet add package Carom.Extensions
```
```csharp
var cushion = Cushion.ForService("api").OpenAfter(5, 10);
await Carom.ShotAsync(() => api.Call(), cushion);
```

---

### Step 3: Full ASP.NET Core Integration

**Before (Polly)**:
```csharp
services.AddHttpClient("api")
    .AddPolicyHandler(GetRetryPolicy())
    .AddPolicyHandler(GetCircuitBreakerPolicy());
```

**After (Carom)**:
```bash
dotnet add package Carom.AspNetCore
```
```csharp
services.AddHttpClient("api")
    .AddCaromHandler(retries: 3)
    .AddCaromCircuitBreaker("api");
```

---

## Competitive Analysis: Feature Parity Matrix

| Feature | Polly v8 | Carom Core | Carom.Extensions | Carom.AspNetCore |
|:--------|:---------|:-----------|:-----------------|:-----------------|
| Retry | ✅ | ✅ | ✅ | ✅ |
| Jitter (Default) | ❌ Opt-in | ✅ Mandatory | ✅ Mandatory | ✅ Mandatory |
| Circuit Breaker | ✅ | ❌ | ✅ Planned | ✅ Planned |
| Timeout | ✅ | ⚠️ Manual CT | ✅ Planned | ✅ Planned |
| Fallback | ✅ | ❌ | ✅ Planned | ✅ Planned |
| Bulkhead | ✅ | ❌ | 🤔 Maybe v2.0 | 🤔 Maybe v2.0 |
| Rate Limiter | ✅ | ❌ | 🤔 Maybe v2.0 | 🤔 Maybe v2.0 |
| Hedging | ✅ | ❌ | ❌ Out of scope | ❌ Out of scope |
| Telemetry | ✅ OTel | ❌ | ✅ Events | ✅ Events + OTel |
| Chaos Engineering | ✅ Simmy | ❌ | ❌ Out of scope | ❌ Out of scope |
| **Startup Overhead** | 🔴 High | ✅ Zero | ✅ Zero | ✅ Zero |
| **Dependencies** | 🔴 Multiple | ✅ Zero | ✅ Zero | ⚠️ ASP.NET Core |
| **Package Size** | 🔴 Large | ✅ <50KB | ✅ <100KB | ✅ <40KB |
| **netstandard2.0** | ✅ | ✅ | ✅ | ❌ (requires .NET 6+) |

---

## Success Criteria for "Developers Will Prefer Carom"

### Quantitative Metrics

- ✅ **Performance**: 10x faster startup than Polly (measured in benchmarks)
- ✅ **Size**: <50KB core vs Polly's larger footprint
- ✅ **Simplicity**: 3 lines of code vs 10+ for simple retry
- ✅ **Adoption**: 10K NuGet downloads/month within 6 months

### Qualitative Metrics

- ✅ **"It just works"**: Zero config for 80% of use cases
- ✅ **"Feels lightweight"**: Developers notice faster cold starts
- ✅ **"Easy to understand"**: Physics metaphor resonates
- ✅ **"Trustworthy"**: Zero dependencies = zero security concerns

### Developer Testimonials (Target)

> "Switched from Polly to Carom and our Lambda cold starts dropped 40ms. No config needed."

> "Finally, a resilience library that doesn't bring 10 other packages with it."

> "The 'Shot/Bounce/Cushion' API is so much clearer than Policy/Pipeline/Strategy."

---

## Conclusion

**Modular package architecture enables Carom to compete with Polly by offering:**

1. **Choice**: Use only what you need (vs Polly's monolith)
2. **Performance**: Core package has zero overhead (vs Polly's pipeline tax)
3. **Security**: Core package has zero attack surface (vs Polly's dependencies)
4. **Simplicity**: Intuitive API for common cases (vs Polly's enterprise verbosity)

**The strategy is clear**:
- Keep `Carom` (core) absolutely pure - zero dependencies, maximum performance
- Build specialized packages for advanced features (circuit breaker, telemetry, etc.)
- Integrate deeply with frameworks via opt-in packages (ASP.NET Core, EF Core)
- Never compromise the core for the sake of features

**Developers will prefer Carom when they value**:
- Speed over comprehensiveness
- Simplicity over flexibility
- Security over features
- Clarity over configuration

**Next Steps**:
1. Finalize `Carom.Extensions` package design
2. Prototype circuit breaker (validate zero-allocation claim)
3. Benchmark every feature vs Polly
4. Build migration guides with side-by-side comparisons
5. Launch with clear positioning: "80% of Polly, 20% of the weight"

---

**Document Version**: 1.0
**Last Updated**: 2025-12-27
**Next Review**: After Phase 1 implementation (Q1 2025)
