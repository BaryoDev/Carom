# Carom Implementation Roadmap: Q1-Q4 2025

**Philosophy**: Baryo.Dev - Every feature must justify its weight, allocations, and complexity.

---

## Phase 1: Foundation Enhancement (Q1 2025)

### 1.1 Circuit Breaker ("Cushion") - v1.1.0

**Target**: February 2025
**Package**: `Carom.Extensions`
**Complexity**: High
**Performance Goal**: <10ns overhead when circuit closed, zero allocations

#### Design Specification

**File Structure**:
```
src/Carom.Extensions/
├── Cushion.cs (public API)
├── CushionState.cs (internal state)
├── CircuitState.cs (enum: Closed, Open, HalfOpen)
└── CushionStore.cs (static state management)
```

**Core Implementation**:

```csharp
// Cushion.cs
namespace Carom.Extensions
{
    /// <summary>
    /// Circuit breaker configuration for protecting failing services.
    /// The "cushion" absorbs repeated impacts before breaking.
    /// </summary>
    public readonly struct Cushion
    {
        public string ServiceKey { get; }
        public int FailureThreshold { get; }
        public int SamplingWindow { get; }
        public TimeSpan HalfOpenDelay { get; }

        private Cushion(string key, int threshold, int window, TimeSpan delay)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Service key required", nameof(key));
            if (threshold < 1)
                throw new ArgumentException("Threshold must be >= 1", nameof(threshold));
            if (window < threshold)
                throw new ArgumentException("Window must be >= threshold", nameof(window));

            ServiceKey = key;
            FailureThreshold = threshold;
            SamplingWindow = window;
            HalfOpenDelay = delay;
        }

        /// <summary>
        /// Creates a cushion for the specified service.
        /// </summary>
        public static CushionBuilder ForService(string serviceKey) =>
            new CushionBuilder(serviceKey);

        /// <summary>
        /// Executes an action with circuit breaker protection.
        /// </summary>
        internal T Execute<T>(Func<T> action)
        {
            var state = CushionStore.GetOrCreate(ServiceKey, this);

            // Fast path: circuit closed, just track
            if (state.State == CircuitState.Closed)
            {
                try
                {
                    var result = action();
                    state.RecordSuccess();
                    return result;
                }
                catch (Exception ex)
                {
                    state.RecordFailure();

                    // Check if we should open circuit
                    if (state.ShouldOpen(FailureThreshold, SamplingWindow))
                    {
                        state.Open(HalfOpenDelay);
                        throw new CircuitOpenException(ServiceKey, ex);
                    }

                    throw;
                }
            }

            // Circuit open: reject immediately
            if (state.State == CircuitState.Open)
            {
                // Check if we can transition to half-open
                if (state.CanAttemptReset(HalfOpenDelay))
                {
                    state.TransitionToHalfOpen();
                }
                else
                {
                    throw new CircuitOpenException(ServiceKey);
                }
            }

            // Half-open: test with one request
            if (state.State == CircuitState.HalfOpen)
            {
                try
                {
                    var result = action();
                    state.Close(); // Success! Close circuit
                    return result;
                }
                catch (Exception ex)
                {
                    state.Open(HalfOpenDelay); // Failed, reopen
                    throw new CircuitOpenException(ServiceKey, ex);
                }
            }

            throw new InvalidOperationException($"Invalid circuit state: {state.State}");
        }
    }

    public class CushionBuilder
    {
        private readonly string _serviceKey;
        private int _threshold = 5;
        private int _window = 10;
        private TimeSpan _delay = TimeSpan.FromSeconds(30);

        internal CushionBuilder(string serviceKey) => _serviceKey = serviceKey;

        public CushionBuilder OpenAfter(int failures, int within)
        {
            _threshold = failures;
            _window = within;
            return this;
        }

        public Cushion HalfOpenAfter(TimeSpan delay)
        {
            _delay = delay;
            return new Cushion(_serviceKey, _threshold, _window, _delay);
        }
    }
}
```

