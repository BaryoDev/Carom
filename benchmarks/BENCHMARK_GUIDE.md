# Carom Benchmarks Documentation

## Overview

This document describes the benchmark suite for Carom, including methodology, expectations, and how to run and interpret the benchmarks.

## Benchmark Categories

### 1. Core Resilience Benchmarks

#### Light Load Tests
- **Purpose**: Measure performance of individual retry operations
- **Expected Results**:
  - Single success: < 50ns
  - Transient failure (1 retry): < 200ns
  - Async success: < 100ns
- **Use Case**: Single API calls, low-frequency operations

#### Medium Load Tests
- **Purpose**: Measure performance under moderate throughput
- **Expected Results**:
  - 10 sequential operations: < 1µs
  - 10 parallel operations: < 5µs
  - With intermittent failures: < 2µs
- **Use Case**: Batch processing, moderate API traffic

#### Heavy Load Tests
- **Purpose**: Measure performance under high throughput
- **Expected Results**:
  - 100 sequential operations: < 10µs
  - 50 parallel operations: < 20µs
  - Mixed success/failure: < 30µs
- **Use Case**: High-traffic APIs, data processing pipelines

### 2. Circuit Breaker Benchmarks

#### Closed Circuit Tests
- **Purpose**: Measure overhead when circuit is closed
- **Expected Results**:
  - Single success: < 100ns overhead vs direct call
  - Sequential operations: < 1µs for 10 calls
  - Parallel operations: < 10µs for 10 concurrent calls
- **Use Case**: Normal operation mode

#### Open Circuit Tests
- **Purpose**: Measure fast-fail performance
- **Expected Results**:
  - Single rejection: < 20ns (extremely fast)
  - Multiple rejections: < 200ns for 10 rejections
  - Parallel rejections: < 500ns for 20 concurrent rejections
- **Use Case**: Service outage scenarios, preventing cascade failures

#### RingBuffer Tests
- **Purpose**: Measure failure tracking performance
- **Expected Results**:
  - 100 additions: < 2µs
  - Wrap-around (200 additions to 50-capacity): < 4µs
- **Implementation**: Lock-free circular buffer

### 3. Memory Allocation Tests

#### Simple Retry
- **Expected**: < 100 bytes allocation
- **Notes**: Most allocations from operation itself, minimal overhead

#### Retry with Timeout
- **Expected**: < 200 bytes allocation
- **Notes**: Includes CancellationTokenSource allocation

#### Circuit Breaker
- **Expected**: < 150 bytes allocation
- **Notes**: Includes shared state management

### 4. Critical Path Benchmarks

These benchmarks simulate real-world critical paths in applications:

#### Authentication Flow
- **Scenario**: Token retrieval with retry
- **Expected**: < 100µs (including 50ms base delay)
- **Failure Mode**: 2 retries on transient auth service failures

#### Database Query
- **Scenario**: Query with connection retry
- **Expected**: < 50µs (including 2ms simulated query time)
- **Failure Mode**: 3 retries on connection timeouts

#### External API with Fallback
- **Scenario**: API call with fallback to cached value
- **Expected**: < 20µs on success, < 5µs on fallback
- **Failure Mode**: 20% failure rate, immediate fallback

### 5. Realistic Scenario Benchmarks

#### API Gateway
- **Scenario**: Gateway routing with circuit breaker
- **Load**: 20 requests, 15% failure rate
- **Expected**: Circuit opens after 5 failures in 10 requests
- **Success Rate**: ~80-85% (depending on circuit state)

#### Database Connection Pool
- **Scenario**: Concurrent queries with circuit breaker
- **Load**: 15 parallel queries, 12.5% timeout rate
- **Expected**: Circuit opens to prevent connection exhaustion
- **Success Rate**: ~85-90%

#### Microservices Chain
- **Scenario**: Service-to-service calls with cascading circuit breakers
- **Load**: 10 requests through 2 services, 20% failure in downstream
- **Expected**: Both circuits protect their respective services
- **Success Rate**: ~75-80%

## Running Benchmarks

