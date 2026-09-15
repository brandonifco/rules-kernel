# 0014 — Warning where unordered becomes ordered

## Status

Accepted — 2026-09-15. Closes the gap
[0009](0009-what-the-source-blacklists-do-not-prove.md) lists last and
[0013](0013-the-analyzer-ships-when-its-rules-cover-what-it-claims.md) names as one of the
two that hold a release.

## Context

`docs/architecture.md` said it plainly: enumerating a `Dictionary` or a `HashSet` is "a
review obligation, not a gate". [0009](0009-what-the-source-blacklists-do-not-prove.md)
listed it alongside reflection and `using` aliases as a bypass left deliberately
unaddressed. An external review of `8d205fc` verified the hole empirically rather than
inferring it: `System.Random.Shared.Next()` reported RK0001, `foreach (var kv in dictionary)`
reported nothing, `foreach (var x in hashSet)` reported nothing.

With `--only ordering` and the `determinism` blacklist both mechanised, and the kernel's
central claim being "the same ordered history", that left the largest acknowledged
determinism gap enforced by reviewer memory alone.

A gate cannot prove ordering in general. It does not have to. The common case is a *known*
unordered collection type reaching a *known* ordered sink, which is a symbol question, not a
dataflow one.

## Decision

RK0007 warns when an unordered collection is materialized into an ordered one without an
explicit sort.

That sentence is the whole scope. It is recognisable in the diagnostic message, in
`UnorderedMaterializationAnalyzer`, and in every test fixture: the rule fires only on a
positive recognition of **both** halves.

**The unordered half** is a list of concrete types whose documented iteration order is
unspecified — `Dictionary<,>`, `HashSet<>`, `ConcurrentDictionary<,>`, `ConcurrentBag<>`,
the immutable and frozen dictionaries and sets, `Hashtable` — plus the nested collections
they declare, so `dictionary.Keys` is the same finding as `dictionary`. No interface is on
that list. `IDictionary<,>` is implemented by `SortedDictionary<,>` and `IEnumerable<T>` says
nothing about order at all, so flagging either would fire on code that is already correct.

**The ordered half** is a list of sinks whose entire contract is that things come out in the
order they went in: `List<T>.Add` and its relatives, `Queue<T>.Enqueue`, `Stack<T>.Push`,
`StringBuilder.Append*`, the `ImmutableArray<T>`/`ImmutableList<T>` builders, and an array
element write. `ICollection<T>.Add` would have covered all of them in one line and is
deliberately absent, because `HashSet<T>` implements it too.

The LINQ form is the same question with the loop written as a chain:
`dictionary.Select(…).ToList()` reports, and so do `.ToArray()`, `.ToImmutableArray()`,
`.ToImmutableList()` and `string.Join`. `ToDictionary`, `ToHashSet`, `ToLookup`, `Any`,
`All`, `Count` and `Sum` do not, because their result cannot depend on the order the source
was visited in.

`.OrderBy(…)` and `.OrderByDescending(…)` end the walk silently. That is the fix the message
names, so it is asserted directly rather than left to follow from the design.

## The two scopes rejected

The issue offered three. Both rejected ones failed for the same reason from opposite
directions: a rule is worth its permanent ID only if a consumer leaves it switched on.

**Flag all direct enumeration of an unordered collection.** Simple, and the version that
dies. Most enumeration of a dictionary is `.Count()`, `.Any()`, a commutative sum, or
building another set, and none of it can depend on visit order. A rule that squiggles all of
it in ordinary correct code gets suppressed project-wide in a `.editorconfig` within a week,
and a suppressed rule catches nothing — it is strictly worse than no rule, because the
suppression then also covers the real case that arrives next year.

**Flag only where the result flows into an ordered kernel type.** The narrowest option and
the one this repository's owner leaned toward before the code was read. Counting what it
would actually guard settled it: `ReplayCompatibilityIdentity`'s `sourceBaselines` is the
only place in the kernel that takes a consumer-supplied ordered collection. The rule would
have been precise, correct, and would have guarded essentially one constructor — while the
shape the review actually reproduced, a dictionary walked into a `List<T>` that never touches
a kernel type, went on reporting nothing.

## The false positive is the expensive one

[0011](0011-shipping-a-determinism-analyzer.md) records why: a consumer cannot fix a false
positive. They can suppress it or drop the package, and both outcomes cost more than a
missed case. So **when the analyzer cannot tell, it stays quiet**, and that is a decision
rather than an unfinished feature.

Concretely, the following are known false negatives, each pinned as a test so it is on
record instead of waiting to be discovered:

- **A call it cannot see into.** `foreach (var kv in map) { Record(kv.Value); }` reports
  nothing, even when `Record` appends to a list one frame down. Answering would need a call
  graph, which an operation action does not have. This is the same line
  [0011](0011-shipping-a-determinism-analyzer.md) drew for RK0005 and a helper called from
  `GetHashCode`, drawn the other way round — there, refusing to follow the call over-reports;
  here it under-reports. The line is the same: no call graph, no claim.
- **An interface-typed source.** A parameter typed `IDictionary<,>` or `IEnumerable<T>` may
  be ordered.
- **An operator the rule does not model.** `GroupBy`, `ToLookup`, a consumer's own extension
  method: the walk back through the chain stops at the first operator it does not recognise
  rather than assuming order survived it.
- **Ordering-sensitive reads that are not materializations.** `dictionary.First()` is a real
  determinism bug and RK0007 says nothing about it, because it is not the sentence above.
  A separate ID is the honest way to cover it, not a widening of this one.

The one place the rule does the opposite and reasons about flow is the accumulator declared
*inside* the loop body: a `List<T>` created per iteration dies with it, so its fill order
cannot reach a result, and flagging it would fire on a perfectly ordinary shape. The check
asks only where the local was declared, never where it goes afterwards — a local declared
above the loop is treated as escaping even when it does not.

## Consequences

`docs/architecture.md` and [0009](0009-what-the-source-blacklists-do-not-prove.md) no longer
say unordered iteration is review-only, and both now say precisely how much is checked and by
what. The claim they make is narrow on purpose: this is a warning in an *opt-in* package over
a *consumer's* code. This repository's own source is still checked by
`tools/repo-checks.py`, which is textual and does not know what a `Dictionary` is, so
unordered iteration inside the kernel itself remains a review obligation — the wording says
so.

RK0007 is the seventh permanent ID. Per
[0013](0013-the-analyzer-ships-when-its-rules-cover-what-it-claims.md) it is one of the two
gaps blocking a release; replay-unstable hashing was the other and RK0005 closed it, so the
analyzer's rules now cover what its name claims.

## What would change this

- **A false positive in a real consumer's build.** That is the failure this scope was
  chosen to avoid, and a single confirmed one is grounds to narrow the type lists rather
  than to add a suppression note. Report it against the fixture, not the rule.
- **A false negative that happens by accident rather than by construction.** Every gap
  above is reachable deliberately. If one of them is hit by someone who did not know the
  rule, the trade is mispriced for this codebase and the scope should widen — most likely by
  modelling one more LINQ operator, which is additive and needs no new ID.
- **A second engine's code.** The type and sink lists are the kind of thing that looks
  complete until it meets a codebase that was not consulted while writing it.
