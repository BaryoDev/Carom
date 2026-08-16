# Contributing to Carom

Carom is a zero-dependency resilience library for .NET. Contributions are welcome, and this
document is the whole deal — what's expected, what gets merged, and what gets someone shown the
door.

Read the ["What this project does not accept"](#what-this-project-does-not-accept) section before
opening anything. It is short and it is enforced.

## Claiming an issue

**Claims need a maintainer's approval before you start.** This is a change from how the repository
used to work, where `/take` assigned you instantly.

1. Comment `/take` on the issue you want.
2. A bot labels it `claim: awaiting approval` and says your request is in.
3. A maintainer replies `/approve` — you're assigned, and it's yours.

Approval is usually a formality and usually quick. It exists so that issues are not claimed in bulk
by people who have no intention of doing the work, which leaves them looking taken to everyone else.
Expect a reply within a few days. If nobody has answered in a week, say so on the thread.

Maintainers who comment `/take` are assigned immediately, without the approval step.

**Do not open a pull request for an issue that is assigned to someone else.** If an issue has been
assigned for a long time and looks stalled, ask on the thread rather than racing the person who
holds it.

Once approved: no deadline, and no hard feelings if you change your mind — just say so and someone
else can pick it up. A draft PR early is very welcome if you'd like eyes on the direction before you
polish it.

## What a good pull request looks like

Most issues here, especially those labelled `good first issue`, already contain the diagnosis and
often the exact code to write. That is deliberate — the analysis is the part that needs repository
context, and it has been done for you. **The contribution is the remaining work**, which is:

- the change itself
- tests that fail before it and pass after it
- evidence that the change does what it claims

Restating an issue's "What should change" section back to the thread is not a contribution and does
not reserve the issue. Neither is a pull request that changes the described lines and nothing else,
when the issue asked for more — several issues here ask for a sweep across similar code, a guard so
the bug cannot silently return, or a mutation check proving a test still catches the fault it exists
to catch. Read the whole issue, including the section about what will cost you an afternoon.

Before you push:

```bash
dotnet build -c Release
dotnet test -c Release --no-build
```

Both must be clean. If your change is performance-sensitive, include BenchmarkDotNet numbers from
before and after — see `benchmarks/Carom.Benchmarks` and `docs/BENCHMARKS.md`.

## Coding standards

These are not negotiable in this repository, and a PR that ignores them will be sent back:

- **No LINQ in hot paths.** Use `for` loops.
- **Zero allocations on hot paths.** Prefer `struct` and `Span<T>`; avoid closures.
- **Lock-free.** State updates use `Interlocked`, never `lock`.
- **Zero external dependencies** in core packages. This is a headline feature of the library.
- **Expression Trees over Reflection** for meta-programming.
- **xUnit**, Verdict style.
- **MPL-2.0 licence headers** on every source file.
- **SemVer**, with manual version bumps only.

`CLAUDE.md` has the architectural detail — state classes, store classes, the builder pattern, and
how the patterns compose.

## Licensing

Carom is MPL-2.0. CLA Assistant will ask you to sign on your first pull request. It takes about
thirty seconds and **you keep copyright of your work**.

## What this project does not accept

**Carom does not pay for contributions. This is a flat no, and it is not the opening of a
negotiation.** Do not quote a price for fixing an issue, do not invite the maintainers to hire you,
and do not make starting work conditional on payment. Carom is a volunteer project: there is no
budget, no procurement process behind the issues, and no rate at which the answer becomes yes. This
applies on every channel — issues, pull requests, discussions, email, and direct messages alike —
so moving the same offer somewhere quieter is not a way around it.

Offers of this kind will be declined and the comment may be removed. This is not a judgement of
freelancing as a way to earn a living; it is simply not what this project is. Contribute unpaid and
you are welcome here on exactly the same terms as everyone else.

**Do not use the tracker for solicitation of any other kind**, including recruiting, promoting a
product or service, or driving traffic somewhere else.

**AI assistance is allowed. Unreviewed AI output is not.** Use whatever tools you like, but you are
the author: you are expected to understand every line you submit, to have built and tested it, and
to be able to answer questions about it. A pull request whose description or diff makes clear that
nobody read it before it was sent will be closed without a detailed review.

**Do not open low-value pull requests to farm contribution counts** — whitespace churn, unrequested
reformatting, speculative dependency bumps, or typo fixes bundled across unrelated files. A genuine
typo fix in prose is fine and welcome; a hundred of them across the repository in one PR is not.

**Do not claim issues in bulk.** Take one, finish it or hand it back, then take another.

## Grounds for blocking

Maintainers may block an account from this repository, without further warning, for any of the
following. These are the standard the project holds people to, and they apply from the date this
document was published.

1. **Continuing after a maintainer has declined.** One offer, one pitch, one suggestion is fine, and
   being told no is a complete answer. Repeating it after a decline — on the same thread or by
   moving to another issue — is grounds on its own.
2. **Commercial solicitation after being pointed at this policy.** The first time is a
   misunderstanding, and it gets a link to this section. There is no second time.
3. **Bulk automated commenting** — near-identical comments posted across multiple issues in a single
   pass, whether by a script or by hand.
4. **Bulk issue claims** left unworked, which make open issues look taken and deter people who would
   have done them.
5. **Harassment, abuse, or discriminatory language**, toward anyone, in any thread. Immediate, no
   warning.
6. **Deception** — misrepresenting who you are or who you work for, passing off someone else's work
   as your own, or falsely claiming an affiliation or endorsement.
7. **Malicious code** in a pull request, including anything that exfiltrates data, adds an undeclared
   network call, or tampers with the build or release workflows. Immediate, and reported to GitHub.
8. **Persistent bad-faith argument** — reopening a settled decision repeatedly, or demanding
   maintainer time after being asked to stop.

Blocking is a last resort, and it is about conduct on this repository rather than about anybody's
worth as an engineer. Where a first offence is plausibly a misunderstanding, expect a link to this
document instead of a block. A blocked account can appeal by email to the address in
[`docs/SECURITY.md`](docs/SECURITY.md).

To be explicit, because it matters: **this policy is not retroactive.** Behaviour that predates its
publication gets a pointer to the relevant section, not a block.

## Reporting a security issue

Do not open a public issue for a vulnerability. Follow [`docs/SECURITY.md`](docs/SECURITY.md).
