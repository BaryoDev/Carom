# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [2.1.0] - Unreleased

### Added - Carom

- `CaromHooks` gives the library four signals a consumer can subscribe to: retry,
  circuit opened, bulkhead rejected, rate limit rejected. Each carries a
  `readonly struct` payload so a later field is additive and nothing boxes. The
  delegate is read into a local and null-checked before anything is computed, so
  an unsubscribed hot path costs nothing: gated at 0 bytes over 10,000 retrying
  calls, alongside the existing 0 bytes over 10,000 successful ones

### Added - Carom.Telemetry.OpenTelemetry

- `CaromTelemetry.Subscribe()` and `Unsubscribe()` wire the meters to those
  hooks. Until now the package emitted nothing at all: `CaromTelemetry` appeared
  exactly once in the source tree, its own declaration, while its instruments sat
  unreferenced (#39). Subscribing twice records once. Instrument and meter names
  are unchanged.
- The core keeps zero package dependencies. The seam lives in `Carom` and the
  telemetry package subscribes to it, rather than the core calling the package,
  because the dependency only points one way. `DiagnosticSource` would have been
  the conventional answer and is a NuGet reference, which the core does not take

### Fixed - packaging

- `publish.yml` packed six projects by name and omitted
  `Carom.DependencyInjection`, so it was built, tested and never shipped while CI
  asserted a seven-package set. It packs the solution now and repeats CI's
  package-set assertion on the path that actually publishes (#33)

## [2.0.0] - Unreleased

Twelve defects found by an adversarial audit of the retry, timeout, circuit
breaker and bulkhead paths, each reproduced against the built assemblies and run
head to head against Polly v8, the Polly v7 API and System.Threading.RateLimiting.
The published suite was green throughout, so every one of these sat in a blind
spot the tests did not reach.

### Breaking - Carom

- A negative `WithDelay` now throws `ArgumentOutOfRangeException` at
  configuration time. It used to reach `Thread.Sleep`/`Task.Delay` from inside
  the catch block, so the timer's exception replaced the caller's real one and
  no retry happened at all
- A non-positive `WithTimeout` now throws `ArgumentOutOfRangeException`.
  `TimeSpan.Zero` used to race the operation and was enforced 22 times in 200
  identical runs
- `OperationCanceledException` is never retried, and a `shouldBounce` predicate
  can only narrow what gets retried, never widen it to include cancellation.
  A cancelled operation used to be retried four times through every extension
  entry point, because they all supply a predicate that returned true for it
- `TimeoutRejectedException` is exempt from that rule and stays retryable, so an
  outer retry around an inner timeout still works. A Shot's own timeout still
  ends that call rather than feeding its own loop
- Passing a `Bounce` that carries a `Timeout` to a synchronous `Shot` overload
  now throws `InvalidOperationException` naming the fix, instead of silently
  dropping the timeout. The sync path has no cancellation mechanism and never
  applied it. Rejecting a nonsense timeout at configuration time while quietly
  ignoring a valid one was the inconsistency; enforcing it on the sync path was
  considered and rejected, because that means running the work on a task and
  waiting on it, the exact pattern that stopped firing under load in the DI
  package (#36)

### Breaking - Carom.Extensions

- `CushionBuilder.OpenAfter(int failures, int within)` is now
  `OpenAfter(int failures, int trackingLast)`. The old name read as a duration
  and was a call count
- The circuit opens as soon as `failures` failures are recorded. It used to
  require the window to be full first, so `OpenAfter(3, within: 5)` needed five
  failures, not three, and a hard-down low-traffic dependency kept being called
- Failures older than the sampling duration stop counting, one minute by
  default. The window had no time dimension at all, so failures from a resolved
  incident sat in the buffer until enough traffic flushed them
- Retries now run inside the breaker, so one logical call records one outcome.
  Retry used to wrap the breaker, so a single call with the default retry count
  wrote four entries and could open a circuit sized for five separate failures
- `CushionStore` rejects a registration that disagrees with the entry already
  holding the key on `FailureThreshold`, `SamplingWindow`, `HalfOpenDelay` or
  `SamplingDuration`. It used to compare `SamplingWindow` alone, so a lenient
  call site silently repurposed a circuit a strict one had registered

### Added - packaging

- All seven packages ship a NuGet icon. Every one showed the grey placeholder in
  the gallery before (#37). The mark is a white cue ball and a gold bounced path
  on billiards green, generated from `assets/icon-spec.json` and checked against
  the house style; `assets/logo.svg` is the source, `assets/logo.png` the 128px
  render that packs. Verified by packing the core and inspecting the nupkg: the
  icon element is present, the PNG is 128x128, and the dependency group is still
  empty so the zero-dependency gate is unaffected

### Added - Carom

- `Bounce.WithMaxDelay(TimeSpan)` and the same on `Bounce<T>` make the retry
  delay ceiling configurable. It was a hardcoded 30 seconds in `JitterStrategy`,
  documented nowhere, so a longer backoff was impossible and a latency-sensitive
  path could not lower it. The default is unchanged at 30 seconds, including for
  `default(Bounce)`. A non-positive maximum is rejected; a base delay above the
  maximum is clamped rather than rejected, because cross-field rejection in a
  fluent immutable builder depends on the order the methods are called (#35)
- `ShotAsync` overloads taking `Func<CancellationToken, Task<T>>` and
  `Func<CancellationToken, Task>`. Without them a timeout could stop the caller
  waiting but never stop the work, and no caller could fix that from outside.
  The existing overloads still work and their XML docs now say they abandon the
  operation on timeout

### Added - Carom.Extensions

- `CushionBuilder.When(Func<Exception, bool>)` decides which exceptions count as
  the dependency's fault. Without it every exception counted, so a bug in the
  calling code opened the circuit on a healthy service
- `CushionBuilder.WithinLast(TimeSpan)` sets the sampling duration

### Fixed - Carom.Extensions

- `CompartmentStore` validates a conflicting registration on the lost-`GetOrAdd`
  race as well as the sequential path. Under a concurrent first touch, 16 of 50
  conflicting registrations were accepted silently, so a bulkhead sized for one
  could run at fifty. This is the bug `ThrottleStore` had already fixed
- All three stores now share one conflict rule in `StoreConflictHelper` and call
  it on both registration paths. The rule existed in three copies and was
  enforced fully in one

### Changed - docs

- The README's performance claims (175,000x startup, 15x hot path, 4.8x async)
  and the separate, contradictory set in `docs/BENCHMARKS.md` (3,750x, 2x, 2x)
  are removed rather than corrected. Neither was gated and measurement supported
  neither. What remains is what a test defends: zero allocations on the
  successful hot path, the measured assembly sizes, and per-target dependency
  counts
- `tests/Carom.Tests/PublishedClaimsTests.cs` gates those claims, so a published
  number cannot go stale unnoticed again

### Fixed - Carom.DependencyInjection

- The timeout strategy stopped firing under thread pool pressure. A 50 ms
  timeout over a 200 ms operation would throw nothing and return the result
  after the full 200 ms. Reproduced ten times against an unmodified checkout by
  running two full suites concurrently, and zero times in nine single-suite runs
  of the same commit. The strategy now takes a monotonic timestamp before the
  work starts and throws `TimeoutException` on any completing path that overran
  the budget, whichever way the wait behaved. `ExecuteAsync` had the same hole
  for an action that ignores its cancellation token and got the same backstop.
  The underlying mechanism was never identified, so this guarantees the
  observable contract rather than explaining the cause, and the code says so.
  It does not stop the abandoned work, only guarantees the caller is told

### Fixed - review follow-ups

- The `Bounce` overloads on the Cushion, Compartment and Throttle extensions
  unpacked selected fields by hand, so `MaxDelay` and the synchronous timeout
  guard, both added in this release, never reached core through them. Measured:
  a 50 ms `WithMaxDelay` took 5,615 ms through the extension against 170 ms
  through core, and a `Bounce` carrying a timeout threw on core but was accepted
  silently on all three extension paths. They now pass the whole `Bounce`
  through, so a future field cannot drift the same way. This was the defect
  class the release exists to fix, reintroduced by the release itself
- `Carom.DependencyInjection`'s timeout reported `TaskCanceledException` instead
  of `TimeoutException` when a saturated pool cancelled the queued task before
  the action started. Found only once the pressure test was made to wait for
  actual pressure rather than merely queueing blockers
- `WithinLast(TimeSpan.MaxValue)` could overflow the conversion to timestamp
  units and yield a negative duration on pre-.NET 9 runtimes, so fresh failures
  were discarded and the circuit never opened. The conversion now saturates
- The extensions size gate passed silently when it could not find the assembly,
  and `docs/BENCHMARKS.md` stated the measured sizes as though the test asserted
  them exactly rather than as upper bounds

### Fixed - Carom.EntityFramework

- `TimeoutRejectedException` is now retried. The transient classifier decided
  what to retry by lowercasing the message and looking for "timeout", but
  Carom's own timeout reads "Operation timed out after Nms", which does not
  contain that substring. So the one exception this release explicitly keeps
  retryable was classified permanent by Carom's own EF package and never
  retried. It is now matched by type before the message fallback, which is
  otherwise unchanged so nothing that used to retry stopped.
  `DbUpdateConcurrencyException` is deliberately not transient: replaying the
  same stale original values hits the same conflict and can mask a lost update.
  Only the parameter overload was affected; the Bounce overload never consulted
  this classifier and was already correct

### Changed - Carom.Telemetry.OpenTelemetry

- The package description promised "automatic metrics, traces, and activity
  tracking for all Carom resilience patterns". Nothing is automatic:
  `CaromTelemetry` appears exactly once in the whole source tree, its own
  declaration, and no core, extension, HTTP, DI, ASP.NET Core or EF path ever
  calls its Record methods or `StartActivity`. The instruments work when called,
  which is now tested. The description and the README row say what the package
  actually does instead. Wiring the hooks into the core paths is filed
  separately; it needs a seam that does not make the core depend on the
  telemetry package, which is a larger design job than this release

### Fixed - tests

- Nine tests asserted on wall-clock timing and failed intermittently under load.
  Where a clock could drive the behaviour it now does, using the injectable
  `Func<long>` seam; where the behaviour is genuinely concurrent, the timing
  assertions were replaced with load-independent invariants such as peak
  concurrency never exceeding the limit and every permit being returned.
  `RateLimiter_EnforcesApiQuota` was not merely flaky but wrong: it asserted at
  least one request was throttled, which fails a correct limiter whenever the
  loop runs longer than a second, because refill legitimately admits all of them
- Four upper-bound assertions on synchronous fail-fast paths are still
  wall-clock bounded. Removing those needs an injectable delay hook on the retry
  loop, the analogue of the existing timestamp seam

## [1.7.0] - 2026-08-28

### Added
- `Carom.AspNetCore`, `Carom.EntityFramework` and `Carom.Telemetry.OpenTelemetry`
  target `net8.0;net10.0`. The core packages stay `netstandard2.0` and already
  run on .NET 10. Tests run on both runtimes in CI
- `Carom.EntityFramework` references EF Core 8.0.30 on net8.0 and EF Core
  10.0.0 on net10.0

### Changed
- Dependency floors with known vulnerabilities raised: EF Core 8.0.0 pulled
  Microsoft.Extensions.Caching.Memory 8.0.0 (GHSA-qj66-m88j-hmgj, high) and
  OpenTelemetry.Api 1.7.0 carried GHSA-g94r-2vxg-569j (moderate, patched in
  1.15.3)

## [1.6.0] - 2026-08-28

### Fixed - Carom.Extensions
- The `Bounce` overloads of `Shot`/`ShotAsync` now apply the same fail-fast
  default as the `retries:` overloads: a `CircuitOpenException`,
  `ThrottledException` or `CompartmentFullException` is no longer retried with
  full backoff. `Bounce.Times(3)` against an open circuit used to pay the whole
  jittered backoff on every rejected call. A caller-supplied predicate still
  wins. The async `Bounce` overloads also honor `Bounce.WithTimeout`, which
  they previously dropped
- `Compartment.QueueDepth` now does what its documentation always said: up to
  `QueueDepth` callers wait for a slot, anything past the bound is shed
  immediately. It was stored, validated and never read; depth 10 behaved
  exactly like depth 0
- `Masse` hedging honors `HedgeDelay` between attempts. A completed but
  unsatisfactory result used to shortcut the wait, launching every remaining
  attempt as fast as the loop could go (4 attempts in 12ms against a 500ms
  delay). When every attempt succeeds but none satisfies `ShouldHedge`, the
  last result is returned instead of an `AggregateException` with zero inner
  exceptions
- Circuit half-open timing and token-bucket refill read a monotonic clock
  (`Stopwatch.GetTimestamp`) instead of `DateTime.UtcNow`. A backwards NTP
  step used to extend the open period by the size of the step and stall the
  bucket entirely; a forwards jump refilled the whole burst at once
- `ThrottleStore` rejects a second registration whose `TimeWindow` disagrees
  with the existing one, matching what it already did for the other settings
- `Throttle.WithRate` validates its arguments instead of silently building a
  limiter that always throws

### Fixed - Carom.Http
- `CaromHttpHandler` buffers the request body so a retry actually resends it;
  a retried `StreamContent` used to go out empty

### Changed - Carom
- `Bounce.WithTimeout` documents that the timeout is honored only on the
  async path; the synchronous `Shot` overloads have no cancellation mechanism
  and ignore it

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
