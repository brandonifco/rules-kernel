# 0017 — A held tag returns main to a development version

## Status

Accepted — 2026-09-15. Closes a gap in
[0012](0012-a-development-tree-is-not-a-release-candidate.md).

## Context

[0012](0012-a-development-tree-is-not-a-release-candidate.md) opens with a claim: **`main` is
never a release version.** It describes a cycle in which the release PR clears
`<VersionSuffix>`, promotes the baselines, and *is tagged* — one atomic-sounding step.

It was not atomic. The 0.3.0 release PR was merged, and then the tag was held: a calibration
run against a real engine found two rules whose messages said false things, recorded in
[0015](0015-calibrating-the-rule-set-before-it-freezes.md). Holding the tag was right. But it
left `main` resolving to a plain `0.3.0` — an unreleased tree wearing a release version, which
is the exact state 0012 exists to make unreachable, and the exact shape of the incident
`scripts/pack-local.sh` documents.

Nobody packed it, so no harm followed. The gap is the point: 0012's cycle had no answer for
"the release PR merged and the tag did not follow", and that is not an exotic case. It is what
happens whenever the last check before publishing does its job.

## Decision

**If a tag is held after its release PR merges, `main` returns to a development version
immediately.** `VersionSuffix` goes back to `dev`, keeping the same `VersionPrefix` — the
release being prepared has not shipped, so it is still the *next* release, and `main` resolves
to `0.3.0-dev` again rather than moving on to `0.4.0`.

The prefix advances only after a tag is actually pushed. A version number is not spent by
preparing to release it.

## Consequences

The release PR is no longer the last reviewed step before a tag; it is a step that can be
undone by a second PR if the tag does not follow. That is a small cost, and it is the honest
shape of the process: between merge and tag there is a window, and the window has a correct
state.

`tools/release-checks.py` and `publish.yml`'s resolved-version gate are unaffected — both run
at the tag, against whatever the tree says then. A `main` carrying `dev` simply cannot be
tagged, which is the protection working rather than an obstacle.

The alternative, considered and rejected: **merge the release PR only when tagging
immediately afterwards**. It sounds tidier and it is unenforceable — nothing can make a human
push a tag within a given minute, and a rule that depends on that is prose, not enforcement.
The state has to have a correct answer, because it will occur.

## What would change this

Automating the tag as part of the release PR's merge, so the window closes mechanically. That
would make this record unnecessary. It would also put a publish behind a merge button, which
is a larger decision than this one and has not been made.
