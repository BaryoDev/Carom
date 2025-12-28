# Carom Implementation Instructions for AI Agent

**CRITICAL**: This document contains strict implementation instructions. You MUST follow every requirement without deviation or shortcuts.

---

## 🎯 Mission Statement

You are implementing the Carom resilience library according to the Baryo.Dev philosophy. Your goal is to create a lean, performant, zero-dependency alternative to Polly that developers will prefer for its simplicity and performance.

**Core Principle**: Every line of code must justify its existence. If in doubt, leave it out.

---

## 🚫 Absolute Prohibitions (NEVER DO THESE)

### 1. Dependencies
- ❌ NEVER add external NuGet dependencies to `Carom` (core)
- ❌ NEVER add external NuGet dependencies to `Carom.Http` (BCL only)
- ❌ NEVER add external NuGet dependencies to `Carom.Extensions`
- ✅ ONLY integration packages (`Carom.Telemetry.OpenTelemetry`, `Carom.AspNetCore`, `Carom.EntityFramework`) may have external dependencies

**Verification**: After every change, run `dotnet list package` and verify zero external dependencies in core packages.

### 2. Allocations
- ❌ NEVER allocate on the hot path (success case) without explicit justification
- ❌ NEVER use `new` for configuration objects - use `struct` instead
- ❌ NEVER create wrapper classes when extension methods suffice
- ✅ ALWAYS measure allocations with `[MemoryDiagnoser]` in benchmarks

**Verification**: Every new feature requires a benchmark proving <100 bytes allocation on hot path.

### 3. Complexity
- ❌ NEVER use reflection (slower, less secure)
- ❌ NEVER use dynamic code generation
- ❌ NEVER create background threads without explicit user control
- ❌ NEVER add features "because Polly has them" - justify each feature independently
- ✅ ALWAYS prefer simple, readable code over clever abstractions

**Verification**: Code review must confirm every class/method has clear purpose.

### 4. Breaking Changes
- ❌ NEVER break the public API in minor/patch releases
- ❌ NEVER remove public methods without deprecation cycle
- ❌ NEVER change struct field order (binary breaking)
- ✅ ALWAYS use semantic versioning strictly

**Verification**: Run API compatibility checks before release.

### 5. Unsafe Defaults
- ❌ NEVER make jitter opt-in (must be default ON)
- ❌ NEVER allow infinite retries without warning
- ❌ NEVER exceed 30s delay cap (protect against misconfiguration)
- ✅ ALWAYS fail-safe (circuit breaker should protect backends)

**Verification**: All defaults must be reviewed for production safety.

---

## ✅ Implementation Checklist

### Phase 1.1: Circuit Breaker ("Cushion") - v1.1.0

**Target Package**: `Carom.Extensions` (NEW)
**Target Date**: Complete within 2 weeks
**Performance Target**: <10ns overhead when circuit closed, zero allocations

#### Step 1.1.1: Create Package Structure ✅ MANDATORY

```bash
# Create new project
cd src
dotnet new classlib -n Carom.Extensions -f netstandard2.0

# Configure project
cd Carom.Extensions
```

**File**: `src/Carom.Extensions/Carom.Extensions.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>

    <!-- Package Metadata -->
    <PackageId>Carom.Extensions</PackageId>
    <Version>1.1.0</Version>
    <Authors>Baryo.Dev</Authors>
    <Company>Baryo.Dev</Company>
    <Description>Advanced resilience patterns for Carom: Circuit Breaker, Fallback, Timeout. Zero external dependencies.</Description>
    <PackageTags>resilience;circuit-breaker;fallback;timeout;polly-alternative;performance</PackageTags>
    <RepositoryUrl>https://github.com/BaryoDev/Carom</RepositoryUrl>
    <License>MPL-2.0</License>
    <PackageReadmeFile>README.md</PackageReadmeFile>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="../Carom/Carom.csproj" />
  </ItemGroup>

  <ItemGroup>
    <None Include="../../README.md" Pack="true" PackagePath="\" />
  </ItemGroup>
</Project>
```

**Verification**:
```bash
dotnet build
dotnet list package  # MUST show zero external dependencies
```

#### Step 1.1.2: Implement CircuitState Enum ✅ MANDATORY

**File**: `src/Carom.Extensions/CircuitState.cs`

```csharp
namespace Carom.Extensions
{
    /// <summary>
    /// Represents the state of a circuit breaker.
    /// </summary>
    public enum CircuitState
    {
        /// <summary>
        /// Circuit is closed (normal operation, requests flowing).
        /// </summary>
        Closed = 0,

        /// <summary>
        /// Circuit is open (rejecting all requests to protect backend).
        /// </summary>
        Open = 1,

        /// <summary>
        /// Circuit is half-open (testing if backend recovered).
        /// </summary>
        HalfOpen = 2
    }
}
```

**Verification**: Compile successfully, no warnings.

#### Step 1.1.3: Implement RingBuffer ✅ MANDATORY

**File**: `src/Carom.Extensions/RingBuffer.cs`

