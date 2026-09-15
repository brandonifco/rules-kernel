#!/usr/bin/env bash
# check.sh -- prove the packages are usable by a real consumer, on every framework they
# claim, at runtime and not merely at compile time.
#
# The gate builds RulesKernel, RulesKernel.Randomness and RulesKernel.Testing from source
# and runs their tests against project references. A consumer does none of that: it
# restores a .nupkg and binds against whatever is inside it. Those two are not the same
# artifact, and the difference has already cost this repository a release. 0.1.0 shipped
# lib/net10.0 only and locked SRD_Combat -- pinned to SDK 8.0.129 with rollForward
# disabled -- out of even the dependency-free base assembly. Every test passed.
# docs/decisions/0008 records it, and records how it was actually found: by building a real
# net8.0 consumer against the packed packages and running it, not by inspecting the nupkg
# layout.
#
# tools/analyzer-probe/check.sh generalizes to exactly this, and this script is deliberately
# built in its image. The difference is the last step. The analyzer probe asks the compiler
# a question and reads its answer, which is the right question for an analyzer, because an
# analyzer's entire output IS compiler diagnostics. A library's is not. A package can
# restore, reference and compile while being wrong the moment it executes: a lib folder
# for the right framework holding an assembly built for the wrong one, a dependency that
# did not flow because something was marked DevelopmentDependency, an IL-trimmed build
# whose argument validation was optimised away. So this one COMPILES AND RUNS a consumer
# and asserts on its output.
#
# Two runs per framework, because either alone proves nothing:
#   1. the consumer's assertions PASS, and it reports the exact number it made
#   2. the same consumer, asked to assert a knowingly false expectation, FAILS
# Without (2), a consumer whose assertion helper had been reduced to a no-op -- or whose
# `Main` returned 0 unconditionally -- would report a clean sweep of assertions it never
# actually made, and this script would print ok. That is the same vacuity the analyzer
# probe's second build exists to prevent, in the other direction.
#
# The values asserted are not this repository's own output echoed back at itself. The PCG32
# draws are the canonical seed-42/stream-54 sequence from the upstream reference
# implementation, the same numbers tests/RulesKernel.Randomness.Tests/Pcg32ReferenceVectors.cs
# carries and upstream's own check-pcg32.out records. A package that restores and runs but
# produces different numbers is a broken replay contract, and that is precisely what this
# would catch.
#
#   tools/package-probe/check.sh
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$REPO_ROOT"

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

# The exact number of assertions Program.cs makes on a successful run. Checked, rather
# than merely "more than none", because the failure this guards against is a whole block
# of assertions quietly ceasing to run -- a `return` added while debugging, a section
# wrapped in a condition that stopped being true. A floor would not notice that; an exact
# count does. It is one number to update when the program deliberately gains or loses an
# assertion, and the mismatch message says so.
ASSERTIONS_EXPECTED=32

# Never the repository's own VersionPrefix. Packing under it and restoring from a local
# folder writes a ~/.nuget/packages entry under a version that may later be published, and
# every restore on the machine then serves the local bytes under the released version,
# silently. It has happened here -- see scripts/pack-local.sh, whose header records the
# incident. A timestamped prerelease suffix sorts below the release and cannot collide
# with it.
BASE="$(python3 -c "
import re,pathlib
t=pathlib.Path('Directory.Build.props').read_text()
print(re.search(r'<VersionPrefix>([^<]+)</VersionPrefix>', t).group(1))")"
VERSION="$BASE-pkgprobe.$(date -u +%Y%m%d%H%M%S)"

FEED="$WORK/feed"
pack() {
  local project="$1"
  dotnet pack "$project" -c Release -p:Version="$VERSION" -o "$FEED" \
      >>"$WORK/pack.log" 2>&1 || {
    echo "FAIL: packing $project did not succeed"; tail -20 "$WORK/pack.log"; return 1
  }
}

pack src/RulesKernel/RulesKernel.csproj
pack src/RulesKernel.Randomness/RulesKernel.Randomness.csproj
# Published from tests/ but genuinely shipped (docs/decisions/0001), so a consumer's test
# suite depends on it exactly as much as on the other two.
pack tests/RulesKernel.Testing/RulesKernel.Testing.csproj

# An isolated package root, so this can neither read nor poison the machine's real cache.
# Reading it would be the worse of the two: a stale entry under this version could satisfy
# the restore and the probe would then be testing bytes it did not pack.
export NUGET_PACKAGES="$WORK/packages"

# The consumer's own expectations that depend on which framework it was built for. Kept in
# a generated file so Program.cs can be written verbatim, with no shell expansion running
# over C# interpolated strings.
write_expected() {
  local dir="$1" tfm="$2" runtime_prefix="$3"
  cat > "$dir/Expected.cs" <<EOF
internal static class Expected
{
    public const string TargetFramework = "$tfm";

    // The version this run packed. Asserted against the running assemblies' informational
    // versions, so "it worked" cannot mean "it bound to some other copy of the kernel".
    public const string PackageVersion = "$VERSION";

    // What RuntimeInformation.FrameworkDescription must start with. This is the assertion
    // that 0.1.0 would have failed: it is not enough that a net8.0 consumer BUILT, it has
    // to have actually executed on the .NET 8 runtime against the assembly the package's
    // lib/net8.0 folder supplied. A missing lib folder fails earlier and louder, at
    // restore; this catches the quieter shapes, such as a lib/net8.0 holding an assembly
    // that was really compiled for something else.
    public const string RuntimePrefix = "$runtime_prefix";
}
EOF
}