```csharp
// CushionState.cs (internal, lock-free)
namespace Carom.Extensions
{
    internal class CushionState
    {
        private int _state; // 0=Closed, 1=Open, 2=HalfOpen
        private int _failureCount;
        private int _successCount;
        private long _lastFailureTicks;
        private readonly RingBuffer<bool> _recentResults;

        public CircuitState State => (CircuitState)Volatile.Read(ref _state);

        public CushionState(int samplingWindow)
        {
            _recentResults = new RingBuffer<bool>(samplingWindow);
        }

        public void RecordSuccess()
        {
            _recentResults.Add(true);
            Interlocked.Increment(ref _successCount);
        }

        public void RecordFailure()
        {
            _recentResults.Add(false);
            Interlocked.Increment(ref _failureCount);
            Interlocked.Exchange(ref _lastFailureTicks, DateTime.UtcNow.Ticks);
        }

        public bool ShouldOpen(int threshold, int window)
        {
            var failures = _recentResults.Count(x => !x);
            var total = _recentResults.Count;

            return total >= window && failures >= threshold;
        }

        public void Open(TimeSpan delay)
        {
            Interlocked.Exchange(ref _state, (int)CircuitState.Open);
            Interlocked.Exchange(ref _lastFailureTicks, DateTime.UtcNow.Ticks);
        }

        public void Close()
        {
            Interlocked.Exchange(ref _state, (int)CircuitState.Closed);
            Interlocked.Exchange(ref _failureCount, 0);
        }

        public bool CanAttemptReset(TimeSpan delay)
        {
            var lastFailure = new DateTime(Volatile.Read(ref _lastFailureTicks));
            return DateTime.UtcNow - lastFailure >= delay;
        }

        public void TransitionToHalfOpen()
        {
            Interlocked.Exchange(ref _state, (int)CircuitState.HalfOpen);
        }
    }

    // Simple ring buffer for recent results (lock-free)
    internal class RingBuffer<T>
    {
        private readonly T[] _buffer;
        private int _index;

        public RingBuffer(int capacity) => _buffer = new T[capacity];

        public void Add(T item)
        {
            var idx = Interlocked.Increment(ref _index) - 1;
            _buffer[idx % _buffer.Length] = item;
        }

        public int Count => Math.Min(Volatile.Read(ref _index), _buffer.Length);

        public int Count(Func<T, bool> predicate)
        {
            var count = Count;
            var matched = 0;

            for (int i = 0; i < count; i++)
            {
                if (predicate(_buffer[i]))
                    matched++;
            }

            return matched;
        }
    }
}
```

**Integration with Core Carom**:

```csharp
// Extension method on Carom class
namespace Carom.Extensions
{
    public static class CaromCushionExtensions
    {
        public static T Shot<T>(Func<T> action, Cushion cushion)
        {
            return cushion.Execute(() => Carom.Shot(action));
        }

        public static async Task<T> ShotAsync<T>(
            Func<Task<T>> action,
            Cushion cushion,
            CancellationToken ct = default)
        {
            return await cushion.Execute(
                () => Carom.ShotAsync(action, ct: ct));
        }
    }
}
```

**Benchmark Target**:
- Circuit closed overhead: <10ns
- Circuit open rejection: <5ns
- State transition: <100ns
- Memory: Zero allocations on hot path

---

### 1.2 Fallback ("Safety Pocket") - v1.2.0

**Target**: March 2025
**Package**: `Carom.Extensions`
**Complexity**: Low
**Performance Goal**: Zero allocations if fallback not invoked

#### Implementation

```csharp
// CaromFallbackExtensions.cs
namespace Carom.Extensions
{
    public static class CaromFallbackExtensions
    {
        /// <summary>
        /// Returns a fallback value if the shot fails.
        /// </summary>
        public static T Pocket<T>(this Func<T> action, T fallback)
        {
            try
            {
                return Carom.Shot(action);
            }
            catch
            {
                return fallback;
            }
        }

        /// <summary>
        /// Executes a fallback function if the shot fails.
        /// </summary>
        public static T Pocket<T>(this Func<T> action, Func<T> fallbackFn)
        {
            try
            {
                return Carom.Shot(action);
            }
            catch
            {
                return fallbackFn();
            }
        }

        /// <summary>
        /// Returns a fallback value if the async shot fails.
        /// </summary>
        public static async Task<T> PocketAsync<T>(
            this Func<Task<T>> action,
            T fallback,
            CancellationToken ct = default)
        {
            try
            {
                return await Carom.ShotAsync(action, ct: ct);
            }
            catch
            {
                return fallback;
            }
        }

        /// <summary>
        /// Executes an async fallback function if the shot fails.
        /// </summary>
        public static async Task<T> PocketAsync<T>(
            this Func<Task<T>> action,
            Func<Task<T>> fallbackFn,
            CancellationToken ct = default)
        {
            try
            {
                return await Carom.ShotAsync(action, ct: ct);
            }
            catch
            {
                return await fallbackFn();
            }
        }

        /// <summary>
        /// Executes an async fallback with access to the exception.
        /// </summary>
        public static async Task<T> PocketAsync<T>(
            this Func<Task<T>> action,
            Func<Exception, Task<T>> fallbackFn,
            CancellationToken ct = default)
        {
            try
            {
                return await Carom.ShotAsync(action, ct: ct);
            }
            catch (Exception ex)
            {
                return await fallbackFn(ex);
            }
        }
    }
}
```

