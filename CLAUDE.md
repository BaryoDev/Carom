# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Carom is a high-performance, zero-dependency resilience library for .NET. It provides retry, timeout, circuit breaker, bulkhead, rate limiting, and fallback patterns with minimal allocations and lock-free implementations.

## Build Commands

```bash
dotnet build                              # Build all projects
dotnet build -c Release                   # Build in Release mode
dotnet test                               # Run all tests
dotnet test --filter "FullyQualifiedName~CushionTests"  # Run specific test class
dotnet test --filter "MethodName"         # Run single test by method name
dotnet pack -c Release                    # Create NuGet packages
```

## Running Benchmarks

```bash
cd benchmarks/Carom.Benchmarks
dotnet run -c Release                     # Run all benchmarks
dotnet run -c Release --filter *LightLoad*  # Run specific category
```

## Architecture

### Core Patterns (Billiards Terminology)

| Pattern         | Class                | Package          |
| --------------- | -------------------- | ---------------- |
| Retry + Timeout | `Carom`, `Bounce`    | Carom            |
| Circuit Breaker | `Cushion`            | Carom.Extensions |
| Bulkhead        | `Compartment`        | Carom.Extensions |
| Rate Limiting   | `Throttle`           | Carom.Extensions |
| Fallback        | `Pocket/PocketAsync` | Carom.Extensions |

### State Management Pattern

Each resilience pattern follows a consistent architecture:
- **State class** (e.g., `CushionState`, `ThrottleState`): Holds mutable state using lock-free Interlocked operations
- **Store class** (e.g., `CushionStore`, `ThrottleStore`): Manages singleton instances per service/resource key
- **Builder pattern**: Fluent API for configuration (e.g., `Cushion.ForService("api").OpenAfter(...)`)

### Key Implementation Details

- **Lock-free**: All state updates use `Interlocked` operations, never locks
- **Zero allocations on hot path**: Uses structs (`Bounce`), thread-static Random, avoids closures
- **RingBuffer**: Sliding window tracking for circuit breaker and rate limiter
- **Decorrelated jitter**: Mandatory exponential backoff with AWS-style jitter formula

## House Style (applies to everything you write here)

These two rules are enforced by CI (`.github/workflows/house-style.yml`) and are the ones most
often broken by AI tooling, so check before you commit. Full text in `CONTRIBUTING.md`.

- **Never add AI attribution.** No "Generated with Claude Code", no `Co-Authored-By: Claude`, no
  `Co-Authored-By: Copilot`, no `Claude-Session:` trailer, no bot footer or badge. This covers
  commit messages, PR titles and descriptions, issue and review comments, and code comments. Using
  AI tools is fine; the credit line is not. The author is whoever submits the work.
- **Never use em dashes** (`—`) in prose, code comments, commit messages, documentation, or any
  text posted to a GitHub thread. Use a comma, a colon, a semicolon, parentheses, or two sentences.

Run `scripts/check-house-style.sh` before pushing. It checks added lines and the commit messages
your branch introduces, and prints the offending line numbers.

Prose in Markdown files wraps at 100 columns.

## Coding Standards (BaryoDev)

- **No LINQ in hot paths** - Use `for` loops
- **Zero allocations** - Use `struct` and `Span<T>`
- **Zero external dependencies** in core packages
- **Expression Trees over Reflection** for meta-programming
- **xUnit with Verdict style** (Fluent Assertions patterns)
- **Benchmarks required** for performance-critical changes (BenchmarkDotNet)
- **License**: MPL-2.0 headers on all source files
- **Versioning**: SemVer with manual bumps only

## Test Organization

- `tests/Carom.Tests/` - Core retry and timeout tests
- `tests/Carom.Extensions.Tests/` - Circuit breaker, bulkhead, throttle, fallback tests
- Test categories: `SmokeTests`, `EdgeCaseTests`, `SecurityTests` (thread-safety)

## Package Structure

```text
src/
├── Carom/                 # Core: Carom.cs, Bounce.cs, JitterStrategy.cs
├── Carom.Extensions/      # Cushion, Compartment, Throttle + Stores
├── Carom.Http/            # HttpClient integration
├── Carom.AspNetCore/      # Health checks
├── Carom.EntityFramework/ # EF Core retry
└── Carom.Telemetry.OpenTelemetry/  # Metrics
```
