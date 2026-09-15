# 0021 — External calibration is a release gate

## Status

Accepted — 2026-09-15. Makes the one-off calibration of
[0015](0015-calibrating-the-rule-set-before-it-freezes.md) a standing requirement.

## Context

Every check this repository runs, it wrote. The probes are consumers written by the kernel's
own authors, with the same blind spots. The analyzer's fixtures are the cases its authors
thought of. The two times something outside that circle was consulted, it found what nothing
inside had.

- **0015.** Pointing the analyzer at SRD_Combat, a real engine, found that RK0001's message
  was false for a seeded generator and that RK0003 was mostly `Environment.NewLine` in console
  output. The tag was held.
- **This cycle.** The Part 107 probe, built against the same corpus as faa-part-107, found
  that two engines pinning identical bytes compared unequal over the spelling of a hash
  derivation ([0019](0019-replay-identity-is-necessary-not-sufficient.md)).

Both were found by hand, because someone thought to look. A calibration that happens when
someone remembers is an audit, and an audit is skipped exactly when a release is in a hurry.

## Decision

**A release is calibrated against real consumers before it ships, and the release gate refuses
a tag that was not.**

- A release that changes anything under `src/RulesKernel.Analyzers/` is built with the packed
  analyzer attached to at least **one** real consumer, and the findings are counted per rule.
  A count is not a verdict. The record is read in the release PR, and a change in the counts is
  the reviewer's to explain.
- A release that changes anything under `src/RulesKernel/` is built and tested against at
  least **two** real consumers, and every one named must pass.
- Real consumers are listed in `tools/calibration/consumers.json`, each at a pinned commit and
  with the modes it serves. Moving a pin is a reviewed change. At this decision the list is the
  three engines rules-factory produces (faa-part-107, hoyle-backgammon, srd-52-combat) and,
  for the analyzer, SRD_Combat.
- `tools/calibration/run.sh kernel|analyzer` does the work. It packs the tree under a version
  that cannot shadow a release, copies each consumer, redirects its RulesKernel pins (kernel)
  or attaches the analyzer from outside (analyzer), and prints the table a record carries. In
  analyzer mode it puts one ambient draw beside every project as a control, because "0
  findings" is only a result if the analyzer was running.
- The record is `docs/calibration/<version>.md`. `tools/release-checks.py` requires it at the
  tag whenever the relevant paths changed since the previous release. It must name the kernel
  commit it ran at, that commit must be in the tag's history with no calibrated path changed
  since, and it must name enough consumers at their pinned commits.
- `publish.yml` also reruns the kernel calibration against the tagged tree, so the record is
  not the only evidence.

## What this does not do

It does not run on every pull request. The consumers are fetched over the network, their
builds take minutes, and the in-repository probes already run on every commit. An individual
PR that changes a kernel type is expected to calibrate and say so in its description, as the
PR for 0019 did. The gate makes sure no release ships without it.

It does not make the counts pass or fail. Deciding whether 17 findings in SRD_Combat are 17
true statements is the reading 0015 did, and no script can do it.

It adds no new process beyond this gate. The concrete failures above justify this one, and
anything more needs its own.

## Consequences

`CLAUDE.md`'s release cycle gains a calibration step. `docs/calibration/0.4.0.md` records the
first runs. They reproduced 0015's post-fix SRD_Combat numbers exactly: 17 findings, 10 RK0001,
5 RK0007, 1 RK0003 and 1 RK0004. That is some evidence the tool measures what the manual
calibration did.