```csharp
using System;
using System.Threading;

namespace Carom.Extensions
{
    /// <summary>
    /// Lock-free ring buffer for tracking recent operation results.
    /// Used by circuit breaker to maintain sliding window of success/failure.
    /// </summary>
    internal class RingBuffer<T>
    {
        private readonly T[] _buffer;
        private int _index;

        public RingBuffer(int capacity)
        {
            if (capacity <= 0)
                throw new ArgumentException("Capacity must be positive", nameof(capacity));

            _buffer = new T[capacity];
        }

        /// <summary>
        /// Adds an item to the buffer (thread-safe).
        /// </summary>
        public void Add(T item)
        {
            var idx = Interlocked.Increment(ref _index) - 1;
            _buffer[idx % _buffer.Length] = item;
        }

        /// <summary>
        /// Gets the current count of items in the buffer (up to capacity).
        /// </summary>
        public int Count => Math.Min(Volatile.Read(ref _index), _buffer.Length);

        /// <summary>
        /// Counts items matching the predicate (thread-safe read).
        /// </summary>
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

        /// <summary>
        /// Resets the buffer (NOT thread-safe - use only when no concurrent access).
        /// </summary>
        public void Reset()
        {
            Interlocked.Exchange(ref _index, 0);
        }
    }
}
```

**Verification**:
- ✅ No locks (must use `Interlocked` only)
- ✅ No allocations on `Add()` or `Count()`
- ✅ Thread-safe for concurrent adds

#### Step 1.1.4: Implement CushionState ✅ MANDATORY

**File**: `src/Carom.Extensions/CushionState.cs`

```csharp
using System;
using System.Threading;

namespace Carom.Extensions
{
    /// <summary>
    /// Internal state for a circuit breaker instance.
    /// Uses lock-free operations for thread safety.
    /// </summary>
    internal class CushionState
    {
        private int _state; // 0=Closed, 1=Open, 2=HalfOpen
        private int _failureCount;
        private int _successCount;
        private long _lastFailureTicks;
        private long _openedAtTicks;
        private readonly RingBuffer<bool> _recentResults;

        public CircuitState State => (CircuitState)Volatile.Read(ref _state);

        public CushionState(int samplingWindow)
        {
            _recentResults = new RingBuffer<bool>(samplingWindow);
            _state = (int)CircuitState.Closed;
        }

        /// <summary>
        /// Records a successful operation.
        /// </summary>
        public void RecordSuccess()
        {
            _recentResults.Add(true);
            Interlocked.Increment(ref _successCount);
        }

        /// <summary>
        /// Records a failed operation.
        /// </summary>
        public void RecordFailure()
        {
            _recentResults.Add(false);
            Interlocked.Increment(ref _failureCount);
            Interlocked.Exchange(ref _lastFailureTicks, DateTime.UtcNow.Ticks);
        }

        /// <summary>
        /// Determines if the circuit should open based on failure threshold.
        /// </summary>
        public bool ShouldOpen(int failureThreshold, int samplingWindow)
        {
            var failures = _recentResults.Count(x => !x);
            var total = _recentResults.Count;

            // Need enough samples AND enough failures
            return total >= samplingWindow && failures >= failureThreshold;
        }

        /// <summary>
        /// Opens the circuit.
        /// </summary>
        public void Open()
        {
            Interlocked.Exchange(ref _state, (int)CircuitState.Open);
            Interlocked.Exchange(ref _openedAtTicks, DateTime.UtcNow.Ticks);
        }

        /// <summary>
        /// Closes the circuit (reset to normal operation).
        /// </summary>
        public void Close()
        {
            Interlocked.Exchange(ref _state, (int)CircuitState.Closed);
            Interlocked.Exchange(ref _failureCount, 0);
            _recentResults.Reset();
        }

        /// <summary>
        /// Checks if enough time has passed to attempt reset (transition to half-open).
        /// </summary>
        public bool CanAttemptReset(TimeSpan halfOpenDelay)
        {
            var openedAt = new DateTime(Volatile.Read(ref _openedAtTicks));
            return DateTime.UtcNow - openedAt >= halfOpenDelay;
        }

        /// <summary>
        /// Transitions circuit to half-open state.
        /// </summary>
        public void TransitionToHalfOpen()
        {
            Interlocked.CompareExchange(ref _state, (int)CircuitState.HalfOpen, (int)CircuitState.Open);
        }
    }
}
```

**Verification**:
- ✅ All state changes use `Interlocked` operations
- ✅ No locks
- ✅ Thread-safe for concurrent access
- ✅ No background threads

#### Step 1.1.5: Implement CushionStore ✅ MANDATORY

**File**: `src/Carom.Extensions/CushionStore.cs`

