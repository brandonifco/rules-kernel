#!/usr/bin/env bash
# check.sh -- prove the shipped analyzer reaches a consumer.
#
# The unit tests in tests/RulesKernel.Analyzers.Tests drive the analyzer as a library. That
# proves the rules, and proves nothing about packaging: an analyzer that lands in lib/
# instead of analyzers/dotnet/cs, or is built against a Roslyn the consumer cannot load,
# passes every one of them and then does nothing in a real build. This builds a consuming
# project against the packed .nupkg and asserts on the compiler's actual output.
#
# Two assertions, because either alone proves nothing:
#   1. a consumer that draws from ambient entropy FAILS, naming RK0001
#   2. a consumer that resolves from its arguments SUCCEEDS
# Without (2), an analyzer that flagged every line, or a build broken for an unrelated
# reason, would pass this check.
#
# Then the same analyzer over this repository's own packaged sources:
#   3. every packaged project, built from a copy of this tree with its own settings and the
#      analyzer attached, compiles with no RK diagnostic
#   4. the same copy with one ambient draw added FAILS, naming RK0001
# Before this, the kernel shipped an analyzer it had never run over itself, and
# docs/architecture.md said so. (4) is what makes (3) evidence: without it, an analyzer that
# never loaded in that build would report a clean kernel.
#
# The analyzer is attached from outside, through MSBuild's CustomAfterMicrosoftCommonTargets
# hook, to a COPY of the tree. No project, props file or lock file in the repository names
# it, so it stays out of the dependency graph tools/repo-checks.py --only layering enforces
# (docs/decisions/0011), and nothing built here touches the repository's own obj/.
#
#   tools/analyzer-probe/check.sh
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$REPO_ROOT"

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

# Never the repository's own VersionPrefix: restoring from a local folder under a version
# that may later be published makes every restore on this machine serve the local bytes,
# silently. See scripts/pack-local.sh, which this deliberately mirrors.
BASE="$(python3 -c "
import re,pathlib
t=pathlib.Path('Directory.Build.props').read_text()
print(re.search(r'<VersionPrefix>([^<]+)</VersionPrefix>', t).group(1))")"
VERSION="$BASE-probe.$(date -u +%Y%m%d%H%M%S)"

FEED="$WORK/feed"
dotnet pack src/RulesKernel.Analyzers/RulesKernel.Analyzers.csproj \
    -c Release -p:Version="$VERSION" -o "$FEED" >"$WORK/pack.log" 2>&1 || {
  echo "FAIL: packing RulesKernel.Analyzers did not succeed"; cat "$WORK/pack.log"; exit 1
}

# An isolated package root, so this can neither read nor poison the machine's real cache.
export NUGET_PACKAGES="$WORK/packages"

# The compiler, not just the target framework. The Roslyn pin in Directory.Packages.props
# exists so the analyzer loads under SDK 8.0.x, whose compiler is Roslyn 4.8
# (docs/decisions/0011). A consumer scaffolded under $WORK finds no global.json and builds with
# the newest SDK on the machine, so until this was added every run of this probe loaded the
# analyzer into a current compiler, and the floor it claims to protect was never exercised.
SDK8="$(cd "$WORK" && dotnet --list-sdks | awk '/^8\.0\./ { version = $1 } END { print version }')"
if [[ -z "$SDK8" ]]; then
  echo "FAIL: no .NET 8.0 SDK is installed, so nothing here can load the analyzer under the"
  echo "      Roslyn 4.8 compiler it is pinned for. CI installs one (build-and-test.yml)."
  exit 1
fi

scaffold() {
  local dir="$1" body="$2"
  mkdir -p "$dir"
  printf '{ "sdk": { "version": "%s", "rollForward": "disable" } }\n' "$SDK8" > "$dir/global.json"
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
  # net8.0 on purpose: the consumer this analyzer must not lock out is the one pinned to
  # SDK 8.0.x, which is what docs/decisions/0008 and the Roslyn 4.8 pin are both about.
  cat > "$dir/Consumer.csproj" <<EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="RulesKernel.Analyzers" Version="$VERSION" PrivateAssets="all" />
  </ItemGroup>
</Project>
EOF
  cat > "$dir/Consumer.cs" <<EOF
public static class Consumer
{
$body
}
EOF
}

scaffold "$WORK/dirty" '    public static int Resolve() => System.Random.Shared.Next(6);'
scaffold "$WORK/clean" '    public static int Resolve(int seed, int bound) => (seed + bound) % 6;'

status=0

selected="$(cd "$WORK/dirty" && dotnet --version)"
if [[ "$selected" != "$SDK8" ]]; then
  echo "FAIL: the consumer resolved SDK $selected, not $SDK8; the Roslyn 4.8 floor is not what ran"
  exit 1
fi
echo "ok   consumers build with SDK $SDK8, the compiler the analyzer's Roslyn pin is for"

# Both builds run FROM the consumer's directory. The dotnet host resolves global.json from the
# working directory, not from the project path, so `dotnet build "$WORK/dirty/Consumer.csproj"`
# run from the repository used the repository's SDK and ignored the pin above. That was
# verified: with the analyzer rebuilt against Roslyn 5.0, the old invocation still reported ok,
# and this one fails with CS9057. The shared compiler server is off so a server started by
# another SDK can never be the compiler that answers.