**Usage Examples**:

```csharp
// Simple value fallback
var data = new Func<string>(() => cache.Get("key"))
    .Pocket("default-value");

// Fallback chain
var user = await new Func<Task<User>>(() => primaryDb.GetUser(id))
    .PocketAsync(async () => await secondaryDb.GetUser(id))
    .PocketAsync(async () => await cacheDb.GetUser(id));

// Exception-aware fallback
var result = await new Func<Task<Result>>(() => api.Process())
    .PocketAsync(async (ex) =>
    {
        logger.LogError(ex, "Primary failed, using backup");
        return await backupApi.Process();
    });
```

**Benchmark Target**:
- Success path: Zero allocation difference vs plain `Carom.Shot`
- Fallback path: Only fallback function allocates

---

### 1.3 Timeout Enhancement ("Shot Clock") - v1.3.0

**Target**: March 2025
**Package**: `Carom` (core enhancement)
**Complexity**: Medium
**Performance Goal**: Only allocate CancellationTokenSource when timeout specified

#### Implementation

```csharp
// Enhance existing Carom.cs
public static class Carom
{
    public static async Task<T> ShotAsync<T>(
        Func<Task<T>> action,
        int retries = 3,
        TimeSpan? baseDelay = null,
        TimeSpan? timeout = null, // NEW
        Func<Exception, bool>? shouldBounce = null,
        bool disableJitter = false,
        CancellationToken ct = default)
    {
        // Create timeout token if needed
        using var timeoutCts = timeout.HasValue
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : null;

        if (timeoutCts != null)
        {
            timeoutCts.CancelAfter(timeout!.Value);
            ct = timeoutCts.Token;
        }

        // Existing retry logic with ct
        // ... (use ct throughout)
    }

    // Overload with Bounce
    public static Task<T> ShotAsync<T>(
        Func<Task<T>> action,
        Bounce bounce,
        CancellationToken ct = default)
    {
        return ShotAsync(
            action,
            bounce.Retries,
            bounce.BaseDelay,
            bounce.Timeout, // NEW property on Bounce
            bounce.ShouldBounce,
            bounce.DisableJitter,
            ct);
    }
}

// Enhance Bounce struct
public readonly struct Bounce
{
    public TimeSpan? Timeout { get; } // NEW

    public Bounce WithTimeout(TimeSpan timeout) =>
        new Bounce(Retries, BaseDelay, timeout, DisableJitter, ShouldBounce);
}
```

**Usage**:

```csharp
// Direct timeout
await Carom.ShotAsync(
    () => api.SlowOperation(),
    timeout: TimeSpan.FromSeconds(5));

// Bounce with timeout
var bounce = Bounce.Times(3)
    .WithDelay(TimeSpan.FromMilliseconds(100))
    .WithTimeout(TimeSpan.FromSeconds(5));

await Carom.ShotAsync(() => api.Call(), bounce);
```

---

## Phase 2: Observability (Q2 2025)

### 2.1 Telemetry Events ("Scoreboard") - v1.4.0

**Target**: April 2025
**Package**: `Carom.Extensions`
**Complexity**: Medium
**Performance Goal**: Zero allocations if no subscribers

#### Implementation

```csharp
// CaromTelemetry.cs
namespace Carom.Extensions
{
    public static class CaromTelemetry
    {
        public static event EventHandler<ShotStartedEventArgs>? OnShotStarted;
        public static event EventHandler<ShotRetryEventArgs>? OnShotRetry;
        public static event EventHandler<ShotCompletedEventArgs>? OnShotCompleted;
        public static event EventHandler<ShotFailedEventArgs>? OnShotFailed;
        public static event EventHandler<CircuitOpenedEventArgs>? OnCircuitOpened;
        public static event EventHandler<CircuitClosedEventArgs>? OnCircuitClosed;

        internal static void NotifyRetry(int attempt, TimeSpan delay, Exception exception)
        {
            OnShotRetry?.Invoke(null, new ShotRetryEventArgs(attempt, delay, exception));
        }

        // ... other notification methods
    }

    public readonly struct ShotRetryEventArgs
    {
        public int Attempt { get; }
        public TimeSpan Delay { get; }
        public Exception Exception { get; }
        public DateTime Timestamp { get; }

        public ShotRetryEventArgs(int attempt, TimeSpan delay, Exception exception)
        {
            Attempt = attempt;
            Delay = delay;
            Exception = exception;
            Timestamp = DateTime.UtcNow;
        }
    }

    // ... other event arg structs
}
```

