# Operating contract

Operating contract for this repository. Read [README.md](README.md) for what the kernel is
and [docs/architecture.md](docs/architecture.md) for how it is put together.

`AGENTS.md` is a symbolic link to this file. It was a copy, and the copy lost the held-tag
rule from the release cycle below within two days of it being written.

## What this repository is

`RulesKernel` is a versioned library that deterministic rules engines **reference**. It is
not a template and not a starter. Anything that makes it easier to copy than to reference
is working against its purpose.

## Governing principles

**1. The kernel is ruleset-agnostic.** No dice, no page numbers, no combat, no tax, no
subject-matter vocabulary. If a name only makes sense for one kind of rules, it belongs in a
domain pack, not here. `UniformInt`, not `D6`.

Nothing checks this, and a banned-vocabulary check would be guessing at what counts. It is a
review obligation, and it is listed first because it is the one most easily lost by accident
— `D6` lived in a Core assembly in the predecessor engine for months before anyone noticed
it was a claim about what kind of rules the foundation was for.

**2. Enforcement over prose.** A rule worth stating is worth a check. `tools/repo-checks.py`
is where rules become facts. If you find yourself writing "we always…" in a comment, ask
what would fail the build.

**3. A check that proves nothing says so.** A check whose inputs do not exist reports
`skip`, never `ok`. A gate that reports PASS while proving less than it claims is worse than
no gate, because it is trusted.

**4. A citation is a promise.** Every reference to a repository document must resolve;
`--only doc-references` enforces it. Never cite a document you have not written.

**5. Compatibility changes are decisions.** Changing a kernel type's shape, the reference
vectors, or the meaning of an identity component breaks every engine downstream. It needs a
record in `docs/decisions/`, not a commit message.

Half of this is now mechanical: `Microsoft.CodeAnalysis.PublicApiAnalyzers` tracks the
public surface of the four packaged assemblies, so reshaping a public member fails the
build until you update `PublicAPI.Unshipped.txt` deliberately. That forces the change to be
noticed; it cannot force the decision record. Three source-breaking changes shipped
unrecorded before this existed, and the analyzer named all three on its first run.

The reference vectors have their own path: `tools/pcg-vectors/generate.sh --check`
re-derives them from the pinned upstream commit. A weekly workflow runs it, because a wrong
vector does not fail — it passes, forever.

## Before you change anything

Run the gate. There is one:

```bash
./scripts/validate.sh full
```

## Working rules

- **Smallest coherent change.** One concern per change. If it needs "and", it is two.
- **Tests are the evidence.** A behavioural claim without a test is a claim. Draw counts,
  ordering, and equality semantics are part of the contract and are asserted directly.
- **Ordered by construction.** Never sort an ordered result afterwards; the order *is* the
  evidence.
- **Public API carries XML docs.** Packaged assemblies build with documentation required.
  Say why, not what — the code already says what.
- **Struct defaults are a real case.** A `readonly record struct` can always be produced by
  `default(T)`, bypassing its constructor. Either validate at every gate that consumes it,
  or expose `IsValid` and reject it where it matters. Both patterns are in use here.

## The release cycle

`main` is never a release version. It carries the **next** version with a prerelease
suffix — `VersionPrefix X.Y.Z` + `VersionSuffix dev`, resolving to `X.Y.Z-dev` — so an
ordinary `dotnet pack` on a development tree produces something nuget.org has never served
and never will. The alternative was tried: `main` sat at `0.2.0` for the whole life of the
0.2.0 release, and a local pack of that tree shadowed the published package for hours
([ADR 0012](docs/decisions/0012-a-development-tree-is-not-a-release-candidate.md)).

Cutting a release, in one reviewed PR:

1. Clear `VersionSuffix` and set `VersionPrefix` to the version being released.
2. Promote every `PublicAPI.Unshipped.txt` into its `PublicAPI.Shipped.txt`. What is about
   to be published is shipped API by definition, and `tools/release-checks.py` fails the
   publish while any unshipped baseline still holds a declaration.
3. If the release changes `src/RulesKernel/` or `src/RulesKernel.Analyzers/`, calibrate it
   against real consumers and commit the record in `docs/calibration/`, named for the version
   ([ADR 0021](docs/decisions/0021-external-calibration-is-a-release-gate.md)):
   `tools/calibration/run.sh kernel` (two consumers at least) and
   `tools/calibration/run.sh analyzer` (one at least). `tools/release-checks.py` fails the
   publish when the record is missing, names too few consumers, or calibrated an earlier tree.
4. `./scripts/validate.sh full`, then merge, then tag that commit `vX.Y.Z` and push the tag.
5. Immediately move `main` to the next version, suffix restored.

If the tag does not follow the merge — a last check fails, a calibration finds something —
put `VersionSuffix` back to `dev` on `main` straight away, keeping the same `VersionPrefix`.
The release has not shipped, so it is still the next one; a version number is not spent by
preparing to release it. See
[ADR 0017](docs/decisions/0017-a-held-tag-returns-main-to-a-development-version.md).

Do not shortcut step 5. Between the tag and that commit, `main` resolves to a version that
has been published — the exact state this cycle exists to make unreachable.

`publish.yml` enforces what a reviewer cannot: it asks MSBuild for the **resolved**
`PackageVersion` of every packable project and requires exact equality with the tag. A
prefix comparison is not enough — a tree resolving to `X.Y.Z-dev` matches the prefix `X.Y.Z`
and would publish a package whose version disagrees with its own tag.

## Packing locally

Use `./scripts/pack-local.sh`. Never pack with the repository's own `VersionPrefix` and
restore from the output folder: that writes a cache entry under a version number that may
later be published, and every restore on the machine then serves the local bytes under the
released version, silently. It has already happened once.

The development suffix above removes most of that hazard; this removes the rest. Two packs
of `X.Y.Z-dev` are two different builds under one version — the same failure in miniature —
and `pack-local.sh` timestamps each one so they cannot shadow each other either.

## Adding a project

Create it, then declare it in `ALLOWED_PROJECT_REFS` and `PROJECT_DIRS` in
`tools/repo-checks.py`, and add it to `RulesKernel.slnx`. An undeclared project fails the
layering check rather than being silently unenforced. That is deliberate.

## What does not belong here

Corpus extraction and adapters, domain vocabulary, mechanics, serialization, persistence,
replay execution, and anything that reads a file. Those are other repositories' jobs.
