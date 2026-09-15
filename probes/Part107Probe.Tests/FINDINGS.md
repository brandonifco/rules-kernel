# What Part 107 found in the kernel

Written while building this probe, against `RulesKernel` 0.4.0-dev. Each finding names the
test in `KernelPressureTests.cs` or `RuleTests.cs` that pins the current behaviour. A kernel
change that resolves a finding should fail that test, and the finding is then updated here
rather than deleted.

The probe covers § 107.29 (night and civil twilight, including the waiver dates in (d)) and
§ 107.65 (knowledge recency), quoting 14 CFR Part 107 as of 2026-01-01. The same bytes are
pinned in github.com/brandonifco/faa-part-107, which is a second, independently written
consumer of the same corpus. Where the two engines hit the same problem, that is stated,
because two engines hitting it is stronger evidence than one.

Counts: **8 findings.** Three are decided in
[decision 0019](../../docs/decisions/0019-replay-identity-is-necessary-not-sufficient.md) (1,
2 and 3): 1 changed the kernel, and 2 and 3 changed only what it claims. Two are accepted
limitations for 0.4.0 (4, 5). The remaining three are evidence the kernel held up (6, 7, 8).

---

## 1. Two engines pinning the same bytes agree only by copying a spelling

`HashDerivation` was free text compared ordinally. This probe's baseline equals
faa-part-107's only because the string `ecfr-versioner-xml` was copied from that engine's
generated code. `eCFR-versioner-XML` names the same bytes and the same method, and it compares
unequal. `ecfr-versioner-xml ` with a trailing space is accepted and also compares unequal,
even though `SourceId` rejects surrounding whitespace for exactly that reason.

**Decided (0019).** The kernel now fixes the form: lowercase ASCII letters and digits joined by
single hyphens or dots. Both respellings above are rejected at construction. Different words
for the same method (`ecfr-xml`) still compare unequal. That vocabulary belongs to the
adapter, and the kernel does not guess at it.

Tests: `The_same_bytes_pinned_by_two_engines_compare_equal_when_both_use_the_one_canonical_form`,
`A_respelling_of_the_same_derivation_is_rejected_rather_than_compared_unequal`,
`Different_words_for_the_same_derivation_still_compare_unequal`.

## 2. One corpus id holds one moment, and a moment is not a range

The pinned snapshot says what Part 107 read on 2026-01-01. It does not say when that text
took effect. § 107.29(d) names two dates, March 16 and April 21, 2021, for different
consequences. The engine needs a start date to answer anything about an operation before
2026-01-01, and it takes that date on its own authority (`Corpus.TextInForceSince`, marked
unverified).

Two things follow. First, an engine answering on both sides of an amendment needs two
snapshots of one regulation, and `ReplayCompatibilityIdentity` rejects two baselines with one
`SourceId`. Second, the start date appears nowhere in the identity: two engines that assume
different start dates have equal identities and give different answers for April 2021.

**Decided (0019).** One id stays one moment. A citation names a corpus by id alone, so two
moments under one id would make every citation ambiguous. An engine that needs editions gives
each edition its own id, and its citations into different editions then differ, which is
accurate, because they cite different text. The trusted date range is an engine assumption and
belongs to its ruleset version, and nothing enforces that. This is recorded as an accepted
limitation.

Tests: `An_identity_cannot_pin_two_snapshots_of_one_regulation`,
`Nothing_in_an_identity_records_the_date_range_its_snapshot_is_trusted_for`,
`An_operation_the_pinned_snapshot_does_not_cover_is_outside_scope`.

## 3. Declaration order is compared, and it is not precedence

The engine applies the regulation before its recorded interpretation because the
interpretation reads the regulation. List position has nothing to do with it. Precedence in
this corpus is not a list at all. § 107.29(d)'s dated provisions override the general waiver
rule in § 107.200 only for night operations and only after a date, and how an adopted
interpretation ranks against the text depends on the question. Even so, the same two
baselines in the other order form a different identity.

Adopting an interpretation also changes the identity through a baseline while the ruleset
version stays the same. That is the right place for the change, and it is worth stating.

