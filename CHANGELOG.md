# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.5.0] - 2026-07-13

### Fixed - Carom Core
- Timeout expiring during a retry backoff delay now throws `TimeoutRejectedException`
  instead of leaking a raw `TaskCanceledException`
- Operations abandoned after a timeout now have their eventual fault observed,
  preventing `UnobservedTaskException` (the operation itself still runs to
  completion; it cannot be cancelled without a token-accepting delegate)
- Decorrelated jitter now honors the 30-second cap when `baseDelay` exceeds 30s
  (previously the min/max range inverted and delays could exceed the cap)

### Fixed - Carom.Extensions
- `RingBuffer`'s high-contention fallback read now takes the same lock as
  writers; it previously used a separate lock object and synchronized with
  nothing, allowing inconsistent circuit-breaker failure counts during
  failure storms
- LRU eviction in `CushionStore`/`ThrottleStore`/`CompartmentStore` no longer
  evicts entries touched after the eviction scan started (evicting a hot entry
  silently reset circuit breakers and refilled token buckets)
- `CompartmentStore` eviction no longer disposes a `CompartmentState` whose
  semaphore still has active holders

### Added - Carom.Extensions
- `Cushion.GetState(serviceKey)`: read-only circuit state lookup for
  monitoring/health checks (returns `null` if the circuit has never been used)

### Fixed - Carom.AspNetCore
- `AddCaromCircuitBreaker` health check now reports the actual circuit state
  (Open → failure status, HalfOpen → Degraded, Closed → Healthy); it was
  previously hardcoded to always return Healthy

### Changed - Carom.Http
- `CaromHttpHandler` no longer retries non-idempotent requests (POST, PATCH)
  by default, preventing duplicate side effects and corrupted re-sent stream
  bodies; opt in via `RetryNonIdempotentRequests = true`

## [1.4.0] - 2025-12-28

### Added - Carom.Extensions
- **Rate Limiting ("Throttle")**: Control operation rate to prevent overwhelming services
  - Token bucket algorithm with lock-free operations
  - Configurable rate limits and burst sizes
  - Automatic token refill over time
  - `ThrottledException` for rate limit rejections
  - <20ns token check overhead

## [1.3.0] - 2025-12-28

### Added - Carom.Extensions
- **Bulkhead ("Compartment")**: Isolate resources to prevent cascading failures
  - `SemaphoreSlim`-based concurrency control
  - Configurable max concurrency and queue depth
  - Automatic slot release (even on exceptions)
  - `CompartmentFullException` for rejection scenarios
  - <50ns overhead when semaphore available

### Added - Carom Core
- **Timeout Enhancement**: Set maximum duration for operations
  - `Bounce.WithTimeout(TimeSpan)` fluent API
  - `timeout` parameter in `ShotAsync` methods
  - Creates linked `CancellationTokenSource` only when timeout specified
  - Zero allocations when timeout not used
  - `TimeoutRejectedException` for timeout scenarios

## [1.2.0] - 2025-12-28

### Added
- **Fallback ("Safety Pocket")**: Return safe defaults on failure
  - Extension methods for inline values and functions
  - Async variants with proper cancellation handling
  - Composable with retry via `ShotWithPocket`
  - Zero allocations on success path
  - Exception-aware fallback functions

## [1.1.0] - 2025-12-28

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

## [1.0.0] - 2025-12-27

### Added
- Initial release of Carom resilience library
- Core retry logic with decorrelated jitter (safe by default)
- `Bounce` configuration struct for fluent API
- HTTP handler integration via `Carom.Http`
- Zero external dependencies
