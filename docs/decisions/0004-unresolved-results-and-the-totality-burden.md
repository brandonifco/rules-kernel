# 0004 — Unresolved results, and where the totality burden sits

## Status

Accepted — 2026-09-13. Consolidates two decisions taken separately in a predecessor engine:
the closed reason vocabulary, and the later correction to which operations must be able to
return one.

## Context

Determinism is only half a useful contract. An engine that reproduces its outputs exactly
but invents an answer for a rule it has not implemented is reproducibly wrong, and worse
than one that admits the gap: the caller has no signal that anything is missing.

The predecessor's first attempt required *every* resolution operation to return a
resolved-or-unresolved union. Review of the first two operations to ship found both already
violated it, because both were genuinely total — every valid input had exactly one answer in
the corpus — and the union was pure ceremony that callers unwrapped without thinking. A
requirement that is universally applied and routinely ignored trains people to ignore it.

## Decision

An operation that cannot resolve a rule returns an explicit **domain result** carrying one
of a closed set of reasons. It never approximates, never returns a default, and never
throws for this case — being unable to resolve a rule is an ordinary, enumerable outcome of
a partial engine, not a defect, and it belongs in the return type where a caller must
acknowledge it.

The reason vocabulary is closed at five values: `UnsupportedRule`,
`RequiresInterpretation`, `OutsideCurrentScope`, `UnsupportedInteraction`,
`MissingRulesData`. Adding a sixth means superseding this decision, not extending the enum
quietly. The five are about the *engine's relationship to its corpus*, not about subject
matter: an unimplemented tabletop manoeuvre and an unimplemented tax election are both
`UnsupportedRule`.

**Totality determines the union, not universality.** An operation *may* return its value
type directly when, for every valid input in its domain, the corpus determines exactly one
answer. It *must* return `Resolution<T>` when any input can reach a rule that is
unsupported, out of scope, or ambiguous. The burden sits on the operation returning a bare
value: totality is the claim that must be justified, and the union is the default. An
implementer who cannot state why an operation is total uses the union.

Every `UnresolvedResult` carries a `SourceLocator`. A gap without a citation is not
actionable, so the constructor rejects one.

## Alternatives considered

**Exceptions for unresolved rules.** Rejected: exceptions are for defects, and using them
here means the expected behaviour of a partial engine travels on the error path, where it
is routinely swallowed.

**Nullable returns.** Rejected: `null` carries no reason and no locator, which are the two
things that make a gap actionable.

**Keeping the universal-union requirement.** Rejected for the reason above — it was already
being violated by correct code.

## Consequences

`Resolution<T>` is a closed hierarchy with a private constructor, so `Match` and a `switch`
over it are exhaustive by construction. `Match` requires both handlers, which is what stops
an unresolved outcome from being silently dropped at a call site.

Reviewers gain a specific question to ask of any operation returning a bare value: why is
this total? That question is the decision's real enforcement mechanism; no check can answer
it.
