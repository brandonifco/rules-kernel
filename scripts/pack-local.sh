#!/usr/bin/env bash
# pack-local.sh -- pack the packages for local inspection, under a version that can never
# be mistaken for a release.
#
# Packing with the repository's own VersionPrefix and restoring from the resulting folder
# writes an entry into ~/.nuget/packages under that exact version, recording a local folder
# as its source. If that version is later published, every restore on the machine keeps
# serving the local copy: same version number, different bytes, no warning. It happened --
# a pre-release build of 0.2.0 packed for a compatibility check shadowed the published
# 0.2.0 for hours, and surfaced only as a compiler error about a constructor overload that
# the published package does have.
#
# This is the local-feed hazard in a second guise. The first was NU1403, a content-hash
# mismatch, which is loud. This one is silent, which makes it worse.
#
#   ./scripts/pack-local.sh [output-dir]
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

OUT="${1:-$REPO_ROOT/artifacts/local}"
BASE="$(python3 -c "
import re,pathlib
t=pathlib.Path('Directory.Build.props').read_text()
print(re.search(r'<VersionPrefix>([^<]+)</VersionPrefix>', t).group(1))")"
# A prerelease suffix sorts below the release and cannot collide with it. The timestamp
# makes successive local packs distinct too, so one stale build cannot shadow the next.
VERSION="$BASE-local.$(date -u +%Y%m%d%H%M%S)"

rm -rf "$OUT"
dotnet pack RulesKernel.slnx -c Release -p:Version="$VERSION" -o "$OUT"

printf '\npacked %s to %s\n' "$VERSION" "$OUT"
printf 'Reference it explicitly; it can never be confused with a published %s.\n' "$BASE"
