#!/usr/bin/env bash
# check.sh -- prove a single-framework restore cannot rewrite a multi-targeted lock file.
#
# Dependabot's NuGet discovery runs, in each project directory:
#   dotnet msbuild <project> /t:Restore,ResolveProjectReferences,GenerateBuildDependencyFile
#       /p:ProjectReferenceBuildTargets="Restore;ResolveProjectReferences;GenerateBuildDependencyFile"
# (dependabot-core, nuget/helpers/lib/NuGetUpdater/NuGetUpdater.Core/Discover/SdkProjectDiscovery.cs).
# Through the project references, that restored RulesKernel, RulesKernel.Randomness and
# RulesKernel.Testing with one TargetFramework each and wrote net10.0-only lock files: issue #63,
# reproduced exactly this way. Directory.Build.targets redirects the lock file in that context.
#
# This runs the same invocation from every test project in a clean copy of the tree and fails
# if any tracked packages.lock.json changed. It reads the copy's own `git status`, so a lock file
# that changed is named, not inferred.
#
#   tools/lockfile-probe/check.sh
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

# A copy of the working tree -- tracked files plus untracked ones that are not ignored, the
# inventory tools/repo-checks.py examines -- committed into a scratch repository so its lock
# files have a baseline to diff against. This checkout's obj/ and lock files are never touched.
mkdir -p "$WORK/tree"
(cd "$REPO_ROOT" && git ls-files -z --cached --others --exclude-standard | xargs -0 cp --parents -t "$WORK/tree")
(cd "$WORK/tree" && git init -q && git add -A \
  && git -c user.name=probe -c user.email=probe@example.invalid commit -qm baseline)

mapfile -t projects < <(cd "$WORK/tree" && grep -rl --include=*.csproj '<IsTestProject>true</IsTestProject>' tests probes | sort)
if [[ "${#projects[@]}" -eq 0 ]]; then
  echo "FAIL: no test projects found to restore from -- this check proved nothing"
  exit 1
fi

for project in "${projects[@]}"; do
  if ! (cd "$WORK/tree/$(dirname "$project")" && dotnet msbuild "$(basename "$project")" \
        "/t:Restore,ResolveProjectReferences,GenerateBuildDependencyFile" \
        '/p:ProjectReferenceBuildTargets="Restore;ResolveProjectReferences;GenerateBuildDependencyFile"' \
        /p:TreatWarningsAsErrors=false -nologo -v:q) >"$WORK/restore.log" 2>&1; then
    echo "FAIL: the Dependabot-style restore of $project did not run"
    tail -10 "$WORK/restore.log"
    exit 1
  fi
done

changed="$(cd "$WORK/tree" && git status --porcelain -- '*packages.lock.json')"
if [[ -n "$changed" ]]; then
  echo "FAIL: a Dependabot-style restore rewrote lock files (issue #63):"
  printf '%s\n' "$changed" | sed 's/^/     /'
  exit 1
fi
echo "ok   ${#projects[@]} Dependabot-style restore(s) left every packages.lock.json unchanged"
