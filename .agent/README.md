# AI Agent Workspace for Carom Implementation

This directory contains all instructions and guidelines for AI agents working on Carom.

## 📋 File Overview

### For AI Developers

1. **[AGENT_PROMPT.md](AGENT_PROMPT.md)** - START HERE
   - Quick-start guide for AI agents
   - High-level mission and rules
   - Workflow instructions
   - Quality checklist

2. **[IMPLEMENTATION_INSTRUCTIONS.md](IMPLEMENTATION_INSTRUCTIONS.md)** - PRIMARY WORK GUIDE
   - Detailed step-by-step implementation instructions
   - Complete code for every file
   - Verification steps for each feature
   - Anti-laziness rules and quality gates

### For Human Architects

3. **Context Documents** (in parent directory):
   - `../ARCHITECTURE_STRATEGY.md` - Overall strategic plan
   - `../PACKAGE_ARCHITECTURE.md` - Modular package design
   - `../IMPLEMENTATION_ROADMAP.md` - Detailed implementation plan
   - `../EXECUTIVE_SUMMARY.md` - High-level summary for leadership

## 🚀 How to Use This Workspace

### For AI Agents

**Step 1**: Read [AGENT_PROMPT.md](AGENT_PROMPT.md) to understand your mission

**Step 2**: Read [IMPLEMENTATION_INSTRUCTIONS.md](IMPLEMENTATION_INSTRUCTIONS.md) to get detailed instructions

**Step 3**: Follow the workflow:
- Create todos using TodoWrite
- Implement each feature following the exact specifications
- Run verification checklist after EVERY change
- Update documentation as you go
- Mark todos complete only when ALL checks pass

**Step 4**: Never commit until the quality checklist passes 100%

### For Humans Supervising AI Agents

**Give this prompt to your AI agent**:

```
You are implementing the Carom resilience library.

Read and follow these documents in order:
1. .agent/AGENT_PROMPT.md - Your mission and rules
2. .agent/IMPLEMENTATION_INSTRUCTIONS.md - Detailed implementation steps

Start by reading AGENT_PROMPT.md and confirm you understand the mission.
Then create todos for Phase 1.1 (Circuit Breaker) using TodoWrite.
Begin implementation following IMPLEMENTATION_INSTRUCTIONS.md exactly.

Do NOT skip any steps. Do NOT skip any verification checks. Do NOT commit code with failing tests.
```

## 📊 Implementation Phases

### Phase 1: Core Resilience (Q1 2025)

- **Phase 1.1**: Circuit Breaker ("Cushion") - v1.1.0
  - New package: `Carom.Extensions`
  - Files: ~8 code files + tests + benchmarks
  - Timeline: 2 weeks
  - Status: 🔴 Not Started

- **Phase 1.2**: Fallback ("Safety Pocket") - v1.2.0
  - Package: `Carom.Extensions` (enhance)
  - Files: ~3 code files + tests + benchmarks
  - Timeline: 1 week
  - Status: 🔴 Not Started

- **Phase 1.3**: Timeout Enhancement - v1.3.0
  - Package: `Carom` (core enhancement)
  - Files: ~3 code files + tests + benchmarks
  - Timeline: 1 week
  - Status: 🔴 Not Started

### Phase 2: Observability (Q2 2025)

- **Phase 2.1**: Telemetry Events - v1.4.0
- **Phase 2.2**: OpenTelemetry Integration - v1.4.1

### Phase 3: Ecosystem (Q3 2025)

- **Phase 3.1**: ASP.NET Core Integration - v1.5.0
- **Phase 3.2**: Entity Framework Integration - v1.6.0

## ✅ Quality Standards

Every feature must meet ALL of these standards:

### Code Quality
- ✅ Zero compiler warnings
- ✅ Zero nullable reference warnings
- ✅ XML docs on all public APIs
- ✅ No commented-out code
- ✅ No TODO comments in production

### Testing
- ✅ All tests pass (100%)
- ✅ Coverage >= 90%
- ✅ No flaky tests
- ✅ Tests run in < 5 seconds

### Performance
- ✅ Benchmarks run successfully
- ✅ No regressions vs baseline
- ✅ Allocation targets met
- ✅ Startup overhead < 1ms

