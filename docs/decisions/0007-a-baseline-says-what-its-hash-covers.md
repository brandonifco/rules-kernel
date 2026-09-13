# 0007 — A baseline says what its hash covers

## Status

Accepted — 2026-09-13. Amends [0003](0003-corpus-baselines-and-the-temporal-axis.md).
Source-breaking: `SourceBaselineId` gains a required `hashDerivation`. Ships in 0.2.0,
before any engine has stored a baseline.

## Context

[0003](0003-corpus-baselines-and-the-temporal-axis.md) justified the content hash with
"a hash proves two people read identical bytes." **The type did not guarantee that.** It
validated 64 hex characters and lowercased them, and said nothing about what the hash was
a hash *of*.

An assessment of a second real engine made the gap concrete rather than theoretical.
SRD_Combat computes a corpus fingerprint by hashing the sorted roster of content ids, and
its own comment says exactly what that does and does not cover: "by id alone, not by the
numbers behind them. Two loads with the exact same roster of ids fingerprint identically
even if a description changed underneath."

That is a legitimate hash, deliberately coarse, and it is precisely the value that engine
would put in a `SourceBaselineId`. Another engine over the same corpus would hash the PDF
bytes. A third would hash the extracted JSON. All three would publish
`srd-5.2.1#<64 hex>` — comparing unequal for identical corpora, or worse, an engine
believing its baseline pins content it does not pin.

The failure is the same shape as the one [0005](0005-pinned-pseudorandom-algorithm.md)
identifies for generators: nothing about a hash proves what produced it, so two baselines
that agree today can mean different things, and the identity cannot tell you.

## Decision

`SourceBaselineId` carries a required `HashDerivation` — a short string naming what the
hash was computed over, in the grammar the corpus's adapter declares: `"pdf-bytes"`,
`"extracted-json"`, `"id-roster"`.

The kernel does not interpret it, exactly as it does not interpret a citation
(`SourceLocator`). Only the adapter that produced the hash can define what it covers. What
the kernel does is make the difference *comparable*: two baselines over the same content
hashed over different things are two baselines, and a consumer can see why.

## Alternatives considered

**Document what the hash must be over, and require adapters to honour it.** Rejected: it
makes the kernel pick a definition it has no basis for picking. The roster hash above is a
reasonable engineering choice, not a mistake to be legislated away.

**A closed enum of derivations.** Rejected for the same reason the citation grammar is not
enumerated — the kernel cannot know what corpora exist.

**Leave it and soften 0003's prose.** Rejected. The whole argument of
[0003](0003-corpus-baselines-and-the-temporal-axis.md) is that retrofitting a dimension
into a compatibility identity after consumers store values is itself the break. That
argument applies here with full force, and there are no such consumers yet. Now is the only
cheap moment.

## Consequences

Source-breaking for anyone constructing a `SourceBaselineId`. The only consumer at the time
of this decision is Deckard, which does not construct one.

`ToString()` becomes `sourceId[@date]#derivation:hash`, so the derivation is visible
wherever a baseline is printed rather than only where it is compared.

Every engine must now answer "what did I hash" at the point of construction. That is the
intended cost: it is a question every one of them already had an answer to, and none of
them was recording it.
