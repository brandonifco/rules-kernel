# 0019 — Replay identity is necessary, not sufficient

## Status

Accepted — 2026-09-15. Amends [0003](0003-corpus-baselines-and-the-temporal-axis.md) and
[0007](0007-a-baseline-says-what-its-hash-covers.md).

One behaviour change: `SourceBaselineId` rejects a `hashDerivation` that is not in canonical
form. It is source-compatible, and a value that no known consumer uses now throws. The rest of
this decision changes documentation only.

## Context

`probes/Part107Probe.Tests` was built against the same pinned corpus as a real engine,
github.com/brandonifco/faa-part-107, to push on replay identity from a corpus whose text
changes on dates it states itself. Its FINDINGS.md raised three questions, and this decision
answers them.

## Decision

### 1. Declaration order is not precedence, and equality stays sequential

The kernel reads nothing from a baseline's position, and no engine should. In Part 107,
precedence is not a list. § 107.29(d)'s dated provisions override the general waiver rule for
night operations only, and only after a date. How an engine's own recorded interpretation
ranks against the text depends on the question. That is a rule, and rules live in the engine's
code under its `RulesetVersion`.

Equality over `SourceBaselines` still compares sequentially, as 0003 decided. The reason is
narrower than 0003 gave: the kernel cannot see whether an engine iterates its baselines and
takes the first match, which would make order meaningful in that engine. A false
"incompatible" costs a version bump, and a false "compatible" corrupts a replay. An engine
declares its baselines in one stable order and treats reordering them as a compatibility
change.

**Revisit when** a real consumer hits a spurious incompatibility from a reorder it could not
avoid. None has.

### 2. `ReplayCompatibilityIdentity` is named too broadly; the name stays until the 1.0 review

Equal identities establish that two runs declared the same ruleset revision, replay schema,
corpora and generator. They establish nothing about initial state or decisions, and nothing
about the assumptions an engine makes about its corpora. The probe trusts a 2026-01-01
snapshot back to 2021-04-21, a date the snapshot does not contain. Two engines assuming
different start dates have equal identities and give different answers.

The name is kept for now. A rename breaks every consumer, including the code rules-factory
generates for them, and the evidence gathered in 0.4.0 may reshape this type further (see 3 and
the accepted limitations below). One rename at the pre-1.0 surface review is better than two.
Until then, the type's documentation says it is necessary and not sufficient.

### 3. `HashDerivation` has a canonical form, and its vocabulary is still the adapter's

A derivation is one or more runs of lowercase ASCII letters and digits, joined by single
hyphens or dots: `ecfr-versioner-xml`, `srd-5.2.1-pdftotext-24.02.0-page-marked`. The
constructor rejects anything else.

The field is compared ordinally, and it exists so that two engines can see they pinned the same
thing (0007). Before this, `eCFR-versioner-XML` and `ecfr-versioner-xml ` were both accepted,
and both compared unequal to faa-part-107's baseline over identical bytes. `SourceId` already
rejects surrounding whitespace for the same reason. Fixing the form is the kernel's call because
the kernel defined the field. What `ecfr-versioner-xml` *means*, and whether `ecfr-xml` names
the same method, stays with the adapter that produced the hash, exactly as before.

Every derivation in use was checked before the change: faa-part-107 (`ecfr-versioner-xml`),
hoyle-backgammon (`gutenberg-plain-text-including-boilerplate`), srd-52-combat
(`srd-5.2.1-pdftotext-24.02.0-page-marked`), rules-factory's fixtures and examples, and every
value in this repository. All conform.

### 4. One corpus id holds one moment

The duplicate-id rule in `ReplayCompatibilityIdentity` stays. A `SourceLocator` names a corpus
by id alone, so two snapshots under one id would make every citation ambiguous. An engine that
needs two editions gives each its own id. Its citations into them then differ, which is
correct, because they cite different text.

## Alternatives considered

**Set equality over baselines.** Rejected, for the reason in 1. It is the right semantics for
an engine that reads no order, and the kernel cannot know which engines those are.

**Rename now**, to something like `ReplayPrerequisites`. Rejected for the reason in 2. The
documentation change is what matters, and it is made now.

**Normalize derivations** (lowercase, trim) instead of rejecting them. Rejected. `ContentHash`
normalizes case because hex has one meaning in either case. A derivation is a name, and folding
a name's case would be a guess about the adapter's scheme. Rejecting it makes the adapter
choose.

**A registry of derivation names.** Rejected. That vocabulary belongs to the corpus toolkit,
not the floor.

## Consequences

`SourceBaselineId`'s constructor gains one check, with tests for accepted and rejected forms.
`ReplayCompatibilityIdentity`'s documentation states 1 and 2. The Part 107 probe's pressure
tests now assert the rejection.

Two limitations are accepted and tracked as issues rather than implied solved: an engine's
assumptions about its corpora (the trusted date range) are outside the identity, and a
resolved answer carries no citation.

This changes an identity primitive, so it ran against consumers before merging. In this
repository that meant `RegulatoryProbe.Tests`, `Part107Probe.Tests` and `tools/package-probe`.
Outside it, the three factory-produced engines were built and tested against a local pack of
this change: faa-part-107 at `3f11dde` (156 tests), hoyle-backgammon at `5ef2097` (552) and
srd-52-combat at `894897f` (250), all passing. Those runs used SDK 10.0.111 in place of the
engines' pinned 10.0.112, which that machine lacked. As a control, the same run with the check
changed to reject any derivation ending in `xml` failed faa-part-107's tests.
