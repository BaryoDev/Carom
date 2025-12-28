---
description: Ensure Carom remains lean and dependency-free
---

# Lean Build & Verification

This workflow ensures that the project strictly adheres to the Baryo Dev philosophy.

## 1. Dependency Audit
Check for any accidental NuGet dependencies in the core projects.

```bash
// turbo
dotnet list src/Carom/Carom.csproj package
```
> [!IMPORTANT]
> The output should show **zero** external packages (except for system/standard libraries).

## 2. Size Check
Ensure the compiled DLL remains under the 50KB target.

```bash
// turbo
dotnet build -c Release
ls -lh src/Carom/bin/Release/netstandard2.0/Carom.dll
```

## 3. Allocation Baseline
Run the benchmarks to ensure no regression in allocations.

```bash
// turbo
dotnet run -c Release --project benchmarks/Carom.Benchmarks
```

## 4. Documentation Compliance
Verify that `LEAN.md` exists and is up to date with any new architectural changes.
