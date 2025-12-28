# AI Agent Prompt: Implement Carom Resilience Library

## Your Mission

You are implementing the **Carom resilience library**, a lean, performant, zero-dependency alternative to Polly for .NET. Your work must adhere to the **Baryo.Dev philosophy**: zero external dependencies, maximum performance, safe by default.

## Critical Instructions

**READ THESE DOCUMENTS FIRST** (in order):
1. `LEAN.md` - Understand the Baryo philosophy
2. `ARCHITECTURE_STRATEGY.md` - Understand the overall strategy
3. `PACKAGE_ARCHITECTURE.md` - Understand the modular package design
4. `IMPLEMENTATION_ROADMAP.md` - See detailed code examples
5. `.agent/IMPLEMENTATION_INSTRUCTIONS.md` - **YOUR PRIMARY WORK GUIDE**

## What You Will Implement

### Phase 1.1: Circuit Breaker ("Cushion") - v1.1.0
**Package**: `Carom.Extensions` (NEW)
**Timeline**: 2 weeks
**Files to Create**:
- `src/Carom.Extensions/CircuitState.cs`
- `src/Carom.Extensions/RingBuffer.cs`
- `src/Carom.Extensions/CushionState.cs`
- `src/Carom.Extensions/CushionStore.cs`
- `src/Carom.Extensions/CircuitOpenException.cs`
- `src/Carom.Extensions/Cushion.cs`
- `src/Carom.Extensions/CaromCushionExtensions.cs`
- `tests/Carom.Extensions.Tests/CushionTests.cs`
- `benchmarks/Carom.Benchmarks/CircuitBreakerBenchmarks.cs`

**Performance Target**: <10ns overhead when circuit closed, zero allocations

### Phase 1.2: Fallback ("Safety Pocket") - v1.2.0
**Package**: `Carom.Extensions` (enhance)
**Timeline**: 1 week
**Files to Create**:
- `src/Carom.Extensions/CaromFallbackExtensions.cs`
- `tests/Carom.Extensions.Tests/FallbackTests.cs`
- `benchmarks/Carom.Benchmarks/FallbackBenchmarks.cs`

**Performance Target**: Zero allocations if fallback not invoked

### Phase 1.3: Timeout Enhancement - v1.3.0
**Package**: `Carom` (core enhancement)
**Timeline**: 1 week
**Files to Modify/Create**:
- `src/Carom/Bounce.cs` (add Timeout property)
- `src/Carom/Carom.cs` (add timeout parameter)
- `src/Carom/TimeoutRejectedException.cs` (new)
- `tests/Carom.Tests/TimeoutTests.cs` (new)
- `benchmarks/Carom.Benchmarks/TimeoutBenchmarks.cs` (new)

**Performance Target**: Only allocate CancellationTokenSource when timeout specified

## Non-Negotiable Rules

### Rule 1: Zero External Dependencies
```bash
# After EVERY change, verify:
dotnet list package
# Must show ZERO external NuGet packages in core/extensions
```

### Rule 2: All Tests Must Pass
```bash
# Before ANY commit:
dotnet test
# Must show 100% tests passing
```

### Rule 3: Test Coverage >= 90%
```bash
# Before ANY commit:
dotnet test --collect:"XPlat Code Coverage"
# Must show >= 90% coverage for new code
```

### Rule 4: Benchmarks Must Run
```bash
# Before ANY commit:
cd benchmarks/Carom.Benchmarks
dotnet run -c Release
# Must complete successfully and meet performance targets
```

### Rule 5: Zero Compiler Warnings
```bash
# Before ANY commit:
dotnet build /warnaserror
# Must succeed (warnings treated as errors)
```

### Rule 6: Package Size Limits
```bash
# Before ANY commit:
dotnet pack -c Release
ls -lh bin/Release/*.nupkg
# Carom.Extensions must be < 100KB
```

## Your Workflow for Each Feature

1. **Read Implementation Instructions**
   - Open `.agent/IMPLEMENTATION_INSTRUCTIONS.md`
   - Find the section for your current phase (e.g., "Phase 1.1: Circuit Breaker")
   - Read EVERY step carefully

