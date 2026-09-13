# Architecture

`RulesKernel` is the ruleset-agnostic floor that deterministic rules engines are built on.
It is a **library engines reference**, not a template they copy. That distinction is the
reason this repository exists separately from any engine: a correction made here reaches
every engine built on it through a version bump, rather than being stranded in whichever
engine happened to discover the bug.

## Assemblies

```
RulesKernel              identity, provenance, resolution     depends on nothing
      ^
RulesKernel.Randomness   PCG32, bias-free bounded draws       depends on RulesKernel
      ^
RulesKernel.Testing      scripted test doubles                depends on RulesKernel.Randomness
```

Nothing points upward. `tools/repo-checks.py --only layering` enforces this by reading the
declared `ProjectReference` graph out of the csproj files, and it fails on any project
present on disk but absent from the declared graph — an undeclared project must not
silently escape enforcement.

The per-assembly `ArchitectureTests` are a second net and are **not yet load-bearing**: the
C# compiler omits assembly references a compilation does not actually use, so while no
assembly consumes another, `GetReferencedAssemblies()` returns nothing and those assertions
pass vacuously. They begin catching real violations as soon as code crosses an assembly
boundary. Until then the declared-graph check is the enforcement.

`RulesKernel.Testing` lives under `tests/` because it is test-support, not a layer of an
engine. It is packaged anyway, because an engine built on this kernel needs the same
scripted source in its own tests. No `src/` project may reference it, and the layering
check enforces that specifically.

## What the kernel is

Three concerns, and nothing else:

**Identity** — what makes two runs comparable. `RulesetVersion` (the implemented revision),
`ReplaySchemaVersion` (the shape of a recorded replay), `SourceBaselineId` (which corpus, at
what content hash, as of what moment), `RandomAlgorithmId` (which generator, if any), and
`ReplayCompatibilityIdentity`, which combines them and compares as a whole.

**Provenance** — `SourceLocator`, naming the corpus and carrying a citation whose grammar
belongs to that corpus's adapter. Page numbers, regulation designations, statute sections
and numbered board-game rules are all citations; none of them is privileged by the kernel.

**Resolution** — `UnresolvedReason`, `UnresolvedResult` and `Resolution<T>`: the contract by
which an engine says "I cannot resolve this, here is why, here is where the rule lives"
instead of guessing.

## What the kernel is not

No corpus handling, no extraction, no adapters. No dice, no pools, no thresholds, no
combat, no effective-dating arithmetic. No serialization, no persistence, no replay
execution. No subject-matter vocabulary of any kind.

Those belong above: corpus adapters in the toolkit, domain vocabulary in domain packs, and
mechanics in an engine's own `Rules` layer.

## The determinism contract

```
same ruleset version + same corpus baselines + same initial state + same ordered decisions
    (+ same random algorithm and state, where randomness is consumed)
        = the same outcomes and the same ordered history
```

Two halves, and the second matters as much as the first. An engine that is reproducible but
guesses at unimplemented rules is reproducibly wrong. `Resolution<T>` is how the second half
is enforced in the type system rather than in prose.

No ambient randomness, no ambient clock, no environment reads, no order-dependent
iteration. The first three are checked: `tools/repo-checks.py --only determinism` fails the
build on `Random.Shared`, `new Random()`, `DateTime.UtcNow`, `Guid.NewGuid()`, `Task.Run`,
`.AsParallel()` and a list of their relatives, across every packable project; `--only
core-boundary` additionally forbids the kernel itself from touching the filesystem, a clock,
the environment, the network, or randomness.

Order-dependent iteration is **not** mechanically checked, and saying otherwise would be the
kind of claim this document exists to avoid. `--only ordering` catches sorting an ordered
result after the fact, which is the failure mode that actually recurs. It does not catch
enumerating a `Dictionary` or a `HashSet`, or anything else whose order is an implementation
detail of the runtime. That one is a review obligation, not a gate.

Ordered results are ordered **by construction** — the sequence in which things actually
happened — never sorted afterwards. A sort applied to an ordered history destroys the
evidence that the history was deterministic in the first place.