**Integration**:

```csharp
// In Carom.cs, add telemetry hooks
public static async Task<T> ShotAsync<T>(...)
{
    CaromTelemetry.NotifyStarted();

    for (int attempt = 0; attempt <= retries; attempt++)
    {
        try
        {
            var result = await action();
            CaromTelemetry.NotifyCompleted(attempt, success: true);
            return result;
        }
        catch (Exception ex)
        {
            CaromTelemetry.NotifyRetry(attempt + 1, nextDelay, ex);
            // ... existing logic
        }
    }
}
```

**Usage**:

```csharp
// Application startup
CaromTelemetry.OnShotRetry += (sender, e) =>
{
    logger.LogWarning(
        e.Exception,
        "Retry attempt {Attempt} after {Delay}ms",
        e.Attempt,
        e.Delay.TotalMilliseconds);
};

CaromTelemetry.OnCircuitOpened += (sender, e) =>
{
    logger.LogError(
        "Circuit opened for {ServiceKey} after {FailureCount} failures",
        e.ServiceKey,
        e.FailureCount);

    // Alert on-call engineer
    alertService.SendAlert($"Circuit breaker opened: {e.ServiceKey}");
};
```

---

### 2.2 OpenTelemetry Integration - v1.4.1

**Target**: May 2025
**Package**: `Carom.Telemetry.OpenTelemetry` (NEW)
**Complexity**: Low
**Performance Goal**: Defer to OTel SDK performance

#### Implementation

```csharp
// Package: Carom.Telemetry.OpenTelemetry
// Dependencies: Carom.Extensions + OpenTelemetry.Api

namespace Carom.Telemetry.OpenTelemetry
{
    public static class CaromOpenTelemetryExtensions
    {
        private static readonly ActivitySource ActivitySource =
            new("Carom", typeof(Carom).Assembly.GetName().Version.ToString());

        private static readonly Meter Meter =
            new("Carom", typeof(Carom).Assembly.GetName().Version.ToString());

        private static readonly Counter<long> RetryCounter =
            Meter.CreateCounter<long>("carom.retry.attempts", "attempts", "Number of retry attempts");

        private static readonly Histogram<double> RetryDelayHistogram =
            Meter.CreateHistogram<double>("carom.retry.delay", "milliseconds", "Retry delay duration");

        private static readonly Counter<long> CircuitOpenCounter =
            Meter.CreateCounter<long>("carom.circuit.opened", "events", "Circuit breaker opened events");

        public static TracerProviderBuilder AddCarom(this TracerProviderBuilder builder)
        {
            // Hook into Carom events
            CaromTelemetry.OnShotRetry += OnRetry;
            CaromTelemetry.OnCircuitOpened += OnCircuitOpened;

            return builder.AddSource("Carom");
        }

        public static MeterProviderBuilder AddCarom(this MeterProviderBuilder builder)
        {
            return builder.AddMeter("Carom");
        }

        private static void OnRetry(object? sender, ShotRetryEventArgs e)
        {
            using var activity = ActivitySource.StartActivity("carom.retry");
            activity?.SetTag("retry.attempt", e.Attempt);
            activity?.SetTag("retry.delay_ms", e.Delay.TotalMilliseconds);
            activity?.SetTag("exception.type", e.Exception.GetType().Name);
            activity?.SetTag("exception.message", e.Exception.Message);

            RetryCounter.Add(1, new TagList
            {
                { "exception.type", e.Exception.GetType().Name }
            });

            RetryDelayHistogram.Record(e.Delay.TotalMilliseconds);
        }

        private static void OnCircuitOpened(object? sender, CircuitOpenedEventArgs e)
        {
            using var activity = ActivitySource.StartActivity("carom.circuit.opened");
            activity?.SetTag("service.key", e.ServiceKey);
            activity?.SetTag("failure.count", e.FailureCount);

            CircuitOpenCounter.Add(1, new TagList
            {
                { "service.key", e.ServiceKey }
            });
        }
    }
}
```

**Usage**:

```csharp
// Program.cs
services.AddOpenTelemetry()
    .WithTracing(builder => builder
        .AddCarom()
        .AddOtlpExporter())
    .WithMetrics(builder => builder
        .AddCarom()
        .AddOtlpExporter());
```

