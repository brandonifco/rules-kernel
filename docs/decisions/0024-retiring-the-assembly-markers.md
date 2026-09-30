# 0024 — Retiring the assembly markers

## Status

Accepted — 2026-09-30. Found by the 1.0 public API freeze review (#74). Removes public API
shipped in 0.3.0.

## Context

`RulesKernel`, `RulesKernel.Randomness` and `RulesKernel.Testing` each shipped a public static
`AssemblyMarker` class with one member, `Assembly`. Each one's documentation gives the same
reason: it exists so the architecture tests can reflect over the assembly and check its
dependency direction.

That reason ended with #54. The architecture tests now read the `.deps.json` the SDK writes,
because reflecting over referenced assemblies passed vacuously, and nothing references a
marker. No consumer does either: faa-part-107, hoyle-backgammon, srd-52-combat, SRD_Combat,
reykholt, hallertau, rules-factory and rules-api were searched.

## Decision

All three are removed before 1.0. From 1.0, a public type is one the kernel will carry through
the whole 1.x line. A type whose only purpose is gone, and whose documentation states a purpose
that is no longer true, should not start that commitment. Removing it later would take a major
version.

## Consequences

Source- and binary-breaking for a consumer that names a marker, and none does. The public API
baselines record the removals. A consumer that wants an assembly handle for its own tests has
`typeof(SourceLocator).Assembly`, or any other type it already uses.