```csharp
using System.Collections.Concurrent;

namespace Carom.Extensions
{
    /// <summary>
    /// Static store for circuit breaker states, keyed by service name.
    /// </summary>
    internal static class CushionStore
    {
        private static readonly ConcurrentDictionary<string, CushionState> States = new();

        /// <summary>
        /// Gets or creates a circuit breaker state for the given service key.
        /// </summary>
        public static CushionState GetOrCreate(string serviceKey, Cushion config)
        {
            return States.GetOrAdd(serviceKey, _ => new CushionState(config.SamplingWindow));
        }

        /// <summary>
        /// Clears all circuit breaker states (useful for testing).
        /// </summary>
        internal static void Clear()
        {
            States.Clear();
        }
    }
}
```

**Verification**:
- ✅ Thread-safe (using ConcurrentDictionary)
- ✅ No allocations on `GetOrAdd` after first call

#### Step 1.1.6: Implement CircuitOpenException ✅ MANDATORY

**File**: `src/Carom.Extensions/CircuitOpenException.cs`

```csharp
using System;

namespace Carom.Extensions
{
    /// <summary>
    /// Exception thrown when a circuit breaker is open and rejects a request.
    /// </summary>
    public class CircuitOpenException : Exception
    {
        /// <summary>
        /// The service key for the circuit breaker that opened.
        /// </summary>
        public string ServiceKey { get; }

        public CircuitOpenException(string serviceKey)
            : base($"Circuit breaker for '{serviceKey}' is open")
        {
            ServiceKey = serviceKey;
        }

        public CircuitOpenException(string serviceKey, Exception innerException)
            : base($"Circuit breaker for '{serviceKey}' is open", innerException)
        {
            ServiceKey = serviceKey;
        }
    }
}
```

**Verification**: Compile successfully, inherits from Exception correctly.

#### Step 1.1.7: Implement Cushion Struct ✅ MANDATORY

**File**: `src/Carom.Extensions/Cushion.cs`

```csharp
using System;
using System.Threading.Tasks;

namespace Carom.Extensions
{
    /// <summary>
    /// Circuit breaker configuration for protecting failing services.
    /// The "cushion" absorbs repeated impacts before opening the circuit.
    /// </summary>
    public readonly struct Cushion
    {
        /// <summary>
        /// The unique identifier for this service (e.g., "payment-api", "db-primary").
        /// </summary>
        public string ServiceKey { get; }

        /// <summary>
        /// Number of failures required to open the circuit.
        /// </summary>
        public int FailureThreshold { get; }

        /// <summary>
        /// Size of the sliding window (number of recent calls to track).
        /// </summary>
        public int SamplingWindow { get; }

        /// <summary>
        /// Time to wait before transitioning from Open to HalfOpen.
        /// </summary>
        public TimeSpan HalfOpenDelay { get; }

        private Cushion(string serviceKey, int failureThreshold, int samplingWindow, TimeSpan halfOpenDelay)
        {
            if (string.IsNullOrWhiteSpace(serviceKey))
                throw new ArgumentException("Service key cannot be null or empty", nameof(serviceKey));
            if (failureThreshold < 1)
                throw new ArgumentException("Failure threshold must be at least 1", nameof(failureThreshold));
            if (samplingWindow < failureThreshold)
                throw new ArgumentException("Sampling window must be >= failure threshold", nameof(samplingWindow));
            if (halfOpenDelay <= TimeSpan.Zero)
                throw new ArgumentException("Half-open delay must be positive", nameof(halfOpenDelay));

            ServiceKey = serviceKey;
            FailureThreshold = failureThreshold;
            SamplingWindow = samplingWindow;
            HalfOpenDelay = halfOpenDelay;
        }

        /// <summary>
        /// Creates a cushion builder for the specified service.
        /// </summary>
        public static CushionBuilder ForService(string serviceKey) =>
            new CushionBuilder(serviceKey);

        /// <summary>
        /// Executes a synchronous action with circuit breaker protection.
        /// </summary>
        internal T Execute<T>(Func<T> action)
        {
            var state = CushionStore.GetOrCreate(ServiceKey, this);

            // Fast path: circuit closed
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

                    if (state.ShouldOpen(FailureThreshold, SamplingWindow))
                    {
                        state.Open();
                        throw new CircuitOpenException(ServiceKey, ex);
                    }

                    throw;
                }
            }

            // Circuit open: check if we can attempt reset
            if (state.State == CircuitState.Open)
            {
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
                    state.Open(); // Failed, reopen
                    throw new CircuitOpenException(ServiceKey, ex);
                }
            }

            throw new InvalidOperationException($"Invalid circuit state: {state.State}");
        }

        /// <summary>
        /// Executes an asynchronous action with circuit breaker protection.
        /// </summary>
        internal async Task<T> ExecuteAsync<T>(Func<Task<T>> action)
        {
            var state = CushionStore.GetOrCreate(ServiceKey, this);

            // Fast path: circuit closed
            if (state.State == CircuitState.Closed)
            {
                try
                {
                    var result = await action().ConfigureAwait(false);
                    state.RecordSuccess();
                    return result;
                }
                catch (Exception ex)
                {
                    state.RecordFailure();

                    if (state.ShouldOpen(FailureThreshold, SamplingWindow))
                    {
                        state.Open();
                        throw new CircuitOpenException(ServiceKey, ex);
                    }

                    throw;
                }
            }

            // Circuit open: check if we can attempt reset
            if (state.State == CircuitState.Open)
            {
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
                    var result = await action().ConfigureAwait(false);
                    state.Close(); // Success! Close circuit
                    return result;
                }
                catch (Exception ex)
                {
                    state.Open(); // Failed, reopen
                    throw new CircuitOpenException(ServiceKey, ex);
                }
            }

            throw new InvalidOperationException($"Invalid circuit state: {state.State}");
        }
    }

    /// <summary>
    /// Fluent builder for Cushion configuration.
    /// </summary>
    public class CushionBuilder
    {
        private readonly string _serviceKey;
        private int _failureThreshold = 5;
        private int _samplingWindow = 10;
        private TimeSpan _halfOpenDelay = TimeSpan.FromSeconds(30);

        internal CushionBuilder(string serviceKey)
        {
            _serviceKey = serviceKey;
        }

        /// <summary>
        /// Sets the failure threshold and sampling window.
        /// </summary>
        /// <param name="failures">Number of failures to trigger circuit open.</param>
        /// <param name="within">Size of sliding window to track.</param>
        public CushionBuilder OpenAfter(int failures, int within)
        {
            _failureThreshold = failures;
            _samplingWindow = within;
            return this;
        }

        /// <summary>
        /// Sets the half-open delay and builds the Cushion.
        /// </summary>
        public Cushion HalfOpenAfter(TimeSpan delay)
        {
            _halfOpenDelay = delay;
            return new Cushion(_serviceKey, _failureThreshold, _samplingWindow, _halfOpenDelay);
        }
    }
}
```

