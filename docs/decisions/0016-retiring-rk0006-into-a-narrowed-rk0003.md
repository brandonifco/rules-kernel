# 0016 — Retiring RK0006 into a narrowed RK0003

## Status

Accepted — 2026-09-15. Answers the question
[0015](0015-calibrating-the-rule-set-before-it-freezes.md) left open when it held the
`v0.3.0` tag.

## Context

[0015](0015-calibrating-the-rule-set-before-it-freezes.md) pointed the analyzer at
`SRD_Combat` and counted what each rule said. RK0003 fired ten times, eight of them on
`Environment.NewLine` being concatenated into console output. RK0006 — ambient culture and
time zone — fired zero times, and 0015 noted that its split from RK0003 "was already in
question".

Narrowing RK0003 is not re-argued here. The rule now names the `System.Environment` members
that make a *result* machine-dependent — `ProcessorCount`, `MachineName`, `GetFolderPath`,
`GetEnvironmentVariable`, `ProcessPath`, `CurrentDirectory` and their relatives — and no
longer bans the type. `NewLine` is output formatting and `Exit` is process control, and the
old message claimed a *read* of machine state that `Exit` does not perform.

What that narrowing forces is the taxonomy question, and it has to be answered now rather
than considered later. Nothing has been published: `v0.3.0` is prepared but untagged. An id
retired today leaves no trace, because it was never a contract anybody could have written a
suppression against. The same act after the tag costs a burned number, a migration, and a
period in which a consumer's `.editorconfig` line half works.

## Decision

**RK0006 is removed.** Ambient culture and time zone fold into RK0003, which becomes
"ambient machine state makes a result machine-dependent" and covers
`CultureInfo.CurrentCulture`, `CultureInfo.CurrentUICulture` and `TimeZoneInfo.Local`
alongside the environment members.

Removed, not marked retired. `AnalyzerReleases` carries no row for it and the descriptor is
gone from the public surface. An id that never shipped never existed, and a file that says
otherwise is recording a contract that was never offered.

**RK0007 does not move down.** The gap at RK0006 is permanent and correct, and both
analyzer sources say so at the point where someone would be tempted to tidy it. Renumbering
would change the identity of a rule whose meaning did not change, which is the one thing
these numbers exist to prevent — the same reasoning `docs/decisions/README.md` applies to
these records, where a superseded decision keeps its number forever.

## The argument that was overruled

It deserves recording, because it is not a weak argument and the decision is not reversible
once the tag lands.

Ambient culture does something `ProcessorCount` does not. Nobody *reads* a culture into a
result. The culture changes what comparing, sorting, parsing and formatting **mean**, at
call sites that name nothing ambient at all — `text.ToUpper()`, `value.ToString()`,
`double.Parse(s)` — and the fix is a `CultureInfo` argument or an invariant overload rather
than lifting a value into a parameter. A consumer could coherently want culture-invariance
enforced while allowing machine-info reads, and one id denies them that: the suppression
for the machine facts silently takes culture with it.

Against that, and decisive: the two rationale sentences were near-identical, one concept
should need one suppression, and the calibration gave RK0006 zero findings against RK0003's
ten. A rule that fires nowhere and reads like its neighbour is a distinction the rule set is
asserting rather than one a consumer has asked for.

The zero is weak evidence on its own — `SRD_Combat` sets `InvariantGlobalization` and does
not format for a user's locale, so it is an unexercised rule rather than a measured one.
That is a reason the question was close, not a reason to keep the id.

## What the re-calibration measured

The same procedure as [0015](0015-calibrating-the-rule-set-before-it-freezes.md), against a
`git archive HEAD` export of the same tree, with the pre-change analyzer packed and built
first so before and after ran under identical conditions. The baseline reproduced exactly:
26 unique findings, RK0001 ×10, RK0003 ×10, RK0007 ×5, RK0004 ×1.

| Rule | Before | After |
|---|---|---|
| RK0001 ambient or unpinned entropy | 10 | 10 |
| RK0003 ambient machine state | 10 | 1 |
| RK0004 concurrency | 1 | 1 |
| RK0006 culture and time zone | 0 | *(retired)* |
| RK0007 unordered materialization | 5 | 5 |
| **Total** | **26** | **17** |

RK0003's nine lost findings are the eight `Environment.NewLine` and the one
`Environment.Exit`. What survives is `Environment.GetFolderPath` in a command-line tool —
the one finding of the ten that was never noise. The fold added nothing, as expected from a
corpus where RK0006 found nothing to report; that it *would* have is asserted in
`tests/RulesKernel.Analyzers.Tests` instead, on fixtures the corpus does not supply, each
beside a near-identical one that stays silent.

RK0001's count is deliberately unchanged. Two of its ten now say the true thing: the seeded
source in `IRandomSource.cs` is told its algorithm is not pinned rather than that it draws
from ambient entropy.

Three findings are judged correct but likely to be suppressed: `Guid.NewGuid` ×3 and
`Task.Run` ×1, all in test projects, where a unique temporary name and a concurrency test
are exactly what the code is for. `tools/repo-checks.py` exempts this repository's own test
projects for the same reason and can, because it knows which projects those are. A package
loaded by a consumer's compiler does not, and inventing a heuristic for it would be the
false-positive direction [0011](0011-shipping-a-determinism-analyzer.md) warns about
pointed the other way. Left as the consumer's `.editorconfig` call, and recorded here so the
next calibration does not read them as new.

The fidelity limit 0015 stated still bounds this: SDK `8.0.129` is not installed here, so
`global.json` was neutralised and the build ran on the pinned .NET 10 SDK targeting
`net8.0`. `TreatWarningsAsErrors` was turned off for the run, because leaving it on is what
stopped the first calibration's build after three errors and hid the rest of the findings.

## Consequences

The published rule set will be RK0001 to RK0005 and RK0007. Six rules, one gap, no
renumbering, ever.

**Taxonomy questions are answered before the first tag or not at all.** That is the general
lesson, and it is the reason this decision was forced now rather than deferred to the first
consumer who complained. Retiring RK0006 cost one afternoon's edit because no package
carrying it exists. The same edit a week after the tag would have cost every consumer who
had written `dotnet_diagnostic.RK0006.severity` into a file.

The one thing the merge gives up is stated so a later reader does not have to rediscover it:
a consumer can no longer suppress machine-info reads without also suppressing
culture-invariance. If that turns out to matter, the answer is a **new** id for the
distinction, never a reuse of 0006.

Culture remains unexercised by any real corpus, which is now RK0003's gap rather than its
own rule's. The next calibration should be run against a corpus that formats for a user.

The lesson from the narrowing is worth keeping beside this record, because it is the one
most likely to be repeated. `tools/repo-checks.py` bans `Environment.` as a whole-type
prefix and is right to: every line of this repository's packaged code is a rules result.
That reasoning was carried into a consumer-facing rule without being re-derived, and a
consumer's application is not the kernel — it has a console UI, a file loader and an exit
path. A rule about what may appear anywhere in *this* repository does not transfer to a rule
about what may appear anywhere in someone else's.
