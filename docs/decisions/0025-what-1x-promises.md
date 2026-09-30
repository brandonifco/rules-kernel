# 0025 — What 1.x promises

## Status

Accepted — 2026-09-30. Written for the 1.0.0 release (#74). Records what the kernel commits to
through the 1.x line, and what it does not.

## Context

Before 1.0, compatibility was a courtesy: three source-breaking changes shipped unrecorded
before the public API analyzer existed, and the pre-1.0 review ([0022](0022-the-pre-1.0-surface-review.md))
spent its one rename deliberately. From 1.0.0 an engine takes a 1.x version bump expecting
nothing to break, and the kernel's product is replay identity. So "compatible" has to cover
meaning as well as shape, and it has to say where it stops.

## Decision

Within 1.x, a minor or patch release of `RulesKernel`, `RulesKernel.Randomness` or
`RulesKernel.Testing` does not:

1. **Remove or reshape public API.** `PublicAPI.Shipped.txt` in each project is the list.
   Additions are allowed. Removals and signature changes wait for 2.0.
2. **Change equality.** Identity components compare as documented: identifiers ordinally,
   baselines sequentially ([0019](0019-replay-identity-is-necessary-not-sufficient.md)),
   content hashes case-insensitively. Two values that compared equal under 1.0 compare equal
   under every 1.x.
3. **Accept less, except to close a hole.** A constructor that validates keeps rejecting what
   it rejected. It may start rejecting a value that was never meaningful, as 0019 did for
   derivation spellings, only with a decision record and a calibration showing no known
   consumer is affected.
4. **Change a draw.** `Pcg32` under `pcg_setseq_64_xsh_rr_32`, and the number of raw values
   `UniformInt` consumes for a given sequence, are fixed. The reference vectors are the
   contract ([0005](0005-pinned-pseudorandom-algorithm.md)). A different generator is a new
   `RandomAlgorithmId`, never a change to this one.
5. **Open a closed set.** `UnresolvedReason` stays five values
   ([0004](0004-unresolved-results-and-the-totality-burden.md)), and `Resolution<T>` stays two
   cases ([0023](0023-resolution-is-closed-at-construction.md)).
6. **Drop a target framework.** net8.0 and net10.0 are carried through 1.x, including past
   their end of support ([0008](0008-multi-targeting-so-adoption-is-not-an-upgrade.md)).

For `RulesKernel.Analyzers`, a diagnostic ID is permanent: it is never reused or renumbered
([0011](0011-shipping-a-determinism-analyzer.md)). What a rule reports is not frozen. A 1.x
release may add a rule or widen one, because an analyzer that finds more is doing its job.
Every rule stays a warning by default, so a bump adds findings rather than failing a build,
unless the engine has chosen warnings-as-errors.

Not promised, in any package: `ToString` output, exception messages, and `GetHashCode` values,
which were never replay-stable and say so.

## Consequences

A change that would break one of these waits for 2.0, and there is no 2.0 planned. The public
API analyzer enforces 1 mechanically. The reference vectors and their weekly re-derivation
enforce 4. The rest are review obligations, and this record is what a reviewer checks against.