### Documentation
- ✅ README updated
- ✅ CHANGELOG updated
- ✅ Code examples tested
- ✅ No broken links

### Package
- ✅ Package builds
- ✅ Size under target
- ✅ Zero external dependencies (core/extensions)
- ✅ Semantic versioning

## 🚫 Absolute Prohibitions

AI agents working on Carom must NEVER:

1. ❌ Add external NuGet dependencies to `Carom` or `Carom.Extensions`
2. ❌ Skip unit tests, benchmarks, or documentation
3. ❌ Commit code with failing tests or warnings
4. ❌ Use reflection, dynamic code generation, or unsafe code without explicit approval
5. ❌ Create background threads without explicit user control
6. ❌ Allocate on hot path without justification and benchmark proof
7. ❌ Deviate from specifications without asking first
8. ❌ "Improve" or "optimize" without benchmarks proving it's needed
9. ❌ Skip verification steps
10. ❌ Mark todos complete without ALL criteria met

## 📈 Progress Tracking

AI agents MUST use TodoWrite to track progress:

**Example todos for Phase 1.1**:
```json
[
  {"content": "Create Carom.Extensions project structure", "status": "pending"},
  {"content": "Implement CircuitState enum", "status": "pending"},
  {"content": "Implement RingBuffer<T>", "status": "pending"},
  {"content": "Implement CushionState", "status": "pending"},
  {"content": "Implement Cushion struct", "status": "pending"},
  {"content": "Create comprehensive unit tests", "status": "pending"},
  {"content": "Create benchmarks vs Polly", "status": "pending"},
  {"content": "Update README and CHANGELOG", "status": "pending"},
  {"content": "Run complete verification checklist", "status": "pending"}
]
```

**Update status as you progress**:
- `pending` → `in_progress` when starting
- `in_progress` → `completed` when ALL verification passes

## 🎯 Success Metrics

A phase is complete when:

1. ✅ All code files created as specified
2. ✅ All tests pass with >= 90% coverage
3. ✅ All benchmarks run and meet targets
4. ✅ All documentation updated
5. ✅ Full verification checklist passes
6. ✅ Package builds successfully
7. ✅ Human code review requested

## 📞 Getting Help

If an AI agent encounters issues:

1. **Re-read the instructions** - most answers are in IMPLEMENTATION_INSTRUCTIONS.md
2. **Check examples** - IMPLEMENTATION_ROADMAP.md has detailed code samples
3. **Ask the user** - provide context about what's unclear
4. **Do NOT guess** - guessing creates bugs

## 🔍 Verification Commands

Quick reference for verification:

```bash
# Build check
dotnet build --configuration Release

# Test check
dotnet test

# Coverage check
dotnet test --collect:"XPlat Code Coverage"

# Dependency check
dotnet list package

# Benchmark check
cd benchmarks/Carom.Benchmarks && dotnet run -c Release && cd ../..

# Package check
cd src/Carom.Extensions && dotnet pack -c Release && ls -lh bin/Release/*.nupkg && cd ../..

# Warning check
dotnet build /warnaserror
```

Run ALL of these before committing.

## 📚 Additional Resources

- [BenchmarkDotNet Documentation](https://benchmarkdotnet.org/)
- [.NET Interlocked Class](https://docs.microsoft.com/en-us/dotnet/api/system.threading.interlocked)
- [ConfigureAwait FAQ](https://devblogs.microsoft.com/dotnet/configureawait-faq/)
- [Struct vs Class Guidelines](https://docs.microsoft.com/en-us/dotnet/standard/design-guidelines/choosing-between-class-and-struct)

## 🎖️ Baryo.Dev Philosophy Reminder

Every line of code must answer these questions:

1. **Is it necessary?** - Can we achieve the goal without it?
2. **Is it lean?** - Could it be simpler or smaller?
3. **Is it fast?** - Have we measured the performance impact?
4. **Is it safe?** - Does it protect users from mistakes by default?
5. **Is it maintainable?** - Will future maintainers understand it?

If the answer to any question is "no", the code needs revision.

---

**Remember**: The goal is not to write code quickly. The goal is to write code correctly, performantly, and maintainably. Quality over speed. Always.

---

**Document Version**: 1.0
**Last Updated**: 2025-12-27
**Maintained By**: Baryo.Dev Architecture Team