scaffold() {
  local dir="$1" tfm="$2" runtime_prefix="$3"
  mkdir -p "$dir"
  cat > "$dir/nuget.config" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="probe" value="$FEED" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
EOF
  # No RollForward setting on purpose: a framework-dependent net8.0 app will not roll
  # forward to a .NET 10 runtime by itself, so "it ran" means it ran on .NET 8. Letting it
  # roll forward would quietly turn both halves of this probe into the same test.
  cat > "$dir/Consumer.csproj" <<EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>$tfm</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <InvariantGlobalization>true</InvariantGlobalization>
  </PropertyGroup>
  <!-- RulesKernel.Randomness is deliberately NOT referenced here, and Program.cs uses it
       anyway. It has to arrive as RulesKernel.Testing's declared package dependency, which
       is the one packaging failure mode that unit tests can never see. A dependency that
       does not flow (marked DevelopmentDependency, given PrivateAssets, or packed with no
       dependency group for this framework) produces a package that restores perfectly and
       then will not compile a line that uses it. Referencing all three would hide exactly
       that. A double hyphen is illegal inside an XML comment, which is why this one reads
       the way it does; see the parseable check in tools/repo-checks.py. -->
  <ItemGroup>
    <PackageReference Include="RulesKernel" Version="$VERSION" />
    <PackageReference Include="RulesKernel.Testing" Version="$VERSION" />
  </ItemGroup>
</Project>
EOF
  write_expected "$dir" "$tfm" "$runtime_prefix"
  cp "$REPO_ROOT/tools/package-probe/Program.cs" "$dir/Program.cs"
}

status=0
frameworks_run=0

probe_framework() {
  local tfm="$1" major="$2"
  local runtime_prefix=".NET $major."

  # A check that proves nothing says so. If the matching runtime is not installed, this
  # framework was not exercised, and reporting ok for it would be a claim about bytes
  # nothing here executed. The CI workflows install both runtimes for exactly this reason;
  # see the setup step in .github/workflows/build-and-test.yml.
  if ! dotnet --list-runtimes | grep -q "^Microsoft.NETCore.App $major\."; then
    printf 'skip %s (no Microsoft.NETCore.App %s.x runtime installed; nothing was executed)\n' \
        "$tfm" "$major"
    return 0
  fi

  local dir="$WORK/$tfm"
  scaffold "$dir" "$tfm" "$runtime_prefix"

  if ! dotnet build "$dir/Consumer.csproj" -c Release >"$WORK/$tfm.build.log" 2>&1; then
    echo "FAIL: a $tfm consumer could not build against the packed packages"
    sed -n '/error/p' "$WORK/$tfm.build.log" | head -10
    status=1
    return 0
  fi

  if ! dotnet run --project "$dir/Consumer.csproj" -c Release --no-build \
      >"$WORK/$tfm.run.log" 2>&1; then
    echo "FAIL: a $tfm consumer built but did not run clean against the packed packages"
    sed -n '/ASSERTION FAILED/p;/Unhandled exception/,+3p' "$WORK/$tfm.run.log" | head -20
    status=1
    return 0
  fi

  local claimed
  claimed="$(sed -n 's/^package-probe: \([0-9][0-9]*\) assertion(s) passed on .*/\1/p' \
      "$WORK/$tfm.run.log")"
  if [[ -z "$claimed" ]]; then
    echo "FAIL: the $tfm consumer exited 0 without reporting how many assertions it made"
    tail -5 "$WORK/$tfm.run.log"
    status=1
    return 0
  fi
  if [[ "$claimed" -ne "$ASSERTIONS_EXPECTED" ]]; then
    echo "FAIL: the $tfm consumer made $claimed assertion(s), expected $ASSERTIONS_EXPECTED."
    echo "      A block of assertions stopped running, or Program.cs deliberately changed"
    echo "      and ASSERTIONS_EXPECTED in this script was not updated with it."
    status=1
    return 0
  fi

  # The negative control. Same binary, same real draws, one knowingly false expectation.
  # If this run also succeeds, the assertion helper is inert and the run above proved
  # nothing at all -- which is the only way the whole probe can be silently worthless.
  if dotnet run --project "$dir/Consumer.csproj" -c Release --no-build -- --negative-control \
      >"$WORK/$tfm.control.log" 2>&1; then
    echo "FAIL: the $tfm consumer passed while asserting a knowingly false expectation."
    echo "      Its assertions do not bite, so the successful run above is vacuous."
    status=1
    return 0
  fi
  if ! grep -q 'ASSERTION FAILED' "$WORK/$tfm.control.log"; then
    echo "FAIL: the $tfm negative control failed, but not with an assertion failure --"
    echo "      the failure is unrelated, so it still does not show the assertions bite."
    tail -10 "$WORK/$tfm.control.log"
    status=1
    return 0
  fi

  local reported
  reported="$(sed -n 's/^package-probe: [0-9][0-9]* assertion(s) passed on \(.*\)/\1/p' \
      "$WORK/$tfm.run.log")"
  printf 'ok   %s consumer ran %s assertions against the packed packages on %s\n' \
      "$tfm" "$claimed" "$reported"
  frameworks_run=$((frameworks_run + 1))
}

# Both halves of the docs/decisions/0008 commitment, in the order that matters: net8.0 is
# the one that was dropped.
probe_framework net8.0 8
probe_framework net10.0 10

if [[ "$frameworks_run" -eq 0 ]]; then
  echo "FAIL: no target framework was actually exercised -- this check proved nothing."
  echo "      Install the runtimes for the frameworks docs/decisions/0008 commits to, or"
  echo "      stop reporting on them."
  status=1
fi

exit "$status"
