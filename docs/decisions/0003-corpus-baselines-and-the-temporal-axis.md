# 0003 — Corpus baselines are plural and carry a temporal axis

## Status

Accepted — 2026-09-13.

## Context

The predecessor design carried a single source baseline — one corpus identifier and one
content hash — inside its replay identity. Two problems surfaced as soon as the kernel was
aimed at more than one kind of corpus.

**Engines routinely draw on several corpora.** A core rulebook plus two supplements; a
statute plus its implementing regulations; a board game's rulebook plus its published
errata. A single baseline cannot express any of those, even though the surrounding tooling
already supported multi-corpus manifests.

**A content hash does not say what the content was.** It proves two people read identical
bytes. It does not record *which revision* those bytes represent, and for a continuously
revised corpus that is the question an engine exists to answer: what did this rule say on
this date. Regulations, statutes, errata'd rulebooks and reprinted games all share that
shape. Nothing in the previous identity could express it.

Retrofitting a dimension into a compatibility identity after engines have stored values is
itself a compatibility break. If it is going in, it goes in before anyone consumes the type.

## Decision

`ReplayCompatibilityIdentity.SourceBaselines` is an **ordered collection**, never empty, and
equality over it is sequential: the same baselines listed in a different order are a
different identity. Ordered comparison is stricter than set comparison, and for a
compatibility check the stricter default is the safe one — a false "incompatible" costs a
version bump, a false "compatible" corrupts a replay.

`SourceBaselineId` carries an optional `AsOf` date alongside its identifier and content
hash. Present means "this corpus as it stood on this date". Absent means "this corpus has
no temporal dimension" — a single printing that will never be revised — and specifically
does **not** mean "unknown": a caller who does not know the date of a revisable corpus has
an unpinned baseline, which is a provenance failure rather than a null.

The content hash is validated as 64 hexadecimal characters and normalized to lowercase, so
two baselines naming the same content in different letter case compare equal.

## Alternatives considered

**Derive the moment from the hash.** Rejected: a hash is an index into content, not a date,
and requires an out-of-band lookup table that may not exist for a corpus fetched once.

**Put `AsOf` on the identity as a whole rather than per corpus.** Rejected: an engine may
legitimately combine a regulation as of one date with a rulebook printing that has no date
at all. The temporal axis is a property of each corpus, not of the run.

**Unordered (set) comparison of baselines.** Rejected as the looser default; see above.

## Consequences

`ReplayCompatibilityIdentity` becomes a class with explicit value equality rather than a
record struct, because a record struct will not compare a collection member by sequence.

An engine over a timeless corpus passes no date and is unaffected. An engine over a revised
corpus must decide, explicitly, which moment it pinned — which is the point.
