# Rules Kernel

The ruleset-agnostic floor that deterministic rules engines are built on.

Given the same ruleset version, the same pinned corpora, the same initial state and the
same ordered decisions, an engine can produce the same outcomes and the same ordered
history — on any machine, on any run. The kernel does not make that true of an engine; it
owns no execution, no persistence and no mechanics. What it provides is the part an engine
cannot safely invent for itself: identity you can compare, provenance you can check, a
replay-stable generator, and a way to say "I cannot resolve this" that a caller must handle.
Whether an engine honours the contract is the engine's business, and its own checks' —
with one exception an engine opts into: `RulesKernel.Analyzers` carries the determinism
diagnostics into the consumer's own build
([ADR 0011](docs/decisions/0011-shipping-a-determinism-analyzer.md)).

What the kernel does not do is stop an engine from guessing. An operation that returns a bare
value can return a guess, and nothing here can tell. Once an operation declares itself
potentially non-total by returning `Resolution<T>`, the kernel makes the unresolved case
something a caller has to name before it can reach a value
([decision 0018](docs/decisions/0018-resolution-enforces-handling-not-honesty.md)).

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
| `RulesKernel.Analyzers` | compile-time diagnostics for ambient non-determinism | nothing |

`RulesKernel.Analyzers` is a build asset, not a reference: it ships no `lib/`, depends on
nothing, and an engine that takes it gets RK0001-RK0005 and RK0007 against ambient entropy,
clock, ambient machine state including culture and time zone, concurrency, replay-unstable
hashing, and unordered collections materialized into ordered results, in its *own* code.
RK0006 is a permanent gap; it was folded into RK0003 before anything shipped
([decision 0016](docs/decisions/0016-retiring-rk0006-into-a-narrowed-rk0003.md)).
It is opt-in and deliberately not a
dependency of `RulesKernel` ([decision 0011](docs/decisions/0011-shipping-a-determinism-analyzer.md)).

### What adopting it looks like

Expect findings on an existing codebase, and expect the build to fail on the first one.

The diagnostics are warnings, which protects an engine taking a *version bump*. It does not
protect one adding the package for the first time: a project with `TreatWarningsAsErrors`
— which this repository sets, and which most disciplined .NET projects set — turns every
finding into an error immediately.

Measured, not estimated. Pointing it at a real engine of 372 C# files
([decision 0015](docs/decisions/0015-calibrating-the-rule-set-before-it-freezes.md)) produced
**17 findings**: 10 RK0001, 5 RK0007, 1 RK0003, 1 RK0004. Adding the package with that
engine's own settings failed the build after **6 of them**, in the first project compiled —
the rest were never reported, because compilation stopped.

That is the experience to plan for: not a clean build, and not even a complete list.

Adopt in stages instead. Turn every rule down to `suggestion` first, so one build shows the
whole picture, then raise them one at a time:

```ini
# In your engine's own .editorconfig, adopting on an existing codebase.
# Start here, read the full list, then promote rules to warning one at a time.
[*.cs]
dotnet_diagnostic.RK0001.severity = suggestion   # ambient or unpinned entropy
dotnet_diagnostic.RK0002.severity = suggestion   # ambient clock
dotnet_diagnostic.RK0003.severity = suggestion   # ambient machine state
dotnet_diagnostic.RK0004.severity = suggestion   # concurrency
dotnet_diagnostic.RK0005.severity = suggestion   # replay-unstable hashing
dotnet_diagnostic.RK0007.severity = suggestion   # unordered materialized into ordered
```

Test projects are worth a separate decision rather than a global one. Several of the
findings above were `Guid.NewGuid` for a temporary filename and a `Task.Run` in a test — true
statements about code whose determinism nobody is claiming. The analyzer is loaded by the
compiler and cannot know which projects are tests, so that judgement is yours:

```ini
# In a second .editorconfig beside your test projects: a test may construct
# exactly what the kernel may not.
[*.cs]
dotnet_diagnostic.RK0001.severity = none
dotnet_diagnostic.RK0004.severity = none
```

A finding is not automatically a bug. RK0001 on a seeded `System.Random` says the algorithm
is not guaranteed stable across runtime versions, which matters if a stored replay must
survive a framework upgrade and does not if it must not
([decision 0005](docs/decisions/0005-pinned-pseudorandom-algorithm.md)). Read the message; it
says which of those it means.

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
reproducibly wrong, and this is how an engine avoids that. It does not prevent it. Whether an
operation returns the union is the engine's decision and a reviewer's question — *why is this
total?* ([decision 0004](docs/decisions/0004-unresolved-results-and-the-totality-burden.md)).
What the type enforces begins after that decision: no path from a `Resolution<T>` to its value
skips the unresolved case, though a handler can still discard it on purpose
([decision 0018](docs/decisions/0018-resolution-enforces-handling-not-honesty.md)).

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
- **target-frameworks** — every packaged project still targets what
  [ADR 0008](docs/decisions/0008-multi-targeting-so-adoption-is-not-an-upgrade.md) committed
  to, and its lock file covers the same set. `dotnet restore --locked-mode` already fails
  when the two disagree; this catches a framework dropped from *both*, where they agree and
  the commitment is simply gone.
- **parseable** — every XML, YAML and JSON file parses. A malformed workflow is not an error
  on GitHub; it simply never runs.
- **text-hygiene** — UTF-8, no BOM, LF, one trailing newline, and no bidi controls,
  zero-width characters, or non-ASCII identifiers.

A check that examined nothing reports `skip`, never `ok` — and a skip fails the run, because
the exit code is the part the gate actually reads.

What these do **not** prove: the determinism and boundary checks are pattern matches over
source. Reflection, aliasing, extension methods and source generation defeat them. They
raise the cost of an accident; they are not a proof of absence.
[ADR 0009](docs/decisions/0009-what-the-source-blacklists-do-not-prove.md) records which
classes of bypass are known and deliberately unaddressed, and what would change that. The
module docstring in `tools/repo-checks.py` says so too, and `tools/tests/` holds 127 tests
that exist to show each check actually fails when it should.

Public API changes to the four packaged assemblies are tracked separately, by
`Microsoft.CodeAnalysis.PublicApiAnalyzers` — adding or reshaping a public member fails the
build until the baseline is updated deliberately. Which half of the baseline a declaration
sits in is the part that carries the obligation: `PublicAPI.Shipped.txt` is compatibility
debt owed to consumers, `PublicAPI.Unshipped.txt` is a draft still open for revision. A
release promotes one into the other, and `tools/release-checks.py` fails a tagged publish
while any `PublicAPI.Unshipped.txt` is non-empty. It is deliberately not part of
`validate.sh`: an unshipped baseline is the normal state of a branch in flight, and only a
release changes that.

## Where this sits

The kernel is step one of a larger separation: a corpus toolkit (adapters, locators,
boundary policy), domain packs (tabletop vocabulary, legal effective-dating), and a factory
that takes a plain-language ruleset and produces an engine built on all of them. Each is its
own repository with its own responsibility. See [docs/architecture.md](docs/architecture.md).

## Decisions

Recorded in [docs/decisions](docs/decisions/README.md), and cited from the code that
implements them.
