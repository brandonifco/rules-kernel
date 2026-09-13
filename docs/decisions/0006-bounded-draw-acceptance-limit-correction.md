# 0006 — Correcting the bounded-draw acceptance limit

## Status

Accepted — 2026-09-13. Amends the rejection-sampling rule in
[0005](0005-pinned-pseudorandom-algorithm.md). Changes observable draw counts for some
bounds, so it is recorded as a compatibility event rather than as a fix.

## Context

`UniformInt.Below` computed its acceptance limit as
`uint.MaxValue - (uint.MaxValue % exclusiveBound)`, justified in a comment by the claim
that 2^32 is not a multiple of any bound above 1, so working against `uint.MaxValue`
(2^32 - 1) gave the same answer as working against 2^32.

**The claim is false.** Every power of two divides 2^32. The consequences, measured:

| bound | values accepted | values acceptable | needlessly rejected |
|---|---|---|---|
| 1 | 4294967295 | 4294967296 | 1 |
| 2 | 4294967294 | 4294967296 | 2 |
| 6 | 4294967292 | 4294967292 | 0 |
| 20 | 4294967280 | 4294967280 | 0 |
| 65536 | 4294901760 | 4294967296 | 65536 |
| 2147483648 | 2147483648 | 4294967296 | 2147483648 |

At a bound of 2^31 the entire draw space maps perfectly with no rejection possible, and
the old formula rejected half of every draw.

Two things this was **not**. It was never a distribution bug: the limit is a multiple of
the bound by construction, so every outcome always received exactly the same number of raw
values. And it was never wrong for a bound that does not divide 2^32 -- at 6 and 20 the
old formula is already optimal.

What it got wrong is the number of draws consumed, and [0005](0005-pinned-pseudorandom-algorithm.md)
states that draw count is observable deterministic behaviour. An engine drawing at a
power-of-two bound would consume a different number of values before and after this change,
shifting every subsequent result.

The tests did not catch it because they recomputed the implementation's own expression to
derive their expectations. A test that reproduces the formula under test validates the code
against itself and can only ever confirm that the code does what it does.

## Decision

Compute the acceptance limit against the true draw space, in `ulong`:

    const ulong DrawSpace = 1UL << 32;
    ulong acceptanceLimit = DrawSpace - (DrawSpace % exclusiveBound);

The limit reaches 2^32 exactly when the bound divides it. That value does not fit in a
`uint`, which is why the comparison stays in `ulong` rather than being narrowed: no `uint`
can then equal or exceed the limit, so nothing is rejected -- the correct answer.

Tests derive their expectations from the definition rather than from the implementation,
and cover 1, 2, 6, 20, 2^16, 2^31 and `uint.MaxValue` explicitly.

## Other compatibility changes shipping alongside

Recorded here rather than left unstated, because this repository holds that a compatibility
change is a decision. None of them was noticed while making the fixes; an adversarial review
compiled a 0.1.0-legal consumer against the new assemblies and found them.

- **`ReplayCompatibilityIdentity.SourceBaselines` changed from `IReadOnlyList<SourceBaselineId>`
  to `ImmutableArray<SourceBaselineId>`.** `ImmutableArray` exposes `Count` only as an explicit
  interface implementation, so `.Count` no longer binds and becomes `.Length`. Source-breaking.
- **`ReplayCompatibilityIdentity` now rejects two baselines naming the same corpus.**
  `[core@h1, core@h1]` constructed in 0.1.0 and throws now. Behaviour-breaking, deliberately.
- **`Resolution<T>`'s cases lost, and regained, their deconstructors.** Making the constructors
  internal required rewriting positional records as bodied ones, which silently dropped the
  compiler-generated `Deconstruct` -- breaking `is Resolution<T>.Resolved(var v)`, the exact
  use the type documentation recommends. `Deconstruct` is now written explicitly and pinned by
  a test. `with { Value = ... }` remains unavailable, which is intended: the properties are
  get-only so a case cannot be rebuilt around a substituted payload.

## Consequences

**This is a replay-compatibility event for any engine drawing at a bound that divides 2^32.**
Such an engine consumes fewer raw values per draw after this change and will produce a
different sequence from the same seed.

No such engine exists. The only consumer at the time of this decision is Deckard, which
draws exclusively at bound 6, where the old and new formulas are identical. `RulesKernel`
0.1.0 is published and immutable; this ships in 0.2.0.

The false comment mattered more than the defect. A reader checking the reasoning would have
been satisfied by an argument that does not hold, in a file whose correctness rests on
exactly that kind of argument being checkable.
