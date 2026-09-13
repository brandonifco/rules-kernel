# 0001 — Kernel scope and layering

## Status

Accepted — 2026-09-13. Governs what may enter `RulesKernel` and how the assemblies depend
on one another.

## Context

Two deterministic rules engines were built before this kernel existed: one over a tabletop
combat SRD, one over a commercial RPG rulebook. Both independently grew an `IRandomSource`,
a scripted test double, a dice layer, an ordered-results convention, and a layered assembly
graph. The second corrected several mistakes of the first.

Because each engine owned its own copy, none of those corrections reached the other. The
older engine still rolls through `System.Random`; the newer one pins a reference-verified
generator and records which algorithm produced a sequence. That divergence is not a
maintenance annoyance, it is the whole problem: a copied foundation cannot be fixed once.

## Decision

The kernel is a **versioned package that engines reference**, never a template they copy.
A correction made here reaches every engine through a version bump.

Its scope is three concerns and nothing else: **identity** (what makes two runs
comparable), **provenance** (where a rule came from), and **resolution** (how an engine
declines to answer). Everything else — corpus handling, domain vocabulary, mechanics,
serialization, persistence — lives above it.

The assembly graph is `RulesKernel <- RulesKernel.Randomness <- RulesKernel.Testing`, with
nothing pointing upward. `RulesKernel` additionally may not touch the filesystem, a clock,
the environment, the network, or randomness: it must resolve from its arguments alone.

`RulesKernel.Testing` sits under `tests/` because it is test-support rather than a layer of
an engine, but it is packaged, because engines need the same scripted source in their own
tests. No `src/` project may reference it.

## Alternatives considered

**A shared source-drop, copied per engine.** Rejected: it is the status quo that produced
the divergence above.

**A single monolithic package.** Rejected: it would force an engine over a statute or a
regulation to take a dependency on dice. See [0002](0002-randomness-is-optional.md).

**Enforcing layering only through reflection over compiled assemblies.** Insufficient alone:
the compiler omits references a compilation does not use, so those assertions pass
vacuously until real code crosses a boundary. The declared `ProjectReference` graph is
checked instead, exactly, by `tools/repo-checks.py --only layering`; the reflective tests
remain as a second net.

## Consequences

Engines gain a supported upgrade path and lose the freedom to quietly diverge from the
foundation. Breaking changes to kernel types become versioning events with real cost, which
is the correct incentive for types whose whole purpose is compatibility.

An undeclared project in `src/` or `tests/` fails the layering check rather than being
ignored, so adding a layer is a deliberate act.
