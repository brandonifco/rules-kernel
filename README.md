# Rules Kernel

The ruleset-agnostic floor that deterministic rules engines are built on.

Given the same ruleset version, the same pinned corpora, the same initial state and the
same ordered decisions, an engine can produce the same outcomes and the same ordered
history — on any machine, on any run. The kernel does not make that true of an engine; it
owns no execution, no persistence and no mechanics. What it provides is the part an engine
cannot safely invent for itself: identity you can compare, provenance you can check, a
replay-stable generator, and a way to say "I cannot resolve this" that a caller must handle.
Whether an engine honours the contract is the engine's business, and its own checks'.

Where it cannot resolve a rule, an engine built on these primitives
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

- **layering** — the declared dependency graph, read from every csproj in the repository at
  any depth, in either quote style, counting a `PackageReference` to a sibling package as
  the edge it is. A project on disk but absent from the declared graph is a failure.
- **core-boundary** — `RulesKernel` touches no filesystem, clock, environment, network, or
  randomness. It resolves from its arguments alone.
- **determinism** — no ambient entropy, clock, or concurrency in any *packable* project,
  which includes `RulesKernel.Testing`. Test projects are deliberately out of scope: a test
  may construct a clock in order to prove the kernel does not use one.
- **ordering** — no sorting an ordered result after the fact. The order is the evidence.
- **doc-references** — every repository file referenced from code or prose exists, including
  `ADR NNNN` shorthand. A citation is a promise. It does not check that the citation is the
  *right* document, only that it resolves.
- **solution-membership** — every project is in `RulesKernel.slnx`. One deleted line would
  otherwise drop a project from the build and the test run with no other symptom.
- **action-pins** — every workflow `uses:` is a 40-character commit SHA.
- **parseable** — every XML, YAML and JSON file parses. A malformed workflow is not an error
  on GitHub; it simply never runs.
- **text-hygiene** — UTF-8, no BOM, LF, one trailing newline, and no bidi controls,
  zero-width characters, or non-ASCII identifiers.

A check that examined nothing reports `skip`, never `ok` — and a skip fails the run, because
the exit code is the part the gate actually reads.

What these do **not** prove: the determinism and boundary checks are pattern matches over
source. Reflection, aliasing, extension methods and source generation defeat them. They
raise the cost of an accident; they are not a proof of absence. The module docstring in
`tools/repo-checks.py` says so too, and `tools/tests/` holds 105 tests that exist to show
each check actually fails when it should.

Public API changes to the three packaged assemblies are tracked separately, by
`Microsoft.CodeAnalysis.PublicApiAnalyzers` — adding or reshaping a public member fails the
build until the baseline is updated deliberately.

## Where this sits

The kernel is step one of a larger separation: a corpus toolkit (adapters, locators,
boundary policy), domain packs (tabletop vocabulary, legal effective-dating), and a factory
that takes a plain-language ruleset and produces an engine built on all of them. Each is its
own repository with its own responsibility. See [docs/architecture.md](docs/architecture.md).

## Decisions

Recorded in [docs/decisions](docs/decisions/README.md), and cited from the code that
implements them.
