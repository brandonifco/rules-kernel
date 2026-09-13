# Rules Kernel

The ruleset-agnostic floor that deterministic rules engines are built on.

Given the same ruleset version, the same pinned corpora, the same initial state and the
same ordered decisions, an engine built on this kernel produces the same outcomes and the
same ordered history — on any machine, on any run. And where it cannot resolve a rule, it
says so explicitly instead of guessing.

The kernel is **referenced, not copied**. A correction made here reaches every engine built
on it through a version bump, rather than being stranded in whichever engine happened to
find the bug. That is the entire reason it lives apart from any engine.

## What it is for

Any body of rules with an authoritative written source: a tabletop RPG, a board game, a
statute, a regulation, a contract. The kernel privileges none of them. It contains no dice,
no page numbers, no subject-matter vocabulary of any kind.

## Packages

| Package | Contains | Depends on |
|---|---|---|
| `RulesKernel` | identity, provenance, resolution | nothing |
| `RulesKernel.Randomness` | PCG32, bias-free bounded draws | `RulesKernel` |
| `RulesKernel.Testing` | scripted test doubles | `RulesKernel.Randomness` |

Randomness is optional. An engine over a statute or a regulation resolves every question
without drawing a value, and never references it ([decision 0002](docs/decisions/0002-randomness-is-optional.md)).

## The three concerns

**Identity** — what makes two runs comparable.

```csharp
var identity = new ReplayCompatibilityIdentity(
    ruleset: new RulesetVersion("cfr-26-401k", 3),
    replaySchema: new ReplaySchemaVersion(1),
    sourceBaselines:
    [
        new SourceBaselineId("cfr-26", hash, asOf: new DateOnly(2019, 3, 14)),
        new SourceBaselineId("rev-proc-2019-20", otherHash, asOf: new DateOnly(2019, 5, 1)),
    ]);

identity.IsDeterministicWithoutRandomness;  // true -- no generator involved
```

Corpora are plural and ordered, and each carries the moment it was pinned as well as its
content hash. A hash proves two people read identical bytes; it does not say what those
bytes were, and "what did this rule say on this date" is the question a regulatory engine
exists to answer ([decision 0003](docs/decisions/0003-corpus-baselines-and-the-temporal-axis.md)).

**Provenance** — where a rule came from.

```csharp
new SourceLocator("core-rules", "printed p. 45 / PDF p. 57");
new SourceLocator("cfr-26", "§ 1.401(k)-1(b)(4)(ii)");
new SourceLocator("boardgame", "rule 4.2.1");
```

The citation's grammar belongs to the corpus's adapter. The kernel checks that one was
supplied and which corpus it points into; whether it is well-formed is the adapter's
question, and whether it points at the right passage is the reviewer's.

**Resolution** — how an engine declines to answer.

```csharp
return Resolution<int>.FromUnresolved(new UnresolvedResult(
    UnresolvedReason.RequiresInterpretation,
    "resolve the elective deferral limit for a mid-year plan amendment",
    new SourceLocator("cfr-26", "§ 1.401(k)-1(b)(4)(ii)")));
```

Five closed reasons, all about the engine's relationship to its corpus rather than about
subject matter. An engine that is reproducible but guesses at unimplemented rules is
reproducibly wrong; this is the half of the determinism contract that prevents it
([decision 0004](docs/decisions/0004-unresolved-results-and-the-totality-burden.md)).

## Verify it

```bash
./scripts/validate.sh full
```

One gate, and CI runs the same one rather than maintaining a separate recipe. It needs no
network beyond NuGet restore and no corpus of any kind: this is the kernel, and anyone who
can clone it can verify it.

## Invariants that are enforced, not merely documented

```bash
tools/repo-checks.py
```

- **layering** — the declared `ProjectReference` graph, read from the csproj files. Exact,
  and it fails on a project present on disk but absent from the declared graph.
- **core-boundary** — `RulesKernel` touches no filesystem, clock, environment, network, or
  randomness. It resolves from its arguments alone.
- **determinism** — no `Random.Shared`, `new Random()`, `DateTime.UtcNow`, `Guid.NewGuid()`,
  `Task.Run` or `.AsParallel()` anywhere under `src/`.
- **doc-references** — every repository document referenced from code or prose exists. A
  citation is a promise.
- **text-hygiene** — UTF-8, no BOM, LF, exactly one trailing newline.

A check that examined nothing reports `skip`, never `ok`.

## Where this sits

The kernel is step one of a larger separation: a corpus toolkit (adapters, locators,
boundary policy), domain packs (tabletop vocabulary, legal effective-dating), and a factory
that takes a plain-language ruleset and produces an engine built on all of them. Each is its
own repository with its own responsibility. See [docs/architecture.md](docs/architecture.md).

## Decisions

Recorded in [docs/decisions](docs/decisions/README.md), and cited from the code that
implements them.
