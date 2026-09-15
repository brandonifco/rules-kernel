# Decisions

One file per decision that would otherwise be re-litigated, re-derived, or quietly
reversed. A decision record states what was decided, what the alternatives were, and what
follows from it — not how the code works, which is the code's job.

Numbering is sequential and permanent. A superseded decision is marked superseded in place
and kept; its number is never reused, because references to it exist in code comments and
in other decisions.

`tools/repo-checks.py --only doc-references` fails the build on a reference to a decision
that does not exist, so a citation in a comment is a promise this repository keeps.

| # | Decision |
|---|----------|
| [0001](0001-kernel-scope-and-layering.md) | Kernel scope and layering |
| [0002](0002-randomness-is-optional.md) | Randomness is optional, and dice are not kernel vocabulary |
| [0003](0003-corpus-baselines-and-the-temporal-axis.md) | Corpus baselines are plural and carry a temporal axis |
| [0004](0004-unresolved-results-and-the-totality-burden.md) | Unresolved results, and where the totality burden sits |
| [0005](0005-pinned-pseudorandom-algorithm.md) | A pinned, reference-verified pseudorandom algorithm |
| [0006](0006-bounded-draw-acceptance-limit-correction.md) | Correcting the bounded-draw acceptance limit |
| [0007](0007-a-baseline-says-what-its-hash-covers.md) | A baseline says what its hash covers |
| [0008](0008-multi-targeting-so-adoption-is-not-an-upgrade.md) | Multi-targeting, so adoption is not a framework upgrade |
| [0009](0009-what-the-source-blacklists-do-not-prove.md) | What the source blacklists do not prove |
| [0010](0010-enforcement-stops-at-the-package-boundary.md) | Enforcement stops at the package boundary (superseded by 0011) |
| [0011](0011-shipping-a-determinism-analyzer.md) | Shipping a determinism analyzer |
| [0012](0012-a-development-tree-is-not-a-release-candidate.md) | A development tree is not a release candidate |
| [0013](0013-the-analyzer-ships-when-its-rules-cover-what-it-claims.md) | The analyzer ships when its rules cover what it claims |
| [0014](0014-warning-where-unordered-becomes-ordered.md) | Warning where unordered becomes ordered |
| [0015](0015-calibrating-the-rule-set-before-it-freezes.md) | Calibrating the rule set before it freezes |
| [0016](0016-retiring-rk0006-into-a-narrowed-rk0003.md) | Retiring RK0006 into a narrowed RK0003 |
| [0017](0017-a-held-tag-returns-main-to-a-development-version.md) | A held tag returns main to a development version |
| [0018](0018-resolution-enforces-handling-not-honesty.md) | Resolution enforces handling, not honesty |
| [0019](0019-replay-identity-is-necessary-not-sufficient.md) | Replay identity is necessary, not sufficient |