**Decided (0019).** Order carries no precedence, and the documentation now says so. Equality
stays sequential, because the kernel cannot see whether an engine reads the order.

Tests: `Listing_the_same_corpora_in_the_other_order_is_a_different_identity`,
`Adopting_an_interpretation_changes_the_identity_without_changing_the_ruleset_version`.

## 4. A resolved answer has no citation in the kernel's vocabulary

`Resolution<T>.Unresolved` must cite. `Resolved` cannot. Both Part 107 engines built the
missing half independently: faa-part-107 put an `Authority` on each finding, and this probe
has `Finding<T>`, which lists authorities in the order they were applied. This probe needs a
list rather than a single authority, because an answer at the 24th month of § 107.65 rests on
the paragraph and on the engine's own interpretation of it. A night answer rests on § 1.1,
§ 107.29(a)(1) and § 107.29(a)(2).

Accepted for 0.4.0. `Resolution<T>` stays as small as it is
([decision 0018](../../docs/decisions/0018-resolution-enforces-handling-not-honesty.md)).
Two engines converging on a shape is the evidence a future kernel type would need. It is not
yet evidence for the shape itself: one engine carries one authority and the other a list.

Test: `A_resolved_outcome_carries_no_citation_and_an_unresolved_one_must`.

## 5. Answers cite corpora the engine does not pin, and the kernel cannot tell which are wrong to

`SourceLocator.SourceId` is documented as "matching a SourceBaselineId.SourceId in the
engine's replay identity". Nothing checks that, and in this probe it is false twice, once
correctly and once not.

**Correctly:** the unresolved results for night beyond the 30-minute window and for Part 61
training cite `cfr-14-1` and `cfr-14-61`. Neither is pinned, and both citations are right,
because an `OutsideCurrentScope` or `MissingRulesData` result points at what the engine does
not hold.

**Not correctly:** a *resolved* night answer lists `cfr-14-1 / § 1.1 Night` among its
authorities (`A_resolved_night_answer_cites_every_passage_it_applied_in_order`). The engine
applied a definition from a corpus it never pinned, so a change to Part 1 would change its
answers without changing its identity. That is a provenance defect in this probe, and it is
left in on purpose, because nothing in the kernel could have caught it: resolved answers
carry no citations (finding 4), and locators are never checked against the identity.

Accepted for 0.4.0 as a limitation, with this probe as its reproduction. A check that a
resolved answer cites only pinned corpora needs finding 4 resolved first.

Test: `An_unresolved_result_may_cite_a_corpus_absent_from_the_engines_identity`.

## 6. The five reasons held

Every unresolved branch fit a reason without strain:

- `RequiresInterpretation`: the 24th calendar month, an unasserted flash rate, and a waiver
  that "terminate[s] on May 17, 2021" when the same paragraph says "After May 17, 2021".
- `MissingRulesData`: the Air Almanac, which both Alaska's civil twilight and § 1.1's night
  depend on.
- `UnsupportedInteraction`: a time that is neither § 107.29(c)'s civil twilight nor § 1.1's
  night.
- `OutsideCurrentScope`: a waiver's unpublished terms, a Part 61 dependency, and operations
  before or after the snapshot. "Another edition" is exactly the latter.

A fact the caller did not supply, such as a record dated after the operation, is an
`ArgumentException` here, as the missing waiver statement is in faa-part-107. Both engines
chose that independently, so it is not evidence for a sixth reason.

## 7. Closed by construction is invisible to the compiler

A `switch` over `Resolution<T>` still needs a default arm, because the compiler does not know
the private constructor closes the hierarchy (`Part107Engine.MayOperate`). This is a small
cost, and the kernel already says it.

## 8. Provenance pinned an error in the corpus faithfully

The pinned § 107.29 ends "86 FR 13631, Mar. 10, 2020". Volume 86 of the Federal Register is
2021, and the pinned § 107.65 cites the same page as "Mar. 10, 2021". A locator cites the passage verbatim, and the hash pins the
error along with everything else. Correcting it is not the kernel's job, and the kernel does
not try.