2. **Implement Code**
   - Create files EXACTLY as specified
   - Copy code from instruction document (it's provided in full)
   - Do NOT deviate from the spec
   - Do NOT "improve" or "optimize" without proof

3. **Write Tests**
   - Create comprehensive unit tests (examples provided)
   - Aim for >90% coverage
   - Test both success and failure paths
   - Test edge cases and invalid inputs

4. **Write Benchmarks**
   - Create benchmarks comparing vs Polly (examples provided)
   - Include `[MemoryDiagnoser]` to track allocations
   - Verify performance targets are met

5. **Update Documentation**
   - Update `README.md` with usage examples
   - Update `CHANGELOG.md` with changes
   - Ensure all public APIs have XML docs

6. **Run Verification Checklist**
   - Run ALL verification steps from `.agent/IMPLEMENTATION_INSTRUCTIONS.md`
   - Do NOT skip ANY step
   - If ANY step fails, fix it before proceeding

7. **Mark Todo Complete**
   - Use TodoWrite to update progress
   - Mark as complete ONLY when all verification passes

## Forbidden Actions

❌ **NEVER** add external NuGet dependencies to core/extensions
❌ **NEVER** skip unit tests
❌ **NEVER** skip benchmarks
❌ **NEVER** skip documentation updates
❌ **NEVER** skip verification steps
❌ **NEVER** use reflection or dynamic code generation
❌ **NEVER** create background threads without explicit user control
❌ **NEVER** allocate on hot path without justification
❌ **NEVER** commit code with compiler warnings
❌ **NEVER** commit code with failing tests
❌ **NEVER** guess at implementation details - ask if unclear

## Quality Checklist (Before ANY Commit)

Run this checklist EVERY TIME before committing:

```bash
# 1. Build succeeds
dotnet build --configuration Release
# MUST succeed with zero warnings

# 2. Tests pass
dotnet test
# MUST show 100% tests passing

# 3. Test coverage
dotnet test --collect:"XPlat Code Coverage"
# MUST show >= 90% coverage

# 4. No external dependencies
dotnet list package
# MUST show zero external packages in core/extensions

# 5. Benchmarks run
cd benchmarks/Carom.Benchmarks
dotnet run -c Release
cd ../..
# MUST complete successfully

# 6. Package builds
cd src/Carom.Extensions
dotnet pack -c Release
ls -lh bin/Release/*.nupkg
cd ../..
# MUST create package < 100KB

# 7. No warnings as errors
dotnet build /warnaserror
# MUST succeed
```

**If ANY check fails, DO NOT commit. Fix it first.**

## Example: How to Start Phase 1.1

```bash
# 1. Read the instructions
cat .agent/IMPLEMENTATION_INSTRUCTIONS.md
# Find "Phase 1.1: Circuit Breaker"

# 2. Create the new project
cd src
dotnet new classlib -n Carom.Extensions -f netstandard2.0
cd Carom.Extensions

# 3. Edit Carom.Extensions.csproj
# (Copy content from IMPLEMENTATION_INSTRUCTIONS.md Step 1.1.1)

# 4. Create CircuitState.cs
# (Copy content from IMPLEMENTATION_INSTRUCTIONS.md Step 1.1.2)

# 5. Continue through ALL steps in order...

# 6. After ALL files created, run verification:
cd ../..
dotnet build --configuration Release
dotnet test
dotnet test --collect:"XPlat Code Coverage"
# ... (rest of checklist)

# 7. Only commit when ALL checks pass
git add .
git commit -m "feat: Add circuit breaker (Cushion) to Carom.Extensions"
```

## When You're Stuck

If you encounter ANY confusion or blocker:

1. **STOP immediately** - don't guess
2. **Re-read the relevant section** in IMPLEMENTATION_INSTRUCTIONS.md
3. **Check the examples** in IMPLEMENTATION_ROADMAP.md
4. **Ask the user for clarification** - provide context about what's unclear
5. **Wait for answer** before proceeding

**Guessing creates bugs. Asking creates clarity.**

## Success Criteria

Your implementation is successful when:

✅ All code compiles with zero warnings
✅ All tests pass (100% pass rate)
✅ Test coverage >= 90%
✅ All benchmarks run and meet targets
✅ Zero external dependencies in core/extensions
✅ Package size under limits
✅ Documentation updated
✅ CHANGELOG updated
✅ All verification steps pass

## Start Here

**Your first task**:

1. Read `.agent/IMPLEMENTATION_INSTRUCTIONS.md` completely
2. Use TodoWrite to create todos for Phase 1.1 (Circuit Breaker)
3. Start with Step 1.1.1: Create Package Structure
4. Work through each step sequentially
5. Do NOT skip ahead
6. Do NOT skip verification steps
7. Mark each todo as completed ONLY when verified

**Begin by reading IMPLEMENTATION_INSTRUCTIONS.md now.**

---

## Quick Reference

- **Main Instructions**: `.agent/IMPLEMENTATION_INSTRUCTIONS.md`
- **Architecture**: `ARCHITECTURE_STRATEGY.md`
- **Package Design**: `PACKAGE_ARCHITECTURE.md`
- **Code Examples**: `IMPLEMENTATION_ROADMAP.md`
- **Philosophy**: `LEAN.md`
- **Current Code**: `src/Carom/Carom.cs`, `src/Carom/Bounce.cs`, `src/Carom/JitterStrategy.cs`

---

**Your mission is clear. Your instructions are detailed. Your standards are high. Begin.**
