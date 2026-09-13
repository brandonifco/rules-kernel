# 0002 — Randomness is optional, and dice are not kernel vocabulary

## Status

Accepted — 2026-09-13.

## Context

Both predecessor engines resolved tabletop mechanics, so both treated a random source as
the foundation everything else stood on. That is an accident of subject matter. A rules
engine over a statute, a regulation, a contract, or a deterministic board game resolves
every question without drawing a single random value; its determinism is about ordering,
versioning and explainability, not about reproducible dice.

A kernel that makes randomness mandatory excludes those corpora, or forces them to carry a
generator they never call and an algorithm identity they cannot meaningfully populate.

Separately, the vocabulary matters. "Die", "dice pool" and "roll" are tabletop words. A
kernel that names its uniform-draw primitive `D6` has quietly declared what kind of rules
it is for.

## Decision

Randomness is an **optional package**, `RulesKernel.Randomness`. An engine that resolves no
random outcome references `RulesKernel` and never takes the dependency.

`RandomAlgorithmId` nonetheless lives in the kernel, not the randomness package, because it
is part of replay *identity*: an engine must be able to state that it consumes no
randomness, and that statement belongs in the identity it compares.
`ReplayCompatibilityIdentity.RandomAlgorithm` is therefore nullable, with `null` meaning
"this engine consumes none" — distinct from any named algorithm, and surfaced as
`IsDeterministicWithoutRandomness`.

The randomness package provides `UniformInt`, deliberately vocabulary-free: "uniform
integer below a bound", not "die". Dice, pools, exploding dice, hit counting and every other
tabletop idiom belong in a domain pack layered above it.

## Alternatives considered

**Ship `Die`/`DicePool` in the randomness package.** Rejected on vocabulary grounds. The
proven rejection-sampling logic is preserved in `UniformInt`, generalized from a fixed six
faces to any bound; a tabletop pack can wrap it in dice vocabulary in a dozen lines.

**A `RandomAlgorithmId.None` sentinel instead of a nullable.** Rejected: a struct's implicit
parameterless constructor already produces an unnamed value, and adding a second
"absent" representation would mean two ways to spell the same thing. The identity's
constructor rejects the struct default explicitly and tells the caller to pass `null`.

**Make the kernel depend on randomness and let unused code be trimmed.** Rejected: the
dependency is a statement about what an engine is, not a size concern.

## Consequences

A legal or regulatory engine can adopt the kernel without inheriting tabletop assumptions.
A tabletop engine takes one extra package reference and a domain pack.

`ReplayCompatibilityIdentity` gains a nullable component, and every consumer must decide
what it means for their engine — which is the intended forcing function.
