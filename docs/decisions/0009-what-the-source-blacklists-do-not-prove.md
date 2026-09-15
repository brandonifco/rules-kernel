# 0009 — What the source blacklists do not prove

## Status

Accepted — 2026-09-14.

## Context

The README lists this repository's checks under the heading "Invariants that are
enforced, not merely documented". Three of them — `determinism`, `core-boundary` and `ordering` — are pattern
matches over C# source text. They have no notion of what a symbol binds to.

An external review of 0.1.0 demonstrated a long list of ways past them. The cheap,
high-value holes were closed, and the patterns in `tools/repo-checks.py` still carry the
comments explaining why each was widened: `Guid.CreateVersion7` embeds a timestamp, so the
Guid rule names it alongside `NewGuid`; `TimeProvider` is banned as a whole type rather than
by member, because `TimeProvider.System` is the ambient clock wearing an abstraction;
`Environment.` is banned as a prefix, because an earlier rule naming three members let
`Environment.ProcessPath` and `Environment.NewLine` through; `Parallel.` and
`Task.Factory.StartNew` were added after only `Task.Run` was banned.

The rest were deliberately not chased. That decision is sound, and it was recorded in the
wrong place: the module docstring of `tools/repo-checks.py`, read only by someone already
inside the file. A reviewer asking whether this repository's determinism claim is true reads
the README's list of checks.

This repository's own standard is that a limit belongs where the claim is made. Two claims
have already been found false here and corrected for exactly that reason.

## Decision

The limit is stated where the claim is made. The README and `docs/architecture.md` point
here where they list the checks.

Known, deliberately unaddressed classes of bypass:

- **Reflection.** The banned symbol never appears as source text.
- **`using` aliases.** `using Clock = System.DateTime;` renames the receiver.
- **Extension methods that hide their receiver.** `value.Now()` names nothing banned.
- **A helper in another assembly.** The blacklist is scoped to projects this repository
  publishes; a call into anything else is invisible to it.
- **Source generation.** The generated tree is not on disk when the check runs.
- **Order-dependent iteration** over `Dictionary` or `HashSet`. These checks still do not
  see it, and cannot: order is a property of a type, and a text match has no types.
  [0014](0014-warning-where-unordered-becomes-ordered.md) closes it where it can be closed
  — RK0007 in the analyzer, over a *consumer's* code, when such a collection is materialized
  into an ordered one without an explicit sort. Inside this repository it remains a review
  obligation, because nothing here runs the analyzer over the kernel's own source.

They are unaddressed because each is defeated by renaming, and a blacklist over source text
cannot close the class — only the instance. Every new bypass is a new pattern, the list
never converges, and each widening raises the false-positive rate on legitimate code. The
only mechanism that closes a class rather than an instance is semantic analysis over the
compiled symbol graph.

## Consequences

The checks keep exactly the value they had. A blacklist catches the ways a rule gets
broken by accident and the ways it gets broken by someone who does not know the rule, on
every commit, in under a second. That is most of the real traffic. It is not a proof of
absence, and the README no longer lets a reader infer that it is.

The other nets are unchanged and are not a substitute: `ArchitectureTests` in
`tests/RulesKernel.Tests` and `tests/RulesKernel.Randomness.Tests` reflect over compiled
assemblies, `tools/tests/` holds 113 tests showing each check fails when it should, and
CLAUDE.md's first governing principle is explicit that ruleset-agnosticism is a review
obligation that nothing checks.

## What would change this

- A determinism analyzer over the symbol graph, which would close these classes rather than
  enumerate them. [0011](0011-shipping-a-determinism-analyzer.md) ships exactly that, as an
  opt-in package — but it analyses a *consumer's* code. These checks remain this
  repository's own net, and remain textual, so everything above still holds here.
- An actual accident of the kind the blacklist misses. The bypasses above are all
  deliberate acts. If one of them ever happens by accident, that is evidence the cheap net
  is in the wrong place, and this decision should be revisited rather than patched.
