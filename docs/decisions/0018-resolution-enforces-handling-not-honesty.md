# 0018 — Resolution enforces handling, not honesty

## Status

Accepted — 2026-09-15. Narrows the claims made for
[0004](0004-unresolved-results-and-the-totality-burden.md). No code or API change.

## Context

The README said an engine built on these primitives "says so explicitly instead of
guessing". `docs/architecture.md` said `Resolution<T>` enforces the second half of the
determinism contract "in the type system rather than in prose". Both claims are broader than
the type.

Here is what a type can reach. An operation that returns `int` for a rule it has not
implemented compiles, passes its tests, and replays byte for byte. It is exactly the
reproducibly-wrong engine that 0004 describes. `Resolution<T>` never sees it, because the
operation never returned one. 0004 already said so: whether an operation must return the
union is a reviewer's question, and "no check can answer it". The summary claims had grown
past the decision they cited.

## Decision

The kernel's guarantee about unresolved outcomes is stated in two parts, and only the second
is enforced.

1. **Declaring non-totality is the engine's claim.** An operation that can reach an
   unsupported, out-of-scope or ambiguous rule must return `Resolution<T>`
   ([0004](0004-unresolved-results-and-the-totality-burden.md)). No kernel type, check or
   analyzer rule verifies it. Documentation says *an engine can* decline explicitly, never
   that *it does*.
2. **Handling a declared gap is enforced.** Once `Resolution<T>` is returned, no member
   yields the value without the caller naming the unresolved case: `Match` requires both
   handlers, and positional or type-test matching names `Unresolved`. A caller can still
   discard it deliberately (`_ => 0`). The type does not prevent that. It puts the choice at a
   call site, where review can see it.

`Resolution<T>` stays exactly as small as it is. Neither part is a reason to add members, and
the gap in part 1 is not closed by an analyzer rule here. Recognizing a bare value that should
have been a union would take domain knowledge the kernel does not have.

## Consequences

README, `docs/architecture.md` and the XML documentation on `Resolution<T>` and
`UnresolvedResult` say part 2 and stop claiming part 1.

A consumer probe that wants evidence for part 1 has to supply it the way
`probes/RegulatoryProbe.Tests` does: enumerate the inputs that reach each unresolved branch
and assert on each one. That evidence is about one engine and does not generalize.