**Verification**:
- ✅ Struct (not class) - zero allocation for configuration
- ✅ Fluent builder pattern
- ✅ Validates all parameters
- ✅ Thread-safe execution

#### Step 1.1.8: Implement Extension Methods ✅ MANDATORY

**File**: `src/Carom.Extensions/CaromCushionExtensions.cs`

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Carom.Extensions
{
    /// <summary>
    /// Extension methods for integrating Circuit Breaker (Cushion) with Carom retry logic.
    /// </summary>
    public static class CaromCushionExtensions
    {
        /// <summary>
        /// Executes a synchronous shot with circuit breaker protection.
        /// Retry logic wraps circuit breaker logic.
        /// </summary>
        public static T Shot<T>(
            Func<T> action,
            Cushion cushion,
            int retries = 3,
            TimeSpan? baseDelay = null,
            Func<Exception, bool>? shouldBounce = null,
            bool disableJitter = false)
        {
            return Carom.Carom.Shot(
                () => cushion.Execute(action),
                retries,
                baseDelay,
                shouldBounce,
                disableJitter);
        }

        /// <summary>
        /// Executes a synchronous shot with circuit breaker and Bounce configuration.
        /// </summary>
        public static T Shot<T>(Func<T> action, Cushion cushion, Bounce bounce)
        {
            return Carom.Carom.Shot(
                () => cushion.Execute(action),
                bounce);
        }

        /// <summary>
        /// Executes an asynchronous shot with circuit breaker protection.
        /// </summary>
        public static async Task<T> ShotAsync<T>(
            Func<Task<T>> action,
            Cushion cushion,
            int retries = 3,
            TimeSpan? baseDelay = null,
            Func<Exception, bool>? shouldBounce = null,
            bool disableJitter = false,
            CancellationToken ct = default)
        {
            return await Carom.Carom.ShotAsync(
                () => cushion.ExecuteAsync(action),
                retries,
                baseDelay,
                shouldBounce,
                disableJitter,
                ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Executes an asynchronous shot with circuit breaker and Bounce configuration.
        /// </summary>
        public static Task<T> ShotAsync<T>(
            Func<Task<T>> action,
            Cushion cushion,
            Bounce bounce,
            CancellationToken ct = default)
        {
            return Carom.Carom.ShotAsync(
                () => cushion.ExecuteAsync(action),
                bounce,
                ct);
        }
    }
}
```

**Verification**:
- ✅ Extension methods on Carom
- ✅ Supports both direct parameters and Bounce configuration
- ✅ Async methods use `ConfigureAwait(false)`

#### Step 1.1.9: Create Unit Tests ✅ MANDATORY

**File**: `tests/Carom.Extensions.Tests/CushionTests.cs`

Create comprehensive unit tests covering:

1. ✅ Circuit opens after threshold failures
2. ✅ Circuit stays closed with successful calls
3. ✅ Half-open state transitions correctly
4. ✅ Concurrent access is thread-safe
5. ✅ CircuitOpenException thrown when circuit open
6. ✅ Invalid configuration throws ArgumentException
7. ✅ Sampling window works correctly
8. ✅ Reset after half-open success

**Minimum Test Coverage**: 90%

```bash
dotnet test --collect:"XPlat Code Coverage"
# Verify coverage >= 90%
```

#### Step 1.1.10: Create Benchmarks ✅ MANDATORY

**File**: `benchmarks/Carom.Benchmarks/CircuitBreakerBenchmarks.cs`

```csharp
using BenchmarkDotNet.Attributes;
using Carom.Extensions;
using Polly;
using Polly.CircuitBreaker;

namespace Carom.Benchmarks
{
    [MemoryDiagnoser]
    [SimpleJob(warmupCount: 3, iterationCount: 10)]
    public class CircuitBreakerBenchmarks
    {
        private Cushion _caromCushion;
        private ResiliencePipeline _pollyPipeline;
        private int _counter;

        [GlobalSetup]
        public void Setup()
        {
            _caromCushion = Cushion.ForService("benchmark-service")
                .OpenAfter(5, 10)
                .HalfOpenAfter(TimeSpan.FromSeconds(30));

            _pollyPipeline = new ResiliencePipelineBuilder()
                .AddCircuitBreaker(new CircuitBreakerStrategyOptions
                {
                    FailureRatio = 0.5,
                    SamplingDuration = TimeSpan.FromSeconds(10),
                    MinimumThroughput = 10,
                    BreakDuration = TimeSpan.FromSeconds(30)
                })
                .Build();
        }

        [Benchmark(Baseline = true)]
        public int Carom_CircuitClosed_Success()
        {
            return CaromCushionExtensions.Shot(() => ++_counter, _caromCushion);
        }

        [Benchmark]
        public int Polly_CircuitClosed_Success()
        {
            return _pollyPipeline.Execute(() => ++_counter);
        }
    }
}
```

**Performance Targets**:
- ✅ Carom must be <= Polly execution time
- ✅ Carom must allocate <= Polly allocated bytes
- ✅ Circuit closed overhead < 10ns

**Verification**:
```bash
cd benchmarks/Carom.Benchmarks
dotnet run -c Release
# Review results, ensure targets met
```

#### Step 1.1.11: Update Solution File ✅ MANDATORY

```bash
dotnet sln add src/Carom.Extensions/Carom.Extensions.csproj
dotnet sln add tests/Carom.Extensions.Tests/Carom.Extensions.Tests.csproj
```

#### Step 1.1.12: Update README.md ✅ MANDATORY

Add section documenting Circuit Breaker usage:

```markdown
### Circuit Breaker ("Cushion")

Protect failing services with automatic circuit breaking:

\`\`\`csharp
var cushion = Cushion.ForService("payment-api")
    .OpenAfter(failures: 5, within: 10)
    .HalfOpenAfter(TimeSpan.FromSeconds(30));

// With retry + circuit breaker
var result = await CaromCushionExtensions.ShotAsync(
    () => paymentApi.Charge(request),
    cushion);
\`\`\`

**How it works**:
- Circuit **closes** (normal) when service is healthy
- Circuit **opens** after 5 failures in last 10 calls
- Circuit **half-opens** after 30 seconds to test recovery
- If test succeeds, circuit closes; if fails, reopens
```

#### Step 1.1.13: Create CHANGELOG Entry ✅ MANDATORY

**File**: `CHANGELOG.md`

```markdown
## [1.1.0] - 2025-02-XX

### Added
- **Circuit Breaker ("Cushion")**: Passive circuit breaker pattern with zero background threads
  - Lock-free state management via `Interlocked` operations
  - Sliding window failure tracking (call-based, not time-based)
  - Automatic transitions: Closed → Open → HalfOpen → Closed
  - Zero allocations on hot path when circuit closed
  - Performance: <10ns overhead in closed state
- New package: `Carom.Extensions` for advanced resilience patterns
- Comprehensive unit tests with >90% coverage
- Benchmarks proving performance parity with Polly

### Breaking Changes
- None (new package, no API changes to core)
```

#### Step 1.1.14: Final Verification Checklist ✅ MANDATORY

Before committing, verify ALL of these:

```bash
# 1. Build succeeds
dotnet build --configuration Release
# MUST succeed with zero warnings

# 2. Zero external dependencies
dotnet list src/Carom.Extensions package
# MUST show only Carom reference

# 3. Tests pass
dotnet test
# MUST show 100% tests passing

# 4. Test coverage >= 90%
dotnet test --collect:"XPlat Code Coverage"
# MUST show >= 90% coverage

# 5. Benchmarks run
cd benchmarks/Carom.Benchmarks
dotnet run -c Release
# MUST complete without errors, review results

# 6. Package builds
cd src/Carom.Extensions
dotnet pack -c Release
# MUST create .nupkg file

# 7. Package size < 100KB
ls -lh bin/Release/*.nupkg
# MUST be < 100KB

# 8. No compiler warnings
dotnet build /warnaserror
# MUST succeed (warnings treated as errors)
```

**ALL CHECKS MUST PASS. DO NOT SKIP ANY.**

---

### Phase 1.2: Fallback ("Safety Pocket") - v1.2.0

**Target Package**: `Carom.Extensions` (enhance existing)
**Target Date**: Complete within 1 week
**Performance Target**: Zero allocations if fallback not invoked

#### Step 1.2.1: Implement Fallback Extensions ✅ MANDATORY

**File**: `src/Carom.Extensions/CaromFallbackExtensions.cs`

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Carom.Extensions
{
    /// <summary>
    /// Extension methods for fallback (Safety Pocket) pattern.
    /// Returns safe defaults when operations fail.
    /// </summary>
    public static class CaromFallbackExtensions
    {
        #region Synchronous Fallback

        /// <summary>
        /// Executes an action and returns a fallback value if it fails.
        /// </summary>
        public static T Pocket<T>(this Func<T> action, T fallback)
        {
            try
            {
                return action();
            }
            catch
            {
                return fallback;
            }
        }

        /// <summary>
        /// Executes an action and invokes a fallback function if it fails.
        /// </summary>
        public static T Pocket<T>(this Func<T> action, Func<T> fallbackFn)
        {
            try
            {
                return action();
            }
            catch
            {
                return fallbackFn();
            }
        }

        /// <summary>
        /// Executes an action and invokes a fallback function with exception if it fails.
        /// </summary>
        public static T Pocket<T>(this Func<T> action, Func<Exception, T> fallbackFn)
        {
            try
            {
                return action();
            }
            catch (Exception ex)
            {
                return fallbackFn(ex);
            }
        }

        #endregion

        #region Asynchronous Fallback

        /// <summary>
        /// Executes an async action and returns a fallback value if it fails.
        /// </summary>
        public static async Task<T> PocketAsync<T>(
            this Func<Task<T>> action,
            T fallback,
            CancellationToken ct = default)
        {
            try
            {
                return await action().ConfigureAwait(false);
            }
            catch when (!ct.IsCancellationRequested)
            {
                return fallback;
            }
        }

        /// <summary>
        /// Executes an async action and invokes a fallback function if it fails.
        /// </summary>
        public static async Task<T> PocketAsync<T>(
            this Func<Task<T>> action,
            Func<T> fallbackFn,
            CancellationToken ct = default)
        {
            try
            {
                return await action().ConfigureAwait(false);
            }
            catch when (!ct.IsCancellationRequested)
            {
                return fallbackFn();
            }
        }

        /// <summary>
        /// Executes an async action and invokes an async fallback function if it fails.
        /// </summary>
        public static async Task<T> PocketAsync<T>(
            this Func<Task<T>> action,
            Func<Task<T>> fallbackFn,
            CancellationToken ct = default)
        {
            try
            {
                return await action().ConfigureAwait(false);
            }
            catch when (!ct.IsCancellationRequested)
            {
                return await fallbackFn().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Executes an async action and invokes an async fallback with exception if it fails.
        /// </summary>
        public static async Task<T> PocketAsync<T>(
            this Func<Task<T>> action,
            Func<Exception, Task<T>> fallbackFn,
            CancellationToken ct = default)
        {
            try
            {
                return await action().ConfigureAwait(false);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                return await fallbackFn(ex).ConfigureAwait(false);
            }
        }

        #endregion

        #region Composition with Carom.Shot

        /// <summary>
        /// Executes a shot with retry, then falls back to a value if all retries fail.
        /// </summary>
        public static T ShotWithPocket<T>(
            Func<T> action,
            T fallback,
            int retries = 3,
            TimeSpan? baseDelay = null)
        {
            try
            {
                return Carom.Carom.Shot(action, retries, baseDelay);
            }
            catch
            {
                return fallback;
            }
        }

        /// <summary>
        /// Executes an async shot with retry, then falls back to a value if all retries fail.
        /// </summary>
        public static async Task<T> ShotWithPocketAsync<T>(
            Func<Task<T>> action,
            T fallback,
            int retries = 3,
            TimeSpan? baseDelay = null,
            CancellationToken ct = default)
        {
            try
            {
                return await Carom.Carom.ShotAsync(action, retries, baseDelay, ct: ct)
                    .ConfigureAwait(false);
            }
            catch when (!ct.IsCancellationRequested)
            {
                return fallback;
            }
        }

        #endregion
    }
}
```

**Verification**:
- ✅ Extension methods (no wrapper objects)
- ✅ Lazy evaluation (fallback only called on error)
- ✅ Cancellation token propagation
- ✅ ConfigureAwait(false) on all awaits

#### Step 1.2.2: Create Unit Tests ✅ MANDATORY

**File**: `tests/Carom.Extensions.Tests/FallbackTests.cs`

Test coverage:
1. ✅ Success path returns primary value
2. ✅ Failure path returns fallback value
3. ✅ Fallback function only invoked on error
4. ✅ Exception-aware fallback receives exception
5. ✅ Async fallback with cancellation
6. ✅ Composition with retry (ShotWithPocket)

**Minimum Coverage**: 95%

#### Step 1.2.3: Create Benchmarks ✅ MANDATORY

**File**: `benchmarks/Carom.Benchmarks/FallbackBenchmarks.cs`

Benchmark:
- Success path (no fallback invoked) - MUST allocate 0 bytes extra
- Failure path (fallback invoked) - MUST only allocate exception + fallback result

#### Step 1.2.4: Update README ✅ MANDATORY

Add fallback documentation with examples.

#### Step 1.2.5: Update CHANGELOG ✅ MANDATORY

```markdown
## [1.2.0] - 2025-03-XX

### Added
- **Fallback ("Safety Pocket")**: Return safe defaults on failure
  - Extension methods for inline values and functions
  - Async variants with proper cancellation handling
  - Composable with retry via `ShotWithPocket`
  - Zero allocations on success path
```

#### Step 1.2.6: Final Verification ✅ MANDATORY

Run full verification checklist (same as 1.1.14).

---

### Phase 1.3: Timeout Enhancement - v1.3.0

**Target Package**: `Carom` (core enhancement)
**Target Date**: Complete within 1 week
**Performance Target**: Only allocate CancellationTokenSource when timeout specified

#### Step 1.3.1: Enhance Bounce Struct ✅ MANDATORY

**File**: `src/Carom/Bounce.cs`

Add timeout property:

```csharp
public readonly struct Bounce
{
    // ... existing properties

    /// <summary>
    /// Optional timeout for the entire operation (including retries).
    /// </summary>
    public TimeSpan? Timeout { get; }

    // Update constructor
    private Bounce(
        int retries,
        TimeSpan baseDelay,
        TimeSpan? timeout,
        bool disableJitter,
        Func<Exception, bool>? shouldBounce)
    {
        Retries = retries;
        BaseDelay = baseDelay;
        Timeout = timeout;
        DisableJitter = disableJitter;
        ShouldBounce = shouldBounce;
    }

    /// <summary>
    /// Sets the timeout for the operation.
    /// </summary>
    public Bounce WithTimeout(TimeSpan timeout) =>
        new Bounce(Retries, BaseDelay, timeout, DisableJitter, ShouldBounce);
}
```

#### Step 1.3.2: Enhance Carom.ShotAsync ✅ MANDATORY

**File**: `src/Carom/Carom.cs`

Add timeout parameter and logic:

```csharp
public static async Task<T> ShotAsync<T>(
    Func<Task<T>> action,
    int retries = 3,
    TimeSpan? baseDelay = null,
    TimeSpan? timeout = null, // NEW
    Func<Exception, bool>? shouldBounce = null,
    bool disableJitter = false,
    CancellationToken ct = default)
{
    if (action == null) throw new ArgumentNullException(nameof(action));

    // Create linked token source ONLY if timeout specified
    using var timeoutCts = timeout.HasValue
        ? CancellationTokenSource.CreateLinkedTokenSource(ct)
        : null;

    if (timeoutCts != null)
    {
        timeoutCts.CancelAfter(timeout!.Value);
        ct = timeoutCts.Token;
    }

    // ... existing retry logic using ct
}

// Update Bounce overload
public static Task<T> ShotAsync<T>(
    Func<Task<T>> action,
    Bounce bounce,
    CancellationToken ct = default) =>
    ShotAsync(
        action,
        bounce.Retries,
        bounce.BaseDelay,
        bounce.Timeout, // NEW
        bounce.ShouldBounce,
        bounce.DisableJitter,
        ct);
```

#### Step 1.3.3: Add TimeoutRejectedException ✅ MANDATORY

**File**: `src/Carom/TimeoutRejectedException.cs`

```csharp
using System;

namespace Carom
{
    /// <summary>
    /// Exception thrown when an operation exceeds its timeout.
    /// </summary>
    public class TimeoutRejectedException : OperationCanceledException
    {
        public TimeSpan Timeout { get; }

        public TimeoutRejectedException(TimeSpan timeout)
            : base($"Operation timed out after {timeout.TotalMilliseconds}ms")
        {
            Timeout = timeout;
        }

        public TimeoutRejectedException(TimeSpan timeout, Exception innerException)
            : base($"Operation timed out after {timeout.TotalMilliseconds}ms", innerException)
        {
            Timeout = timeout;
        }
    }
}
```

#### Step 1.3.4: Create Unit Tests ✅ MANDATORY

Test coverage:
1. ✅ Timeout triggers cancellation
2. ✅ Timeout exception thrown
3. ✅ No timeout allocated when not specified
4. ✅ Bounce.WithTimeout() works
5. ✅ Retry respects timeout across all attempts

**Minimum Coverage**: 90%

#### Step 1.3.5: Create Benchmarks ✅ MANDATORY

Verify:
- Without timeout: 0 extra allocations
- With timeout: Only CancellationTokenSource allocated

#### Step 1.3.6: Update README & CHANGELOG ✅ MANDATORY

#### Step 1.3.7: Final Verification ✅ MANDATORY

Run full verification checklist.

---

## 🎯 Completion Criteria

### For Each Feature

Before marking ANY feature as "complete", verify:

1. ✅ **Code compiles** with zero warnings
2. ✅ **All tests pass** (100% pass rate)
3. ✅ **Test coverage >= 90%** for new code
4. ✅ **Benchmarks run** successfully
5. ✅ **Performance targets met** (check benchmark results)
6. ✅ **Zero external dependencies** in core packages (run `dotnet list package`)
7. ✅ **Package builds** successfully (run `dotnet pack`)
8. ✅ **Package size < target** (<50KB core, <100KB extensions)
9. ✅ **README updated** with usage examples
10. ✅ **CHANGELOG updated** with user-facing changes
11. ✅ **XML docs** on all public APIs
12. ✅ **Code review** by human (mark for review)

### DO NOT Mark Complete Unless ALL Criteria Met

**If even ONE criterion fails, the feature is NOT complete. Fix it.**

---

## 📝 Progress Tracking

Use TodoWrite tool to track progress. Update todos IMMEDIATELY after completing each step.

Example:
```json
{
  "content": "Implement CircuitState enum",
  "activeForm": "Implementing CircuitState enum",
  "status": "in_progress"
}
```

After completion:
```json
{
  "content": "Implement CircuitState enum",
  "activeForm": "Implementing CircuitState enum",
  "status": "completed"
}
```

**IMPORTANT**: Mark todo as completed ONLY when all verification steps pass.

---

## 🚨 Anti-Laziness Rules

### Rule 1: No Shortcuts
- ❌ DO NOT skip unit tests ("I'll add them later")
- ❌ DO NOT skip benchmarks ("I'll measure later")
- ❌ DO NOT skip documentation ("It's self-explanatory")
- ❌ DO NOT skip verification steps ("It probably works")

**Every shortcut creates technical debt. Zero shortcuts allowed.**

### Rule 2: No Assumptions
- ❌ DO NOT assume code works without running it
- ❌ DO NOT assume tests pass without running them
- ❌ DO NOT assume performance without measuring it
- ❌ DO NOT assume coverage without checking report

**If you didn't verify it, it doesn't work.**

### Rule 3: No Deviations
- ❌ DO NOT change API without approval
- ❌ DO NOT add features not in spec
- ❌ DO NOT remove features from spec
- ❌ DO NOT "improve" things that aren't broken

**Stick to the plan. If the plan is wrong, ask first.**

### Rule 4: No Premature Optimization
- ❌ DO NOT add caching "just in case"
- ❌ DO NOT use unsafe code "for speed" without proof
- ❌ DO NOT add complexity "for future flexibility"

**Optimize only when benchmarks prove it's needed.**

### Rule 5: No Magic Numbers
- ❌ DO NOT use hardcoded values without constants
- ❌ DO NOT use unexplained thresholds
- ❌ DO NOT use arbitrary timeouts

**Every number must have a name and reason.**

---

## 📊 Quality Gates

### Gate 1: Code Quality
- ✅ Zero compiler warnings
- ✅ Zero nullable reference warnings
- ✅ XML docs on all public APIs
- ✅ No commented-out code
- ✅ No TODO comments in production code

### Gate 2: Testing
- ✅ All tests pass
- ✅ Coverage >= 90%
- ✅ No flaky tests
- ✅ Tests run in < 5 seconds

### Gate 3: Performance
- ✅ Benchmarks complete successfully
- ✅ No performance regressions vs baseline
- ✅ Allocation targets met
- ✅ Startup overhead < 1ms

### Gate 4: Documentation
- ✅ README updated
- ✅ CHANGELOG updated
- ✅ Code examples work (copy-paste tested)
- ✅ No broken links

### Gate 5: Package
- ✅ Package builds
- ✅ Size under target
- ✅ No external dependencies (core)
- ✅ Strong-name signed (future)

**ALL GATES MUST PASS. NO EXCEPTIONS.**

---

## 🎓 Learning Resources

If you encounter issues:

1. **Interlocked Operations**: https://docs.microsoft.com/en-us/dotnet/api/system.threading.interlocked
2. **Volatile.Read/Write**: https://docs.microsoft.com/en-us/dotnet/api/system.threading.volatile
3. **ConfigureAwait**: https://devblogs.microsoft.com/dotnet/configureawait-faq/
4. **BenchmarkDotNet**: https://benchmarkdotnet.org/articles/overview.html
5. **Struct vs Class**: https://docs.microsoft.com/en-us/dotnet/standard/design-guidelines/choosing-between-class-and-struct

---

## 🆘 When Stuck

If you're stuck on ANY step:

1. **STOP** - Don't guess or improvise
2. **READ** - Review this document and related specs
3. **ASK** - Request clarification from user
4. **WAIT** - Get answer before proceeding

**Guessing creates bugs. Asking creates clarity.**

---

## ✅ Final Reminder

**Your mission**: Implement Carom according to Baryo.Dev philosophy with zero compromises.

**Success means**:
- ✅ Every feature works correctly (tests prove it)
- ✅ Every feature performs well (benchmarks prove it)
- ✅ Every feature is documented (README proves it)
- ✅ Every feature is maintainable (future you will thank you)

**Failure means**:
- ❌ Skipped tests
- ❌ Skipped benchmarks
- ❌ Skipped documentation
- ❌ Skipped verification

**Your job is to succeed. No shortcuts. No excuses. No compromises.**

---

**Document Version**: 1.0
**Last Updated**: 2025-12-27
**Next Review**: After Phase 1.1 completion