if (cd "$WORK/dirty" && dotnet build Consumer.csproj -c Release -p:UseSharedCompilation=false) >"$WORK/dirty.log" 2>&1; then
  echo "FAIL: a consumer calling Random.Shared built successfully; the analyzer did not reach it"
  status=1
elif ! grep -q 'RK0001' "$WORK/dirty.log"; then
  echo "FAIL: the consumer build failed, but not with RK0001 -- the failure is unrelated"
  sed -n '/error/p' "$WORK/dirty.log" | head -5
  status=1
else
  echo "ok   a consumer calling Random.Shared fails with RK0001"
fi

if (cd "$WORK/clean" && dotnet build Consumer.csproj -c Release -p:UseSharedCompilation=false) >"$WORK/clean.log" 2>&1; then
  echo "ok   a consumer resolving from its arguments builds clean"
else
  echo "FAIL: a consumer with no ambient input failed to build"
  sed -n '/error/p' "$WORK/clean.log" | head -5
  status=1
fi

# -- 3 and 4: the kernel's own packaged sources -------------------------------------------

# The analyzer exactly as the package ships it, not the build output: a consumer loads the
# copy in analyzers/dotnet/cs, and so does this.
ANALYZER_DLL="$WORK/analyzer/RulesKernel.Analyzers.dll"
python3 - "$FEED/RulesKernel.Analyzers.$VERSION.nupkg" "$ANALYZER_DLL" <<'PY'
import pathlib, sys, zipfile
with zipfile.ZipFile(sys.argv[1]) as package:
    data = package.read("analyzers/dotnet/cs/RulesKernel.Analyzers.dll")
target = pathlib.Path(sys.argv[2])
target.parent.mkdir(parents=True)
target.write_bytes(data)
PY

cat > "$WORK/dogfood.targets" <<TARGETS
<Project>
  <ItemGroup>
    <Analyzer Include="$ANALYZER_DLL" />
  </ItemGroup>
</Project>
TARGETS

# The tree the next commit would contain: tracked files plus untracked ones that are not
# ignored, the same inventory tools/repo-checks.py examines.
KERNEL="$WORK/kernel"
mkdir -p "$KERNEL"
git ls-files -z --cached --others --exclude-standard | xargs -0 cp --parents -t "$KERNEL"

# Packaged means what tools/repo-checks.py says it means, read from the csproj on disk, so a
# new package is dogfooded without anyone remembering to add it here.
mapfile -t PACKAGED < <(python3 - "$KERNEL" <<'PY'
import importlib.util, pathlib, sys
spec = importlib.util.spec_from_file_location("repo_checks", "tools/repo-checks.py")
module = importlib.util.module_from_spec(spec)
sys.modules["repo_checks"] = module
spec.loader.exec_module(module)
root = pathlib.Path(sys.argv[1])
for path in sorted(module.packaged_projects(root).values()):
    print(path.relative_to(root))
PY
)

if [[ "${#PACKAGED[@]}" -eq 0 ]]; then
  echo "FAIL: no packaged project found to run the analyzer over -- this proved nothing"
  exit 1
fi

# Only the probe package needed an isolated cache. Everything the kernel restores is a
# published package, so the machine cache is safe here and saves a full download.
unset NUGET_PACKAGES

dogfood_build() {
  local project="$1" log="$2"
  dotnet build "$KERNEL/$project" -c Release -warnaserror \
      -p:CustomAfterMicrosoftCommonTargets="$WORK/dogfood.targets" >"$log" 2>&1
}

for project in "${PACKAGED[@]}"; do
  name="$(basename "$project" .csproj)"
  log="$WORK/dogfood.$name.log"
  if ! dogfood_build "$project" "$log"; then
    echo "FAIL: $name does not build clean under its own analyzer"
    grep -oE '[^ ]+\.cs\([0-9,]+\): error [A-Z]+[0-9]+: [^[]*' "$log" | sort -u | head -20 | sed 's/^/     /'
    status=1
  elif grep -qE '(warning|error) RK[0-9]{4}' "$log"; then
    echo "FAIL: $name built, but reported RK diagnostics"
    grep -oE '[^ ]+\.cs\([0-9,]+\): (warning|error) RK[0-9]+: [^[]*' "$log" | sort -u | head -20 | sed 's/^/     /'
    status=1
  else
    echo "ok   $name builds clean under RulesKernel.Analyzers"
  fi
done

# The negative control, in the floor itself: one ambient draw must fail this exact build.
cat > "$KERNEL/src/RulesKernel/DogfoodControl.cs" <<'CS'
namespace RulesKernel;

internal static class DogfoodControl
{
    internal static int Draw() => System.Random.Shared.Next();
}
CS
if dogfood_build src/RulesKernel/RulesKernel.csproj "$WORK/dogfood.control.log"; then
  echo "FAIL: an ambient draw added to RulesKernel built clean -- the analyzer did not load"
  echo "      into the dogfood build, so the clean results above prove nothing"
  status=1
elif ! grep -q 'error RK0001' "$WORK/dogfood.control.log"; then
  echo "FAIL: the dogfood control failed, but not with RK0001 -- the failure is unrelated"
  grep -oE 'error [A-Z]+[0-9]+: [^[]*' "$WORK/dogfood.control.log" | sort -u | head -5
  status=1
else
  echo "ok   an ambient draw added to RulesKernel fails its dogfood build with RK0001"
fi

exit "$status"
