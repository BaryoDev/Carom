# Carom Architecture Strategy: A Lean Alternative to Polly

**Status**: Strategic Review
**Author**: Senior Architecture Team
**Date**: 2025-12-27
**Philosophy**: Baryo.Dev - Zero Dependencies, Maximum Performance, Safe by Default

---

## Executive Summary

Carom is positioned to become the **performance-first, serverless-optimized resilience library** for .NET, directly competing with Polly v8 by offering:

1. **Zero cold-start overhead** (critical for serverless/FaaS)
2. **Zero external dependencies** (eliminates supply chain security risks)
3. **Safe-by-default jitter** (prevents thundering herd without configuration)
4. **Sub-50KB footprint** (vs Polly's larger dependency graph)
5. **Allocation-aware design** (struct-based, GC-friendly)

**Market Position**: Carom is the precision tool for teams who need Polly's capabilities but cannot afford its weight, startup cost, or dependency complexity.

---

## Current State Analysis

### What Carom Has (v1.0.0)

✅ **Core Retry Engine**
- Synchronous and async retry patterns
- Decorrelated jitter (AWS-recommended) as default
- Fluent configuration via `Bounce` struct
- Thread-safe random jitter generation
- Zero allocations on hot path

✅ **HTTP Integration**
- `CaromHttpHandler` for transient HTTP failures
- Smart detection of 503, 504, 502, 429, 408 status codes
- Drop-in replacement for standard HttpClient handlers

✅ **Performance Infrastructure**
- BenchmarkDotNet comparison suite vs Polly
- Proof of zero startup overhead
- Memory diagnostics showing allocation advantages

### What Polly Has (v8.6.5)

Polly offers a **comprehensive resilience pipeline** with:

1. **Retry** - Full exponential backoff, jitter options _(Carom has this)_
2. **Circuit Breaker** - Auto-opens circuit after threshold failures _(Carom missing)_
3. **Timeout** - Enforces max execution time _(Carom missing)_
4. **Bulkhead** - Limits concurrent executions _(Carom missing)_
5. **Rate Limiter** - Token bucket / sliding window _(Carom missing)_
6. **Hedging** - Parallel redundant requests _(Carom missing)_
7. **Fallback** - Returns alternative values on failure _(Carom missing)_
8. **Chaos Engineering** - Simmy integration for fault injection _(Carom missing)_
9. **Telemetry** - Built-in OpenTelemetry support _(Carom missing)_

### Gap Analysis

| Feature Category | Polly v8 | Carom v1.0 | Priority for Carom |
|:----------------|:---------|:-----------|:-------------------|
| Retry | ✅ Full | ✅ Full | ✅ Complete |
| Circuit Breaker | ✅ Full | ❌ None | 🔴 **CRITICAL** |
| Timeout | ✅ Full | ❌ None | 🟡 **HIGH** |
| Fallback | ✅ Full | ❌ None | 🟡 **HIGH** |
| Bulkhead | ✅ Full | ❌ None | 🟢 MEDIUM |
| Rate Limiter | ✅ Full | ❌ None | 🟢 MEDIUM |
| Hedging | ✅ New | ❌ None | ⚪ LOW |
| Telemetry | ✅ Full | ❌ None | 🔴 **CRITICAL** |
| Chaos Engineering | ✅ Simmy | ❌ None | ⚪ LOW |

---

## Strategic Positioning: "The 80/20 Resilience Library"

### Core Thesis

**Carom targets the 80% of use cases that need resilience but are penalized by Polly's comprehensiveness.**

We will NOT compete on feature count. We compete on:
- **Performance** (zero startup, minimal allocations)
- **Simplicity** (physics-based mental model)
- **Safety** (secure defaults, zero dependencies)
- **Compatibility** (netstandard2.0, works everywhere)

### Target Audience

1. **NuGet Library Authors** - Cannot impose Polly's dependency tree on consumers
2. **Serverless/FaaS Developers** - Cold start time is money
3. **High-Performance Services** - GC pauses are unacceptable
4. **Security-Conscious Teams** - Minimal supply chain attack surface
5. **Mobile/Unity Developers** - Package size matters

---

## Architecture Roadmap: Lean Resilience Patterns

### Phase 1: Core Resilience (v1.1 - v1.3)

#### 1.1 The "Cushion" (Circuit Breaker) 🔴 CRITICAL

**Philosophy**: Passive, allocation-free circuit breaker that protects backends.

**Design Principles**:
- **No background threads** (unlike Polly's time-based reset)
- **Struct-based state** (atomic operations, no heap allocations)
- **Sliding window counters** (last N calls, not time-based sampling)
- **Safe defaults** (fail-fast after 50% failure rate over 10 calls)

**API Design** (maintains Baryo philosophy):

```csharp
// Minimal API - just wrap with circuit breaker
var cushion = Cushion.ForService("external-api")
    .OpenAfter(failures: 5, within: 10)
    .HalfOpenAfter(TimeSpan.FromSeconds(30));

var result = Carom.Shot(() => externalApi.Call(), cushion);
```

**Implementation Strategy**:
- Use `Interlocked` operations for state transitions
- Store state in a static `ConcurrentDictionary<string, CushionState>` (one per service)
- `CushionState` is a `struct` with atomic counters
- No timers - state transitions happen on-demand during calls

**Advantages over Polly**:
- Zero background threads (Polly uses timers)
- Zero allocations on hot path
- Simpler mental model (call-based, not time-based)

---

#### 1.2 The "Safety Pocket" (Fallback) 🟡 HIGH

**Philosophy**: Return a safe default without ceremony.

**API Design**:

```csharp
// Inline fallback value
var data = Carom.Shot(() => cache.Get(key))
    .Pocket(fallback: defaultValue);

// Fallback function (only executed on failure)
var result = Carom.ShotAsync(() => api.GetUserAsync(id))
    .PocketAsync(async () => await backupApi.GetUserAsync(id));
```

**Implementation Strategy**:
- Extension methods on `T` and `Task<T>`
- No wrapper objects - operates directly on return values
- Lazy evaluation of fallback functions

**Advantages over Polly**:
- No policy builder ceremony
- Zero allocations if fallback not invoked
- Composable with retry naturally

---

#### 1.3 The "Shot Clock" (Timeout) 🟡 HIGH

**Philosophy**: Every shot has a time limit.

**API Design**:

```csharp
// Simple timeout
var result = await Carom.ShotAsync(
    () => api.SlowOperation(),
    timeout: TimeSpan.FromSeconds(5)
);

// Bounce configuration with timeout
var bounce = Bounce.Times(3)
    .WithDelay(TimeSpan.FromMilliseconds(100))
    .WithTimeout(TimeSpan.FromSeconds(5));

await Carom.ShotAsync(() => api.Call(), bounce);
```

**Implementation Strategy**:
- Use `CancellationTokenSource.CancelAfter(timeout)`
- Integrate with existing `CancellationToken` parameter
- Throw `TimeoutRejectedException` on timeout
- Resource cleanup via `using` patterns

**Advantages over Polly**:
- Simpler - just a CancellationToken wrapper
- No separate timeout policy objects
- Natural integration with async/await

---

### Phase 2: Observability & Hardening (v1.4 - v1.6)

#### 1.4 The "Scoreboard" (Telemetry) 🔴 CRITICAL

**Philosophy**: Zero-dependency telemetry via callbacks, not framework coupling.

**Design Principles**:
- **No OpenTelemetry dependency** (let users wire it themselves)
- **Event-based hooks** (pre/post shot, retry, failure)
- **Minimal allocations** (reusable event arg structs)

**API Design**:

```csharp
// Global telemetry hook
Carom.OnShot += (sender, e) => {
    Console.WriteLine($"Shot: {e.Attempt}/{e.MaxRetries}, Delay: {e.Delay}");
};

// Or per-shot telemetry
var result = Carom.Shot(() => api.Call(),
    onRetry: (attempt, delay, ex) => logger.LogWarning(ex, "Retry {Attempt}", attempt)
);
```

**Events to Expose**:
- `OnShotStart` - Execution begins
- `OnShotRetry` - Retry triggered (with exception, delay, attempt #)
- `OnShotSuccess` - Final success
- `OnShotFailure` - Final failure (after all retries)
- `OnCircuitOpen` - Circuit breaker opens
- `OnCircuitHalfOpen` - Circuit breaker testing
- `OnCircuitClose` - Circuit breaker closes

**Implementation Strategy**:
- Use .NET events (zero allocations if no subscribers)
- Provide `struct` event args (stack-allocated)
- Document OpenTelemetry integration pattern in docs (but don't take dependency)

**Advantages over Polly**:
- No forced OTel dependency
- Users control telemetry sink
- Lighter weight, more flexible

---

#### 1.5 Security Hardening 🔴 CRITICAL

**Philosophy**: Zero trust, minimal attack surface.

**Security Initiatives**:

1. **Dependency Scanning**
   - GitHub Dependabot (already zero deps, but verify transitive)
   - Weekly NuGet package vulnerability scans
   - Automated security advisories

2. **Code Signing**
   - Sign all NuGet packages with strong name
   - Publish PGP signatures for releases
   - Document supply chain verification

3. **Attack Surface Reduction**
   - No reflection (faster, safer)
   - No dynamic code generation
   - All types are sealed where possible
   - Internal visibility by default

4. **DoS Protection**
   - Max retry cap (prevent infinite loops)
   - Max delay cap (already 30s, document this)
   - Overflow protection on jitter calculation

5. **Documentation**
   - Security best practices guide
   - Threat model documentation
   - Responsible disclosure policy

**Advantages over Polly**:
- Polly has 0 direct dependencies but is larger attack surface
- Smaller codebase = faster security audits
- Zero deps = zero supply chain risk

---

#### 1.6 Performance Guardrails 🟡 HIGH

**Philosophy**: Automated performance regression prevention.

**Infrastructure**:
- BenchmarkDotNet in CI (already exists, expand)
- Automated allocation tracking
- Startup time regression tests
- Package size tracking

**Benchmarks to Add**:
- Circuit breaker state transitions (vs Polly)
- Timeout overhead (vs manual CancellationToken)
- Fallback hot path (vs try/catch)
- Multi-strategy composition (retry + circuit + timeout)

**CI Gates**:
- Fail build if package size > 50KB
- Fail build if startup overhead > 1ms
- Fail build if hot path allocates > 100 bytes

---

### Phase 3: Advanced Patterns (v2.0+) - OPTIONAL

**Only implement if Baryo philosophy can be maintained.**

#### 2.1 The "Rack" (Bulkhead) 🟢 MEDIUM

**Concept**: Limit concurrent executions to protect resources.

**Baryo-Compliant Design**:
- `SemaphoreSlim` wrapper (no custom thread pools)
- Static service-keyed semaphores
- Fail-fast when capacity reached (no queueing by default)

**API Design**:

```csharp
var rack = Rack.ForService("db-pool").Limit(10);

await Carom.ShotAsync(() => db.Query(), rack);
// Throws IsolationRejectedException if 10 concurrent calls
```

**Defer if**: Requires significant state management complexity.

---

#### 2.2 The "Limiter" (Rate Limiting) 🟢 MEDIUM

**Concept**: Token bucket rate limiting.

**Baryo Concerns**:
- Requires background timers (refill tokens)
- Or per-call timestamp tracking (allocations)
- Complex state management

**Decision**: **Defer to v2.0+** or recommend external rate limiting (API Gateway, middleware).

**Alternative**: Provide integration guide for existing .NET 7+ `RateLimiter` APIs.

---

#### 2.3 The "Bank Shot" (Hedging) ⚪ LOW

**Concept**: Send parallel redundant requests, use first success.

**Baryo Concerns**:
- High resource cost (intentional waste)
- Limited use cases (tail latency optimization)
- Not aligned with "lean" philosophy

**Decision**: **Not in scope**. Hedging is valuable for large-scale distributed systems (Google, AWS) but overkill for Carom's target audience.

---

## Implementation Priorities

### Immediate (Q1 2025)

1. ✅ **Cushion (Circuit Breaker)** - [Carom.cs](src/Carom/Carom.cs#L1), [Cushion.cs](src/Carom/Cushion.cs) (new file)
2. ✅ **Safety Pocket (Fallback)** - [Carom.cs](src/Carom/Carom.cs#L1) extensions
3. ✅ **Shot Clock (Timeout)** - [Carom.cs](src/Carom/Carom.cs#L140) enhancement
4. ✅ **Scoreboard (Telemetry)** - [Carom.cs](src/Carom/Carom.cs#L1) events

### Near-Term (Q2 2025)

5. ✅ **Security Audit** - Third-party code review
6. ✅ **Performance CI** - Automated regression gates
7. ✅ **Documentation** - API reference, migration guides

### Long-Term (Q3-Q4 2025)

8. 🤔 **Rack (Bulkhead)** - If demand exists
9. 🤔 **Limiter (Rate Limiting)** - If can maintain Baryo principles
10. ❌ **Hedging** - Out of scope

---

## Competitive Positioning Matrix

### When to Choose Carom

| Scenario | Carom Advantage |
|:---------|:----------------|
| **AWS Lambda / Azure Functions** | Zero cold-start penalty vs Polly's pipeline construction |
| **NuGet Package Development** | Zero dependencies vs Polly's transitive deps |
| **Mobile / Unity Apps** | <50KB vs Polly's larger footprint |
| **High-Throughput APIs** | Allocation-free hot path vs Polly's object graphs |
| **Security-Critical Systems** | Minimal attack surface, no supply chain risk |
| **Simple Retry Needs** | 3 lines of code vs Polly's builder ceremony |

### When to Choose Polly

| Scenario | Polly Advantage |
|:---------|:----------------|
| **Complex Multi-Strategy Pipelines** | Built-in pipeline composition |
| **Rate Limiting Required** | Native `RateLimiter` integration |
| **Chaos Engineering** | Simmy fault injection |
| **Enterprise Governance** | Microsoft official support |
| **Hedging / Advanced Patterns** | More comprehensive feature set |

### Coexistence Strategy

**Carom is NOT trying to kill Polly.** We're serving different markets:

- **Polly**: Enterprise-grade, comprehensive resilience framework
- **Carom**: Precision tool for performance-critical, lean applications

**Polly is the Swiss Army knife. Carom is the surgeon's scalpel.**

---

## Technical Architecture Principles

### The Baryo Constraints

Every feature MUST satisfy ALL of these:

1. ✅ **Zero external NuGet dependencies** (only BCL)
2. ✅ **Allocation-aware** (hot path must be zero-alloc or warn in docs)
3. ✅ **netstandard2.0 target** (maximum compatibility)
4. ✅ **Static-first API** (avoid object construction overhead)
5. ✅ **Safe by default** (jitter mandatory, circuit breakers fail-safe)
6. ✅ **Physics-based naming** (Shot, Bounce, Cushion, Pocket, not Policy/Pipeline)

### Code Quality Standards

- **Test Coverage**: >90% for core retry engine, >80% for integrations
- **Benchmark Coverage**: Every new feature requires benchmark vs Polly
- **Documentation**: XML docs on all public APIs, usage examples in README
- **Versioning**: Semantic versioning, no breaking changes in minor releases

### Anti-Patterns to Avoid

❌ **Do NOT**:
- Add NuGet dependencies (not even for testing - use manual mocks)
- Use reflection (startup cost, security risk)
- Create background threads without user control
- Copy Polly's API (we have our own identity)
- Implement features "just because Polly has them"

✅ **Do**:
- Question every allocation
- Benchmark every feature
- Document trade-offs honestly
- Provide migration guides from Polly
- Maintain the "physics" metaphor

---

## Marketing & Communication Strategy

### Key Messages

1. **"Zero Friction Resilience"**
   - No startup cost, no dependencies, no complexity

2. **"Built for Serverless, Works Everywhere"**
   - Lambda, Azure Functions, Unity, mobile

3. **"Safe by Default, Fast by Design"**
   - Jitter mandatory, circuit breakers fail-safe, allocation-aware

4. **"The 80/20 Resilience Library"**
   - 80% of Polly's value, 20% of the weight

### Documentation Deliverables

- ✅ **Migration Guide**: Polly → Carom (code comparisons)
- ✅ **Performance Comparison**: Benchmarks with analysis
- ✅ **Security Whitepaper**: Threat model, zero-dependency rationale
- ✅ **Best Practices**: When to use each resilience pattern
- ✅ **Integration Examples**: HttpClient, gRPC, Entity Framework, Dapper

### Community Building

- Blog series: "Building a Lean Resilience Library"
- Conference talks: ".NET Performance: The Cost of Resilience"
- Open benchmarks: Public dashboard comparing Carom vs Polly
- Transparent roadmap: GitHub Projects with community voting

---

## Success Metrics

### Technical Metrics

- Package size: <50KB (vs Polly's base package)
- Startup overhead: <1ms (vs Polly's pipeline construction)
- Hot path allocations: 0 bytes (for retry without failures)
- Test coverage: >90%

### Adoption Metrics

- NuGet downloads: 10K/month by Q2 2025
- GitHub stars: 500 by Q2 2025
- Production usage: 50 companies by end of 2025
- NuGet package references: Featured in 100+ libraries

### Community Metrics

- Contributors: 10+ by end of 2025
- Issues/PRs: <48hr response time
- Documentation: >95% positive feedback
- Stack Overflow: 50+ Carom-tagged questions

---

## Risk Analysis

### Technical Risks

| Risk | Likelihood | Impact | Mitigation |
|:-----|:-----------|:-------|:-----------|
| Circuit breaker complexity violates Baryo | Medium | High | Prototype first, validate allocation profile |
| Telemetry requires dependencies | Low | Critical | Use .NET events, no OTel coupling |
| Performance regression | Medium | High | Automated CI benchmarks |
| Security vulnerability | Low | Critical | Third-party audit, bug bounty |

### Market Risks

| Risk | Likelihood | Impact | Mitigation |
|:-----|:-----------|:-------|:-----------|
| Polly adds zero-dependency mode | Low | High | Focus on simplicity, not just deps |
| Microsoft endorses Polly only | Medium | Medium | Position as complement, not competitor |
| Lack of adoption | Medium | Critical | Aggressive marketing, clear use cases |
| Feature creep pressure | High | Medium | Strict Baryo adherence, say no often |

---

## Conclusion

Carom has a **clear market position**: the lean, fast, secure resilience library for performance-critical .NET applications.

By implementing Circuit Breaker, Fallback, Timeout, and Telemetry while maintaining the Baryo philosophy, we can capture the 80% of use cases where Polly is overkill.

**Success requires discipline**: We must resist feature creep and stay true to zero dependencies, zero allocations, and zero ceremony.

**Next Steps**:
1. Validate circuit breaker design (prototype, benchmark)
2. Implement fallback extensions (low risk, high value)
3. Enhance timeout support (already 90% there)
4. Add telemetry hooks (events, not dependencies)
5. Security audit and hardening
6. Marketing push: blog series, benchmarks, documentation

**The goal is not to beat Polly. The goal is to be the obvious choice when Polly is too heavy.**

---

## References

- [Polly GitHub](https://github.com/App-vNext/Polly)
- [Polly Documentation](https://www.pollydocs.org/)
- [Building Resilient Cloud Services with .NET 8](https://devblogs.microsoft.com/dotnet/building-resilient-cloud-services-with-dotnet-8/)
- [AWS Exponential Backoff and Jitter](https://aws.amazon.com/blogs/architecture/exponential-backoff-and-jitter/)
- Baryo.Dev Philosophy (internal)

---

**Document Version**: 1.0
**Last Updated**: 2025-12-27
**Next Review**: Q1 2025 (post-Phase 1 implementation)
