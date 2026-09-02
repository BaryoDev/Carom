# Carom Measurements

This document is short on purpose. A published number needs a gate behind it, so it only carries figures that are deterministic and checked by `tests/Carom.Tests/PublishedClaimsTests.cs`, plus measurements stated with their limits.

Earlier versions of this document and the README claimed speed ratios against Polly (175,000x startup, 15x hot path, 4.8x async here; 3,750x and 2x in an older revision of this file). The two documents disagreed with each other and with re-measurement, because nothing checked them. Those ratios are deleted, not corrected: they came from stopwatch loops on a non-idle laptop, which is not a measurement worth publishing.

## Environment

- Apple M1 laptop, macOS, .NET 8
- Polly 8.4.2 (`Polly` and `Polly.Core` packages)
- Single process, stopwatch loops around warmed calls, machine not idle. Not BenchmarkDotNet.

Allocation counts and file sizes from this setup are exact. Timings from it are not, so none are published.

## Allocations per successful call

| Call | Allocated per call |
| ---- | ------------------ |
| Carom `Carom.Shot` | 0 B |
| Polly v8 `ResiliencePipeline.Execute` | 24 B |
| Polly v7 API `Policy.Execute` | 248 B |

Allocations were read with `GC.GetAllocatedBytesForCurrentThread` around a warmed loop, which is exact per thread. The 0 B figure is asserted by `PublishedClaimsTests.Shot_SuccessfulSyncCall_AllocatesZeroBytes` on every test run.

## Assembly size on disk

Release builds, bytes on disk:

| Assembly | Size |
| -------- | ---- |
| Carom.dll (netstandard2.0) | 19,968 B |
| Carom.Extensions.dll (netstandard2.0) | 51,200 B |
| Polly.Core.dll (net8.0, from the 8.4.2 package) | 242,736 B |
| Polly.dll (net6.0, from the 8.4.2 package) | 297,520 B |

`PublishedClaimsTests` bounds the Carom and Carom.Extensions sizes so the README figures cannot drift unnoticed.

## Package dependencies by target framework

Carom's numbers come from its project files (no `PackageReference` at all, enforced by `PublishedClaimsTests`). Polly.Core's come from the nuspec shipped in the 8.4.2 package:

| Target | Carom | Carom.Extensions | Polly.Core 8.4.2 |
| ------ | ----- | ---------------- | ---------------- |
| net8.0 | 0 | Carom only | 0 |
| net6.0 | 0 | Carom only | 1 |
| netstandard2.0 | 0 | Carom only | 4 |
| net462 / net472 | 0 | Carom only | 5 |

On net8.0, where new projects start, Polly.Core is also dependency-free. Carom's advantage is that it stays dependency-free on netstandard2.0 and .NET Framework, the hosts most likely to care.

## What happened to the startup comparison

Earlier versions compared building a Polly pipeline (about 5 microseconds, once) against assigning a Carom `Bounce` struct. An application builds its pipeline once and holds it, so that comparison does not describe anything a user waits for. It is retired.

## Timing

Per successful call, both libraries cost on the order of tens to hundreds of nanoseconds, which disappears next to the I/O being wrapped. No ratio is published: choosing a resilience library on nanoseconds is the wrong reason, and the setup above cannot defend one anyway.

## Running benchmarks yourself

```bash
cd benchmarks/Carom.Benchmarks
dotnet run -c Release
```

Results land in `BenchmarkDotNet.Artifacts/results/`. If you produce BenchmarkDotNet numbers on an idle machine and want them published here, add a gate for them first.
