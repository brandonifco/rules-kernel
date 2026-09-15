# 0011 — Shipping a determinism analyzer

## Status

Accepted — 2026-09-14. Supersedes
[0010](0010-enforcement-stops-at-the-package-boundary.md), which decided the opposite a day
earlier.

## Context

[0010](0010-enforcement-stops-at-the-package-boundary.md) recorded that no enforcement
follows the package, and gated any analyzer on three triggers: the factory, a third-party
consumer, or a real incident. None of them has fired. This record does not claim otherwise.

What changed is the decision, made deliberately rather than by an event: the gap 0010
described is real, it is the one limit on the phrase "a deterministic rules kernel" that a
consumer cannot close by reading, and the work to close it turned out to be bounded.

0010's reasoning is not discarded. Every constraint it identified is honoured below; what it
got wrong was the conclusion that those constraints made the thing not worth building. They
made it a *separate package*, which 0010 itself specified.

## Decision

`RulesKernel.Analyzers` ships. It is the fourth packaged assembly.

**Opt-in, and outside the stack.** It references no kernel type and no kernel project
references it. An engine that wants the diagnostics takes the package; one that does not is
unaffected, and neither can acquire the other transitively.

**netstandard2.0, against Roslyn 4.8.** An analyzer is loaded by the consumer's compiler, so
the repo-wide `net10.0` default would produce one no compiler loads. The Roslyn reference is
pinned to the *oldest* version it must run under — SDK 8.0.x, the pin
[0008](0008-multi-targeting-so-adoption-is-not-an-upgrade.md) exists to serve. Newer
compilers load a 4.8-targeted analyzer without complaint, so the low pin costs nothing and
raising it silently drops consumers.

**Warning, not error.** An analyzer that breaks a consumer's build the moment they take a
version bump teaches them to remove the package. Escalation is the consumer's to make, per
rule, in `.editorconfig` — or wholesale, as this repository's own gate does with
`-warnaserror`.

**Semantic, not textual.** Rules resolve symbols against the compilation.
[0009](0009-what-the-source-blacklists-do-not-prove.md) records that a `using` alias, a
receiver-hiding extension method, a local function or a generated tree all walk past a
pattern blacklist. Those four cases are asserted directly in
`tests/RulesKernel.Analyzers.Tests`, because they are the reason this is a package and not a
wider regex.

**Four rules, permanently numbered.** RK0001 entropy, RK0002 clock, RK0003 environment,
RK0004 concurrency. A diagnostic ID is a contract: a consumer suppresses by ID, so reusing
or renumbering one silently changes what their suppression means.
`AnalyzerReleases.Unshipped.md` makes adding an ID a deliberate act — RS2008 fails the build
otherwise.

## On the scope objection

0010's real objection was that an analyzer is a claim about how a consumer writes its own
code, and therefore a fourth concern beside identity, provenance and resolution
([0001](0001-kernel-scope-and-layering.md)).

That objection stands, and is answered by the package boundary rather than by disputing it.
`RulesKernel` still resolves from its arguments alone and still contains only the three
concerns. The fourth concern lives in an assembly an engine must ask for by name. This is
the same move [0002](0002-randomness-is-optional.md) made for randomness: the kernel does
not get broader, the family gets one more optional member.

## Consequences

The kernel can no longer say that nothing it publishes follows the package to a consumer.
The README's statement of that limit is rewritten rather than deleted — what remains true,
and now matters more, is that taking the analyzer is the engine's choice and the diagnostics
are warnings until the engine says otherwise.

Four rules is a surface that will be asked to grow. It should grow slowly: each ID is
permanent, and a false positive in a consumer's build is far more expensive here than a
false negative, because the consumer cannot fix it — they can only suppress it or drop the
package.

The unit tests prove the rules; they cannot prove the packaging. An analyzer that lands in
`lib/` instead of `analyzers/dotnet/cs`, or is built against a Roslyn the consumer cannot
load, passes every unit test and then does nothing in a real build.
`tools/analyzer-probe/check.sh` packs the package and builds a real `net8.0` consumer
against it, asserting both that ambient entropy fails with RK0001 and that a clean consumer
still builds — the second assertion is what stops the first from passing vacuously. It runs
in `scripts/validate.sh full`.