### Prerequisites
```bash
dotnet restore
dotnet build -c Release
```

### Run All Benchmarks
```bash
cd benchmarks/Carom.Benchmarks
dotnet run -c Release
```

### Run Specific Category
```bash
dotnet run -c Release --filter *LightLoad*
dotnet run -c Release --filter *HeavyLoad*
dotnet run -c Release --filter *ClosedCircuit*
dotnet run -c Release --filter *OpenCircuit*
dotnet run -c Release --filter *Realistic*
```

### Run Single Benchmark
```bash
dotnet run -c Release --filter *LightLoad_SingleSuccess*
```

## Interpreting Results

### Key Metrics

#### Mean Time
- Average execution time across all iterations
- Most important for typical performance

#### Median Time
- Middle value when sorted
- More robust to outliers than mean

#### Standard Deviation
- Measure of performance consistency
- Lower is better for predictable performance

#### Allocated Memory
- Bytes allocated per operation
- Important for GC pressure in high-throughput scenarios

### Performance Expectations

#### Excellent Performance
- Retry operation: < 100ns
- Circuit breaker check: < 50ns
- Memory allocation: < 100 bytes

#### Good Performance
- Retry operation: < 500ns
- Circuit breaker check: < 200ns
- Memory allocation: < 300 bytes

#### Acceptable Performance
- Retry operation: < 1µs
- Circuit breaker check: < 500ns
- Memory allocation: < 500 bytes

### Comparison with Polly

Based on existing benchmarks:

| Metric | Carom | Polly v8 | Improvement |
|--------|-------|----------|-------------|
| Startup | 0.02ns | 3,857ns | 175,000x faster |
| Hot Path (sync) | 10.9ns | 167.8ns | 15x faster |
| Hot Path (async) | 45ns | 216ns | 4.8x faster |
| Allocations | < 100 bytes | ~200+ bytes | 2x less |

## Benchmark Environment

### Recommended Environment
- **OS**: Ubuntu 22.04 LTS (for Linux) or Windows Server 2022 (for Windows)
- **CPU**: Modern x64 processor (Intel/AMD)
- **.NET**: .NET 8.0+
- **Configuration**: Release mode
- **Isolation**: No other heavy processes running

### Environment Variables
```bash
# Disable dynamic PGO for consistent results
export DOTNET_TieredPGO=0
export DOTNET_TC_QuickJitForLoops=1
```

## Continuous Benchmarking

### CI/CD Integration
Benchmarks should be run:
1. On every PR to detect performance regressions
2. On release branches before publishing
3. Periodically (weekly) to track long-term trends

### Performance Regression Detection
- Alert if mean time increases by > 10%
- Alert if memory allocation increases by > 20%
- Alert if any benchmark fails to complete

## Contributing Benchmarks

When adding new benchmarks:

1. **Document the scenario**: Explain what real-world use case it represents
2. **Set expectations**: Document expected performance characteristics
3. **Use categories**: Tag with appropriate `[BenchmarkCategory]`
4. **Test with data**: Use realistic data sizes and patterns
5. **Consider concurrency**: Test both sequential and parallel scenarios
6. **Measure memory**: Include `[MemoryDiagnoser]` attribute

## Troubleshooting

### Inconsistent Results
- Ensure running in Release mode
- Close other applications
- Run multiple iterations for statistical significance
- Check for thermal throttling on the CPU

### Unexpectedly Slow Results
- Verify .NET version (8.0+ recommended)
- Check for debug symbols in release build
- Ensure tiered compilation is enabled
- Review benchmark code for unintended allocations

### Memory Allocation Issues
- Use PerfView or dotMemory for detailed analysis
- Check for boxing of value types
- Review lambda captures and closures
- Verify struct vs class usage

## References

- [BenchmarkDotNet Documentation](https://benchmarkdotnet.org/)
- [.NET Performance Best Practices](https://docs.microsoft.com/en-us/dotnet/core/performance/)
- [Carom Performance Design](../../docs/BENCHMARKS.md)

---

Last Updated: 2026-01-02
Version: 1.0
