# Contributing to Carom

## Claiming work

Comment `/take` on an issue to get it assigned. Open a PR from a feature branch; do not push to master.

## Ground rules

- **Zero dependencies in the core packages.** A PR that adds a NuGet reference to `Carom` or `Carom.Extensions` will be declined regardless of what it buys.
- **A bug fix needs a test that fails without it.** Either write the failing test first, or revert the fix and show the test go red. Say in the PR which production line you reverted.
- **Plain xUnit `Assert`.** No assertion libraries.
- **MPL-2.0 header on every source file** under `src/`. Copy the two lines from any existing file.
- **Public API changes** show up in `tests/Carom.ApiApproval.Tests`. Commit the updated approved file together with the change that caused it, and call out the change in the PR body.
- No em dashes in code, comments, or docs.

## Build and test

```bash
dotnet build -c Release
dotnet test
```

The full suite runs in about 90 seconds. Time-dependent tests use the injectable clock (`CushionState`/`ThrottleState` take a `Func<long>` timestamp); new time-dependent tests should too, not `Task.Delay`.
