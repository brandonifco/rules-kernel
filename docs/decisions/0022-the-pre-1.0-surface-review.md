# 0022 — The pre-1.0 surface review

## Status

Accepted — 2026-09-30. Decides #57, #58, #59, #60 and #61, which
[0019](0019-replay-identity-is-necessary-not-sufficient.md) and the 0.4.0 cycle left open for
this review. One compatibility change: `ReplayCompatibilityIdentity` is renamed
`EngineIdentity`. Everything else here is documentation.

## Context

1.0 is a promise to carry the public surface's shape and meaning through the 1.x line. Five
questions were deferred to the moment before that promise, because they interact: all five are
about what identity and provenance guarantee. They were decided together, against one test:
*does this need to change before compatibility can responsibly be promised?* A future need is
not enough. The evidence had to come from real consumers.

The consumers read for this review, beyond the probes in this repository: faa-part-107,
hoyle-backgammon, srd-52-combat and SRD_Combat (the calibration set, decision 0021), and
reykholt and hallertau, two further engines rules-factory produced after 0.3.0. Also
rules-factory itself, which generates the engines' code and validates their maps.

## Decision

### #57 — A resolved outcome carries no citation: an intentional boundary

Every engine that cites its answers does so already, in its own result type, with
`SourceLocator` as the atom. They do not agree on anything above the atom:

- faa-part-107 and srd-52-combat put one `SourceLocator Authority` on each result type;
- `Part107Probe` carries an ordered list of the passages it applied;
- reykholt and hallertau attach citations to the map entries the engine implements, as
  `ImmutableArray<SourceLocator>`, not to the answers.

That is three shapes from five engines, which is evidence that the carrier belongs to the
engine. `Resolution<T>` stays as small as it is
([0018](0018-resolution-enforces-handling-not-honesty.md)). An engine that cites its answers
puts its locators in `T`, and the type's documentation now says so. A kernel carrier can
still be added in 1.x without breaking anything if the shapes converge. The shapes are not
converging.

### #58 — Locators are not checked against the identity: an intentional boundary

A kernel check would be wrong half the time. An unresolved result correctly cites a corpus the
engine does not pin: `OutsideCurrentScope` points at exactly what the engine does not hold. A
resolved answer carries no kernel citation (#57), so there is nothing for a kernel check to
read. And a `SourceLocator` does not know which identity it belongs to.

The check exists where the map is known. rules-factory's own record 0039 makes the manifest pin
every corpus a map cites, and its map validator fails any `locator.sourceId` the manifest does
not declare. Locators are then verified against the pinned bytes. A hand-written engine owes the
same check to its own tests.

What changes is a claim. `SourceLocator.SourceId` was documented as "matching" a baseline in
the engine's identity, which reads as a guarantee and is not one. It now says what is true: a
resolved rule's locator names a corpus the engine pins, an unresolved one may not, and the
kernel checks neither.

### #59 — Corpus-coverage assumptions are outside the identity: an intentional boundary

A snapshot pins what a corpus said on its `AsOf` date, not the dates an engine trusts it to
cover. That trust is an engine assumption, and the kernel already has a component for "a change
that could alter how a recorded decision resolves": `RulesetVersion`. An engine that changes its
assumed coverage bumps its revision. A new identity axis for coverage would be new machinery
supported by one probe, and it would still be the engine's declaration. `RulesetVersion`'s
documentation now names this case.

### #60 — The identity type is named too broadly: renamed to `EngineIdentity`

Equal `ReplayCompatibilityIdentity` values mean two runs declared the same ruleset revision,
replay schema, pinned corpora and generator. They do not mean the runs are replay-compatible:
initial state and decisions are outside the type, and so are the engine's assumptions (#59).
0019 kept the name and documented the gap, so that consumers would break once, here, rather than
twice. Carrying the name through 1.x would mean shipping a public type whose own documentation
says its name promises more than it holds.

`EngineIdentity` names what the value is: what an engine declares it resolves against. It makes
no replay claim, so it cannot overclaim one, and it reads as consumers already use the value
(`Ruleset.Identity`, `GameRecord.Identity`, rules-api's "which engine is this"). Members,
constructor, equality and hash semantics are unchanged. Only the type name changes.

### #61 — Different words for one derivation compare unequal: an intentional boundary

0019 fixed the form of `HashDerivation`, which is the kernel's business because the kernel
defined the field. Whether `ecfr-xml` and `ecfr-versioner-xml` name one method is a question about
an adapter's vocabulary, and only the corpus toolkit can answer it. A registry here would be
subject-matter knowledge in the floor. The documentation already says so. Nothing changes.

## Alternatives considered

**`ReplayPrerequisites`**, 0019's candidate name. It says "necessary, not sufficient" in the name,
but a plural noun reads badly as one comparable value, and the name still frames the type around
replay, a guarantee it only partly holds.

**Keeping the name.** It costs nothing now and something every time a reader believes it.
The consumers' cost is small: eight source references across four engines, at a version bump
they take deliberately.

**A resolved-citation type now (#57), with a pinned-corpus check on it (#58).** It would pick one
of three shapes that real engines disagree on, and freeze it for 1.x.

**A coverage range on `SourceBaselineId` (#59).** It would put an engine's assumption inside a
corpus's identity, where two engines pinning the same bytes with different assumptions would
disagree about the corpus rather than about themselves.

## Consequences

The rename is source- and binary-breaking for every consumer that names the type. It is the one
breaking change this review makes, and it lands in 1.0.0, the version at which a consumer
expects one. The public API baseline records it as a removal and an addition. The calibration
consumers need a migration commit, the rename only, before `publish.yml`'s calibration can build
them at the tag.

`Resolution<T>`, `SourceLocator` and `RulesetVersion` gain documentation, not members.
`probes/Part107Probe.Tests/FINDINGS.md` records findings 4 and 5 as decided here. Its tests keep
pinning the behaviour, which is unchanged.

**Revisit** #57 and #58 when three engines carry resolved citations in one shape. Revisit #59
when a real engine, rather than a probe, needs its coverage assumption compared by something
other than itself.
