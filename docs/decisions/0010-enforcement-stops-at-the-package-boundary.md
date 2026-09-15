# 0010 — Enforcement stops at the package boundary

## Status

Accepted — 2026-09-14. No consumer-facing enforcement ships. If it is ever built, it is a
separate opt-in package.

## Context

`tools/repo-checks.py` enforces this repository's determinism invariants: no
`Random.Shared`, no ambient clock, no environment reads, no concurrency, no filesystem in
the kernel. Nine checks, backed by 105 tests in `tools/tests/`.

None of it follows the NuGet package. The kernel can be spotless while an engine that
references it calls `Random.Shared`, `DateTime.UtcNow` and `Task.Run` throughout, and
nothing notices. Two engines currently depend on the kernel; neither inherits a single
check.

The README already says so — the kernel supplies primitives for building a deterministic
engine and cannot make an engine deterministic. The question this record answers is whether
that gap should be closed rather than only documented.

## Decision

The kernel ships no consumer-facing enforcement. Its scope stays the three concerns
[0001](0001-kernel-scope-and-layering.md) names: identity, provenance, resolution.

If a determinism analyzer is ever built, it is `RulesKernel.Analyzers` — a separate package
a consumer opts into, never a dependency of `RulesKernel`.

## Alternatives considered

**An analyzer bundled into `RulesKernel`.** Rejected on the strength of
[0008](0008-multi-targeting-so-adoption-is-not-an-upgrade.md). A Roslyn analyzer pins
`Microsoft.CodeAnalysis.CSharp`, which ties to the consumer's compiler. Hanging that
constraint on the dependency-free floor would re-create the exact lockout 0008 was written
to end: SRD_Combat pins SDK `8.0.129` with `rollForward: disable`, and adopting a library
must not become an SDK upgrade. [0002](0002-randomness-is-optional.md) already established
the shape for an optional concern — its own package, referenced by the engines that want it.

**A shared `repo-checks` an engine opts into.** Rejected. It is copied rather than
referenced, which is the precise failure [0001](0001-kernel-scope-and-layering.md) exists to
end. A correction to a copied check is stranded in whichever engine happened to make it.

**Leaving this unrecorded.** Rejected: that is the state that produced the question twice.

## Correcting one argument for building it

The case for an analyzer has been made on the grounds that
`Microsoft.CodeAnalysis.PublicApiAnalyzers` already proves the pattern in this repository.
It does not. All three references — in `src/RulesKernel`, `src/RulesKernel.Randomness` and
`tests/RulesKernel.Testing` — carry `PrivateAssets="all"`, which deliberately stops the
analyzer at this repository's own build. What is proven is that the kernel *consumes* an
analyzer, walled off from consumers. It has never shipped one.

Consuming and authoring are different commitments. Shipping an analyzer means owning a
diagnostic ID namespace permanently, tracking a Roslyn compatibility matrix across both
target frameworks, and answering for every consumer build the diagnostics break.

## On agnosticism

An analyzer would not violate the first governing principle. That principle is about
subject-matter vocabulary — `UniformInt`, not `D6`. An analyzer naming `Random.Shared`,
`DateTime.UtcNow` and `Parallel.For` carries no subject matter, and there is no domain word
available for it to smuggle in.

The real objection is a different one, and it is a scope objection: an analyzer is a claim
about how a consumer writes its own code, which is a fourth concern beside identity,
provenance and resolution. That claim may still be worth making. It is not free, and it
should not be adopted by assuming the agnosticism question was the only one.

## Consequences

An engine's determinism remains the engine's own business and its own checks'. The README's
statement of that limit is load-bearing and must keep standing; it is not a placeholder to
be quietly removed once an analyzer exists.

[0009](0009-what-the-source-blacklists-do-not-prove.md) names semantic analysis as the only
mechanism that closes a class of bypass rather than an instance. That remains true, and this
decision does not dispute it — it says the kernel is not yet the right place to ship one.

## What would change this

- **The factory.** If the factory emits determinism checks with the rails it produces, an
  analyzer here is redundant for every engine the factory builds, and the gap narrows to
  hand-written engines. If the factory does not, the gap is the whole population and the
  case for `RulesKernel.Analyzers` is materially stronger.
- **A third-party consumer.** The present audience is this author's own engines, where
  review reaches every one of them. That reasoning has already failed once, in 0008, on the
  assumption that a shared SDK pin made multi-targeting unnecessary.
- **A real incident.** An engine shipping reproducibly-wrong results because it consumed
  ambient entropy the kernel could have flagged.
