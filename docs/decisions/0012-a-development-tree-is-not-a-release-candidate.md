# 0012 — A development tree is not a release candidate

## Status

Accepted — 2026-09-15.

## Context

`main` carried `<VersionPrefix>0.2.0</VersionPrefix>` for the entire life of the 0.2.0
release and for everything built after it: a new packaged assembly, two decision records
and a tenth repository check. Every ordinary `dotnet pack` on that tree produced bytes
labelled `0.2.0` — a version already published to nuget.org, with different contents.

That is not a hypothetical. Restoring such a pack writes an entry into `~/.nuget/packages`
under the published version, recording a local folder as its source, and every later restore
on the machine serves the local bytes under the released number. It happened here: a
pre-release build of 0.2.0, packed for a compatibility check, shadowed the published 0.2.0
for hours and surfaced only as a compiler error about a constructor the published package
does have. `scripts/pack-local.sh` exists because of that incident and packs under a
timestamped prerelease suffix that cannot collide with anything.

`pack-local.sh` is a good mitigation and it is not enough, because it is opt-in. The
dangerous command is the default one — `dotnet pack`, which every contributor already knows
— and the safe one has to be discovered in a document. A hazard whose safety catch is a
different command is still a hazard.

The version number on `main` is also a claim, and it was a false one. "Which commit is
0.2.0" had at least two answers for months: the tag, and whatever `main` happened to be.
For a library whose entire product is replay identity, that question must have exactly one
answer, permanently.

## Decision

**`main` is never a release version.** It carries the *next* version with a prerelease
suffix: `<VersionPrefix>0.3.0</VersionPrefix>` plus `<VersionSuffix>dev</VersionSuffix>`,
resolving to `0.3.0-dev`. That version has never been published and never will be — the
cycle skips past it — so a default pack on a development tree produces something that cannot
shadow anything on nuget.org.

**A release is the commit that clears the suffix.** The release PR clears `VersionSuffix`,
sets `VersionPrefix` to the version being released, promotes the public API baselines —
`tools/release-checks.py` fails the publish while any `PublicAPI.Unshipped.txt` still holds a
declaration — and is tagged. `main` then moves to the next prefix with the suffix restored.
Exactly one commit in the repository's history ever resolves to a given release version, and
it is the tagged one.

**The tag is checked against the resolved version, not against `VersionPrefix`.** This is
the part that is easy to get subtly wrong, and was. `.github/workflows/publish.yml`
previously compared the tag to `<VersionPrefix>` alone; with a suffix in play, a tree
resolving to `0.3.0-dev` passes that comparison under tag `v0.3.0` and then publishes
`0.3.0-dev`. A package whose version disagrees with the tag it was cut from is the same
class of provenance defect as the relabelled-pack bug that workflow's header describes
having already fixed once — and nuget.org does not allow a correction, only an unlisting.

So the gate asks MSBuild for `PackageVersion` on every packable project — the property
`dotnet pack` actually stamps — and requires exact string equality with the tag. Prefix,
suffix, a per-project override and any future property are all folded into that answer
already. Nothing re-implements MSBuild's version resolution, because a re-implementation is
a thing that can drift from the resolution it claims to check.

## Alternatives rejected

**Leave the prefix at the released version and rely on `pack-local.sh`.** The status quo.
It makes correctness depend on a contributor remembering a document before running a
command they already know, and it leaves `main`'s version number saying something untrue.

**Bump `VersionPrefix` to `0.3.0` with no suffix.** Better, and still wrong in the same way
one step later: `main` would then be labelled as 0.3.0 for the whole development of 0.3.0,
and the shadowing hazard returns on the day 0.3.0 is published. The suffix, not the number,
is what makes a development tree unmistakable.

**Derive the version from the tag at pack time** (`-p:Version=<tag>`). Already tried and
already reverted, for a reason this repository should not have to learn twice: pack
relabelled a package without rebuilding, so the package version and the assembly's
`AssemblyInformationalVersion` could disagree. The version must come from the build, which
means it must come from source, which means the tag can only ever be a check on it.

## Consequences

`dotnet pack` on `main` now produces `0.3.0-dev`. A local feed built from it is harmless;
`pack-local.sh` stays regardless, because a timestamped `0.3.0-local.<stamp>` is still the
right thing for successive local packs — two packs of `0.3.0-dev` are two different builds
under one version, which is the same failure in miniature.

Releasing now requires a deliberate commit that a reviewer can see as a release: it changes
`VersionSuffix` and the API baselines together. A tag pushed at any other commit fails the
gate before anything is built or packed, with an error naming the resolved version and the
tag it disagreed with.

The cycle is written out in `CLAUDE.md` under "The release cycle", because that is the file a
contributor reads before changing anything, and a convention that lives only in a decision
record is one the next person follows by luck.