---

## Phase 3: Ecosystem Integration (Q3 2025)

### 3.1 ASP.NET Core Integration - v1.5.0

**Target**: July 2025
**Package**: `Carom.AspNetCore` (NEW)
**Complexity**: High

#### Key Features

- Middleware for automatic retry
- DI integration
- HttpClientFactory extensions
- Configuration binding

```csharp
// Startup.cs
public void ConfigureServices(IServiceCollection services)
{
    services.AddCarom(options =>
    {
        options.DefaultRetries = 3;
        options.DefaultBaseDelay = TimeSpan.FromMilliseconds(100);
    });

    services.AddCaromCircuitBreaker("payment-api", options =>
    {
        options.FailureThreshold = 5;
        options.SamplingWindow = 10;
        options.HalfOpenDelay = TimeSpan.FromSeconds(30);
    });

    services.AddHttpClient("external-api")
        .AddCaromHandler(retries: 3)
        .AddCaromCircuitBreaker("external-api");
}

public void Configure(IApplicationBuilder app)
{
    app.UseCarom(); // Middleware
}
```

---

### 3.2 Entity Framework Integration - v1.6.0

**Target**: August 2025
**Package**: `Carom.EntityFramework` (NEW)
**Complexity**: Medium

```csharp
services.AddDbContext<MyDbContext>(options =>
{
    options.UseSqlServer(connectionString)
        .UseCaromExecutionStrategy(retries: 5);
});
```

---

## Phase 4: Hardening & Growth (Q4 2025)

### 4.1 Security Audit

- Third-party penetration testing
- Code signing with strong names
- Supply chain verification
- Security documentation

### 4.2 Performance Benchmarking CI

- Automated benchmarks on every PR
- Fail build if slower than baseline
- Public dashboard comparing vs Polly
- Memory profiling integration

### 4.3 Documentation Overhaul

- API reference (DocFX)
- Migration guides (Polly → Carom)
- Best practices guide
- Video tutorials
- Blog series

---

## Baryo Compliance Checklist

Every feature must pass these gates:

| Gate | Requirement | Verification |
|:-----|:------------|:-------------|
| ✅ Dependencies | Zero for core, minimal for extensions | `.csproj` audit |
| ✅ Allocations | <100 bytes on hot path | BenchmarkDotNet `[MemoryDiagnoser]` |
| ✅ Startup | <1ms overhead | Benchmark vs `new Object()` |
| ✅ Size | <50KB core, <100KB extensions | `dotnet pack` size check |
| ✅ Compatibility | netstandard2.0 | CI matrix (Framework, Core, Mono) |
| ✅ Safety | Jitter default, fail-safe circuit breaker | Code review + tests |
| ✅ Tests | >90% coverage for core | CI gate |
| ✅ Benchmarks | Every feature vs Polly | Required PR check |

---

## Timeline Summary

```
2025 Q1: Foundation
├── v1.1.0: Circuit Breaker (Feb)
├── v1.2.0: Fallback (Mar)
└── v1.3.0: Timeout Enhancement (Mar)

2025 Q2: Observability
├── v1.4.0: Telemetry Events (Apr)
└── v1.4.1: OpenTelemetry Integration (May)

2025 Q3: Ecosystem
├── v1.5.0: ASP.NET Core (Jul)
└── v1.6.0: Entity Framework (Aug)

2025 Q4: Hardening
├── Security Audit (Sep)
├── Performance CI (Oct)
└── Documentation (Nov-Dec)
```

---

## Success Criteria

### Technical

- [ ] All packages <200KB combined
- [ ] Zero-allocation retry on success path
- [ ] Circuit breaker <10ns overhead (closed state)
- [ ] 100% faster cold start than Polly (benchmarked)
- [ ] >90% test coverage

### Adoption

- [ ] 10K NuGet downloads/month
- [ ] 500 GitHub stars
- [ ] 10+ production deployments
- [ ] Featured in Microsoft docs (aspirational)

### Community

- [ ] 10+ contributors
- [ ] <48hr issue response time
- [ ] 50+ Stack Overflow questions
- [ ] 3+ blog posts by community

---

## Next Immediate Steps

1. **Week 1**: Prototype circuit breaker, validate lock-free design
2. **Week 2**: Benchmark circuit breaker vs Polly
3. **Week 3**: Implement fallback extensions
4. **Week 4**: Update README with new features, publish v1.1.0-beta

---

**Document Version**: 1.0
**Last Updated**: 2025-12-27
**Owner**: Baryo.Dev Architecture Team
