# 0015 — Calibrating the rule set before it freezes

## Status

Accepted — 2026-09-15. The `v0.3.0` tag is held. Supplements
[0013](0013-the-analyzer-ships-when-its-rules-cover-what-it-claims.md), which deferred
publishing until the rules covered what the package claims; this defers it again, for a
different reason found by measurement rather than by reading.

## Context

Everything about the analyzer was verified against fixtures this repository wrote. 358 tests,
two package probes, a consuming project built from a real `.nupkg`. None of that answers the
question that matters once a diagnostic ID is permanent: **do these rules say true things
about code somebody else wrote?**

`SRD_Combat` is the engine [0008](0008-multi-targeting-so-adoption-is-not-an-upgrade.md)
exists to serve — 372 C# files, `net8.0`, SDK `8.0.129` with `rollForward: disable`, a
tabletop combat engine, which is to say precisely the domain where entropy, ordering and
hashing are load-bearing. It does not reference the kernel yet.

The analyzer was packed locally and built against an exported copy of that tree. Nothing in
that repository was modified: `git archive HEAD` produced a pristine export, the package
reference was added there, and the export was discarded.

One fidelity limit, stated because it bounds the conclusion: SDK `8.0.129` is not installed
here, so `global.json` was neutralised and the build ran on the pinned .NET 10 SDK targeting
`net8.0`. That exercises the rules against real code, which is the point, but it does *not*
independently confirm the Roslyn 4.8 pin loads under the consumer's own compiler.

## What it found

26 unique findings across 15 projects.

| Rule | Unique findings | What they were |
|---|---|---|
| RK0001 ambient entropy | 10 | 5 `Random.Shared`, 3 `Guid.NewGuid` in tests, **2 inside a deliberately seeded source** |
| RK0003 ambient environment | 10 | **8 `Environment.NewLine`**, 1 `Environment.GetFolderPath`, 1 `Environment.Exit` |
| RK0007 unordered materialization | 5 | `Dictionary`, `HashSet` and `KeyCollection` materialized into ordered results |
| RK0004 concurrency | 1 | `Task.Run` in a test |
| RK0002, RK0005, RK0006 | 0 | did not fire |

**RK0007 is the rule that came out best.** Five findings, no obvious noise, in content parsing
and character resolution — exactly where iteration order can reach a result. The narrow scope
[0014](0014-warning-where-unordered-becomes-ordered.md) chose looks calibrated correctly on
the first real corpus it met.

**RK0001 fires on the pattern it recommends adopting.** `SeededRandomSource(int seed)` holds
`new Random(seed)` and calls `_random.Next(...)`. Its own doc comment says the interface must
never touch `Random.Shared` — the author is doing the disciplined thing — and the diagnostic
tells them `'Random..ctor' draws from ambient entropy; take a seeded source as an argument
instead`. They *are* the seeded source.

The finding is arguably still correct: `System.Random`'s algorithm is not guaranteed stable
across runtime versions, which is the whole reason [0005](0005-pinned-pseudorandom-algorithm.md)
pinned PCG32 rather than trusting the framework. But the reason the analyzer gives is false,
and a consumer who reads a false reason concludes the analyzer is wrong and suppresses it.
A right finding with a wrong explanation is still a defect.

**RK0003 is mostly `Environment.NewLine` in console output.** Eight of ten. `Display.cs`
writes a line to a terminal; `'Environment.NewLine' reads ambient machine state; a rules
result must resolve from its arguments` is not true of it, because it is not a rules result.
`Environment.Exit` is worse: the message says it *reads* ambient state, and it reads nothing.

This repository's own `determinism` check bans `Environment.` as a whole-type prefix, and the
comment there explains why — an earlier rule naming three members let `NewLine` through. That
reasoning is sound **for the kernel**, whose every line is a rules result. It does not
transfer to a consumer's entire application, which has a console UI.

**Half the rule set was not exercised.** RK0002, RK0005 and RK0006 fired zero times. That is
not evidence they are wrong. It is evidence they are unvalidated, and RK0006 is the one whose
split from RK0003 was already in question.

**Adopting the analyzer breaks the build.** The first run failed with 3 errors, not warnings,
and compilation stopped before the other projects were analysed: `SRD_Combat` sets
`TreatWarningsAsErrors`. Warning severity protects a consumer who takes a *version bump*; it
does not protect one who takes the package for the first time. That is worth saying out loud
in the README rather than letting an adopter discover it.

## Decision

The `v0.3.0` tag is held until RK0001's and RK0003's messages and scope are fixed.

Both are the kind of defect that freezing makes permanent. An ID's *meaning* is the part that
cannot be revised later without silently changing what an existing suppression hides, and both
of these are meaning defects rather than message typos: RK0001 claims a seeded generator is
ambient entropy, RK0003 claims a line separator is a rules result.

## Consequences

The calibration cost about ten minutes and found two defects that 358 tests did not, because
every one of those tests was written by the same people who wrote the rules. That asymmetry is
the argument for doing this again before any future ID freezes, and the reason this record
exists rather than a note in a pull request.

`RulesKernel.Analyzers` should be pointed at a real, unfamiliar corpus before each release
that adds a rule. One engine is not many, and `SRD_Combat` is the only one available here.
