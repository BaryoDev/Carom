# Benchmark Configuration

This directory contains benchmark configuration and baseline results.

## Running Benchmarks Locally

```bash
cd benchmarks/Carom.Benchmarks
dotnet run -c Release
```

## Benchmark Categories

- **Retry**: Core retry logic performance
- **Timeout**: Timeout overhead
- **Circuit Breaker**: Cushion pattern performance
- **Fallback**: Safety Pocket pattern performance
- **Bulkhead**: Compartment pattern performance
- **Rate Limiting**: Throttle pattern performance

## Baseline Results

Baseline results are stored in `baselines/` directory and used for regression detection.

## CI Integration

Benchmarks run automatically on:
- Every PR (compared to main branch)
- Every push to main (updates baseline)
- Manual workflow dispatch

## Performance Targets

All benchmarks must meet these targets:

| Metric                    | Target  |
| ------------------------- | ------- |
| Retry overhead (success)  | <10ns   |
| Circuit Breaker (closed)  | <10ns   |
| Bulkhead (available)      | <50ns   |
| Rate Limiting (available) | <20ns   |
| Allocations (hot path)    | 0 bytes |

## Regression Detection

CI will fail if:
- Any benchmark is >150% slower than baseline
- Any hot path allocates memory
- Package size exceeds limits

## Viewing Results

- **GitHub Actions**: Check workflow runs
- **PR Comments**: Automated comments on PRs
- **Dashboard**: See [PERFORMANCE.md](../PERFORMANCE.md)
