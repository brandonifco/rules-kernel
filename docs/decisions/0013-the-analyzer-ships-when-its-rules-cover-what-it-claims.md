# 0013 — The analyzer ships when its rules cover what it claims

## Status

Accepted — 2026-09-15. Follows
[0011](0011-shipping-a-determinism-analyzer.md), which deliberately left this open.

## Context

[0011](0011-shipping-a-determinism-analyzer.md) put `RulesKernel.Analyzers` in the
repository on an explicit distinction: merging is reversible, publishing is not. RK0001 to
RK0004 become a permanent contract the moment a version carrying them reaches nuget.org,
where a package can be unlisted but never deleted. Consumers suppress by ID, so an ID cannot
later be renumbered or repurposed without silently changing what their suppression means.

`publish.yml` packs the whole solution, so the next tag would have published the analyzer as
a side effect of tagging. A side effect is not a decision.

The question looked like "should 0.3.0 publish the analyzer". It is not. **Not one line of
C# has changed in the three released packages since `v0.2.0`** — their entire diff is the
public API baseline promotion, which is metadata about what already shipped. A 0.3.0 without
the analyzer would republish three functionally identical packages under a new number. So
the real question is whether there is a 0.3.0 at all, and the analyzer is the only thing
that would give one any content.

## Decision

No release until the analyzer's rules cover the gaps this repository has already written
down — replay-unstable hashing and unordered iteration. Then all four packages go out
together as 0.3.0.

## Reasoning

**Adding rules later is safe; that is not the risk.** RK0005 and beyond are additive, and a
consumer who never sees them is no worse off. The risk is the first impression. A package
named for determinism that says nothing about `GetHashCode` used as a seed, or about
enumerating a `Dictionary`, invites a consumer with a clean build to conclude more than is
true. `docs/architecture.md` and [0009](0009-what-the-source-blacklists-do-not-prove.md)
already admit those are the two largest holes. Shipping the analyzer while they are open
would advertise a coverage this repository has elsewhere been careful not to claim.

**The rule set is still settling.** Within days of 0011, an entire operation kind was found
unregistered: a banned member captured as a method group — `Func<Guid> f = Guid.NewGuid;` —
reported nothing, while the same member called one line later reported RK0001. That was not
a subtle edge case, it was a category, and it is exactly the semantic indirection 0011 cites
as the package's reason to exist. A taxonomy that recent should not be frozen.

**Waiting costs almost nothing.** An engine that wants the diagnostics today references the
project directly or packs it with `scripts/pack-local.sh`. Nobody is blocked.

## Consequences

**No exclusion mechanism is built, and none should be.** Keeping the analyzer out of a
release while still cutting one would have meant either an explicit per-project pack list in
`publish.yml`, or `IsPackable=false` on the analyzer. The second is the tempting one and it
is wrong: `check_determinism` and `check_target_frameworks` both iterate
`packaged_projects()`, which skips a project that says `IsPackable=false`. Expressing a
release preference that way would have silently dropped the analyzer's own source out of the
determinism scan and its framework commitment out of the framework check — weakening two
gates to say something about publishing.

**This cannot be undone by accident.** The release gate from
[0010](0010-enforcement-stops-at-the-package-boundary.md) refuses to publish while any
`PublicAPI.Unshipped.txt` is non-empty, and the analyzer's declarations were unpromoted when
this was written, so tagging that day would have failed before anything was packed.

*Superseded in fact, not in decision:* the release PR has since promoted all four baselines,
so that particular guard no longer stands between this repository and a tag. What holds the
tag now is [0015](0015-calibrating-the-rule-set-before-it-freezes.md), and what returns `main`
to a development version while it is held is
[0017](0017-a-held-tag-returns-main-to-a-development-version.md). The decision recorded here
— that the analyzer ships only when its rules cover what the package claims — is unchanged.

## What would change this

A consumer that needs the four existing rules sooner than the remaining ones. That is a real
possibility and it is not this decision's job to pre-empt — it is a reason to revisit, with
the cost stated plainly: the IDs freeze on the day it ships, complete or not.
