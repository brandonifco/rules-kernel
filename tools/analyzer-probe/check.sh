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

scaffold() {
  local dir="$1" body="$2"
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

if dotnet build "$WORK/dirty/Consumer.csproj" -c Release >"$WORK/dirty.log" 2>&1; then
  echo "FAIL: a consumer calling Random.Shared built successfully; the analyzer did not reach it"
  status=1
elif ! grep -q 'RK0001' "$WORK/dirty.log"; then
  echo "FAIL: the consumer build failed, but not with RK0001 -- the failure is unrelated"
  sed -n '/error/p' "$WORK/dirty.log" | head -5
  status=1
else
  echo "ok   a consumer calling Random.Shared fails with RK0001"
fi

if dotnet build "$WORK/clean/Consumer.csproj" -c Release >"$WORK/clean.log" 2>&1; then
  echo "ok   a consumer resolving from its arguments builds clean"
else
  echo "FAIL: a consumer with no ambient input failed to build"
  sed -n '/error/p' "$WORK/clean.log" | head -5
  status=1
fi

exit "$status"
