# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
