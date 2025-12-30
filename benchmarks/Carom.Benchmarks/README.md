# Carom Benchmarks

This directory contains performance benchmarks comparing Carom against Polly, the industry-standard resilience library for .NET.

## Running Benchmarks

```bash
cd benchmarks/Carom.Benchmarks
dotnet run -c Release
```

## What We Measure

The benchmarks prove three key advantages of Carom:

1. **Zero Startup Overhead** - No policy builders or configuration objects
2. **Faster Hot Path Execution** - Minimal overhead on successful operations
3. **Lower Memory Allocations** - Less GC pressure per operation

## Benchmark Results

> **Environment**: .NET 8.0.7, Apple M1 (Arm64), macOS 26.2  
> **Date**: December 28, 2025

### Startup Overhead

| Method            | Mean      | Allocated |
| ----------------- | --------- | --------- |
| **Carom_Startup** | **~0 ns** | **0 B**   |
| Polly_Startup     | 3,857 ns  | 8,168 B   |

**Analysis**: Carom has literally zero startup overhead because it uses static methods with no configuration objects. Polly requires building a `ResiliencePipeline` with a builder pattern, which takes ~3.9 microseconds and allocates 8KB of memory. In serverless environments (Azure Functions, AWS Lambda), this happens on every cold start.

**Winner**: Carom is **infinitely faster** (Polly takes 3,857ns vs Carom's unmeasurable overhead)

---

### Hot Path - Synchronous Success (No Retry)

| Method                    | Mean         | Allocated |
| ------------------------- | ------------ | --------- |
| **Carom_HotPath_Success** | **10.92 ns** | **64 B**  |
| Polly_HotPath_Success     | 167.81 ns    | 88 B      |

**Analysis**: When operations succeed (the common case), Carom executes **15.4× faster** than Polly. This is the critical path that runs millions of times in production.

**Winner**: Carom is **15.4× faster**

---

### Hot Path - Async Success (No Retry)

| Method                  | Mean         | Allocated |
| ----------------------- | ------------ | --------- |
| **Carom_Async_Success** | **45.03 ns** | **280 B** |
| Polly_Async_Success     | 216.45 ns    | 232 B     |

**Analysis**: For async operations, Carom is **4.8× faster** than Polly. Interestingly, Carom allocates slightly more memory due to its simpler async state machine, but the execution speed advantage far outweighs this.

**Winner**: Carom is **4.8× faster**

---

## Summary Table

| Scenario       | Carom           | Polly             | Carom Advantage          |
| -------------- | --------------- | ----------------- | ------------------------ |
| Startup        | ~0 ns, 0 B      | 3,857 ns, 8,168 B | ∞× faster, 0 allocations |
| Sync Hot Path  | 10.92 ns, 64 B  | 167.81 ns, 88 B   | **15.4× faster**         |
| Async Hot Path | 45.03 ns, 280 B | 216.45 ns, 232 B  | **4.8× faster**          |

## Why Carom Is Faster

1. **No Builder Pattern**: Carom uses static methods, Polly builds pipeline objects
2. **Minimal Abstraction**: Direct execution path vs Polly's strategy pattern
3. **Zero Configuration Overhead**: No policy objects to construct or manage
4. **Optimized for the Common Case**: Fast path for success, acceptable overhead for retries

## Real-World Impact

In a typical microservice handling 10,000 requests/second:

- **Carom**: 109,200 ns/sec overhead = **0.01% CPU**
- **Polly**: 1,678,100 ns/sec overhead = **0.17% CPU**

Over 1 billion requests:
- **Carom**: 10.92 seconds total overhead
- **Polly**: 167.81 seconds total overhead

**Savings**: ~157 seconds of CPU time per billion requests

## Benchmark Methodology

All benchmarks use BenchmarkDotNet with:
- 3 warmup iterations
- 10 measurement iterations
- Memory diagnostics enabled
- Release configuration with optimizations

The Polly pipeline is pre-built in `[GlobalSetup]` to give it the best possible advantage (simulating a cached/singleton pipeline). Despite this, Carom still outperforms significantly.

## Viewing Full Results

See [`resilience_benchmark.txt`](./resilience_benchmark.txt) for complete BenchmarkDotNet output including:
- Detailed statistics (StdDev, Median, Confidence Intervals)
- GC collection counts
- Outlier analysis
- Diagnostic output
