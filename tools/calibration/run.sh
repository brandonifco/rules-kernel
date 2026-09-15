#!/usr/bin/env bash
# run.sh -- calibrate this tree against real consumer engines.
#
# The probes in probes/ are consumers this repository wrote for itself. They catch a lot,
# and they share the kernel's authors' blind spots. A real engine does not. This packs the
# working tree under a version that can never shadow a release, copies each consumer into a
# scratch directory, and asks one of two questions of it:
#
#   tools/calibration/run.sh kernel [checkout...]
#       Point the consumer's RulesKernel* pins at the pack, then restore, build with warnings
#       as errors, and run its tests. Pass/fail. For any change to a kernel type.
#
#   tools/calibration/run.sh analyzer [checkout...]
#       Attach the packed RulesKernel.Analyzers to every project from outside (MSBuild's
#       CustomAfterMicrosoftCommonTargets, as tools/analyzer-probe/check.sh does), build with
#       RK diagnostics kept as warnings so the build does not stop at the first one
#       (docs/decisions/0015 lost 11 of 17 findings that way), and count unique findings per
#       rule. A count, not a verdict: whether a finding is true is a reviewer's reading.
#
# With no checkouts, the consumers listed in tools/calibration/consumers.json for that mode
# are cloned at their pinned commits. Nothing in a consumer's own checkout is modified.
#
# The output is what docs/calibration/<version>.md records, and tools/release-checks.py
# requires that record at a tag (docs/decisions/0021).
#
# Environment:
#   CALIBRATION_SDKS  space-separated "major.minor=version" pairs, e.g. "10.0=10.0.111". A
#                     consumer whose global.json pins that band builds with the given SDK
#                     instead. For a machine without the pinned patch release; a record made
#                     with it must say so.
#   CALIBRATION_KEEP  keep the scratch directory and print its path.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
MODE="${1:-}"
if [[ "$MODE" != kernel && "$MODE" != analyzer ]]; then
  echo "usage: tools/calibration/run.sh kernel|analyzer [consumer-checkout...]" >&2
  exit 2
fi
shift

WORK="$(mktemp -d)"
if [[ -n "${CALIBRATION_KEEP:-}" ]]; then
  echo "scratch: $WORK"
else
  trap 'rm -rf "$WORK"' EXIT
fi

# -- the consumers ---------------------------------------------------------------------------

consumers=()
if [[ "$#" -gt 0 ]]; then
  for checkout in "$@"; do consumers+=("$(cd "$checkout" && pwd)"); done
else
  while IFS=$'\t' read -r name repository commit; do
    target="$WORK/checkouts/$name"
    git init -q "$target"
    git -C "$target" fetch -q --depth 1 "$repository" "$commit" \
      || { echo "FAIL: could not fetch $name at $commit from $repository"; exit 1; }
    git -C "$target" checkout -q FETCH_HEAD
    consumers+=("$target")
  done < <(python3 - "$REPO_ROOT/tools/calibration/consumers.json" "$MODE" <<'PY'
import json, sys
manifest = json.load(open(sys.argv[1], encoding="utf-8"))
for consumer in manifest["consumers"]:
    if sys.argv[2] in consumer["modes"]:
        print(consumer["name"], consumer["repository"], consumer["commit"], sep="\t")
PY
)
fi

if [[ "${#consumers[@]}" -eq 0 ]]; then
  echo "FAIL: no consumers to calibrate against -- a calibration over nothing proves nothing"
  exit 1
fi

# -- the pack ---------------------------------------------------------------------------------

BASE="$(python3 -c "
import re,pathlib
t=pathlib.Path('$REPO_ROOT/Directory.Build.props').read_text()
print(re.search(r'<VersionPrefix>([^<]+)</VersionPrefix>', t).group(1))")"
VERSION="$BASE-calibration.$(date -u +%Y%m%d%H%M%S)"
FEED="$WORK/feed"
(cd "$REPO_ROOT" && dotnet pack RulesKernel.slnx -c Release -p:Version="$VERSION" -o "$FEED") \
    >"$WORK/pack.log" 2>&1 || { echo "FAIL: packing the kernel"; tail -20 "$WORK/pack.log"; exit 1; }

# An isolated cache: the pack must not be served to anything else on the machine, and
# nothing already cached may stand in for it.
export NUGET_PACKAGES="$WORK/packages"

kernel_commit="$(git -C "$REPO_ROOT" rev-parse HEAD)"
dirty=""
[[ -n "$(git -C "$REPO_ROOT" status --porcelain)" ]] && dirty=" (plus uncommitted changes -- not recordable)"
echo "mode: $MODE"
echo "kernel: $kernel_commit$dirty, packed as $VERSION"
[[ -n "${CALIBRATION_SDKS:-}" ]] && echo "sdk overrides: $CALIBRATION_SDKS"

if [[ "$MODE" == analyzer ]]; then
  ANALYZER_DLL="$WORK/analyzer/RulesKernel.Analyzers.dll"
  python3 - "$FEED/RulesKernel.Analyzers.$VERSION.nupkg" "$ANALYZER_DLL" <<'PY'
import pathlib, sys, zipfile
with zipfile.ZipFile(sys.argv[1]) as package:
    data = package.read("analyzers/dotnet/cs/RulesKernel.Analyzers.dll")
target = pathlib.Path(sys.argv[2])
target.parent.mkdir(parents=True)
target.write_bytes(data)
PY
  RULES="$(python3 - "$REPO_ROOT/src/RulesKernel.Analyzers/AnalyzerReleases.Shipped.md" "$REPO_ROOT/src/RulesKernel.Analyzers/AnalyzerReleases.Unshipped.md" <<'PY'
import re, sys
ids = set()
for path in sys.argv[1:]:
    ids.update(re.findall(r"^(RK\d{4})\s*\|", open(path, encoding="utf-8").read(), re.M))
print(";".join(sorted(ids)))
PY
)"
  printf '<Project>\n  <ItemGroup>\n    <Analyzer Include="%s" />\n  </ItemGroup>\n  <PropertyGroup>\n    <WarningsNotAsErrors>$(WarningsNotAsErrors);%s</WarningsNotAsErrors>\n  </PropertyGroup>\n</Project>\n' \
      "$ANALYZER_DLL" "$RULES" > "$WORK/analyzer.targets"
  echo "rules: ${RULES//;/ }"
fi

# -- each consumer ----------------------------------------------------------------------------

status=0
echo
if [[ "$MODE" == kernel ]]; then
  echo "| consumer | commit | pins | tests | result |"
  echo "|---|---|---|---|---|"
else
  echo "| consumer | commit | findings by rule | total |"
  echo "|---|---|---|---|"
fi

for source in "${consumers[@]}"; do
  name="$(basename "$source")"
  commit="$(git -C "$source" rev-parse HEAD 2>/dev/null || echo unknown)"
  short="${commit:0:7}"
  copy="$WORK/consumers/$name"
  mkdir -p "$copy"

  # Files only: a consumer can carry a nested checkout (an agent worktree) that git lists as
  # a single gitlink entry, and it is not part of that consumer.
  python3 - "$source" "$copy" <<'PY'
import pathlib, shutil, subprocess, sys
source, copy = pathlib.Path(sys.argv[1]), pathlib.Path(sys.argv[2])
listed = subprocess.run(
    ["git", "ls-files", "-z", "--cached", "--others", "--exclude-standard"],
    cwd=source, capture_output=True, check=True).stdout.decode().split("\0")
for name in filter(None, listed):
    path = source / name
    if path.is_file():
        (copy / name).parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(path, copy / name)
PY

  python3 - "$copy/global.json" "${CALIBRATION_SDKS:-}" <<'PY'
import json, pathlib, sys
path, overrides = pathlib.Path(sys.argv[1]), dict(p.split("=", 1) for p in sys.argv[2].split())
if path.is_file() and overrides:
    config = json.loads(path.read_text(encoding="utf-8"))
    pinned = config.get("sdk", {}).get("version", "")
    band = ".".join(pinned.split(".")[:2])
    if band in overrides:
        config["sdk"]["version"] = overrides[band]
        path.write_text(json.dumps(config, indent=2) + "\n", encoding="utf-8")
PY

  log="$WORK/$name.log"

  if [[ "$MODE" == kernel ]]; then
    # Every RulesKernel package pin, wherever the consumer declares it, moves to this pack.
    # Counted: a consumer whose pins were not found would be calibrated against whatever
    # version it already had, and report ok.
    pins="$(python3 - "$copy" "$VERSION" <<'PY'
import pathlib, re, sys
root, version = pathlib.Path(sys.argv[1]), sys.argv[2]
pattern = re.compile(r'(Include="RulesKernel(?:\.[A-Za-z]+)?"\s+Version=")[^"]*(")')
count = 0
for path in list(root.rglob("*.props")) + list(root.rglob("*.csproj")):
    text = path.read_text(encoding="utf-8")
    updated, n = pattern.subn(lambda m: m.group(1) + version + m.group(2), text)
    if n:
        path.write_text(updated, encoding="utf-8")
        count += n
print(count)
PY
)"
    if [[ "$pins" -eq 0 ]]; then
      echo "| $name | $short | 0 | - | FAIL: no RulesKernel pin to redirect |"
      status=1
      continue
    fi
    printf '<?xml version="1.0" encoding="utf-8"?>\n<configuration>\n  <packageSources>\n    <clear />\n    <add key="calibration" value="%s" />\n    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />\n  </packageSources>\n</configuration>\n' \
        "$FEED" > "$copy/nuget.config"

    # From the copy's own directory: the dotnet host takes global.json from the working
    # directory, not from the project path (see tools/analyzer-probe/check.sh).
    if (cd "$copy" \
        && dotnet restore --force-evaluate -p:RestoreLockedMode=false \
        && dotnet build --no-restore -warnaserror -p:UseSharedCompilation=false \
        && dotnet test --no-build) >"$log" 2>&1; then
      passed="$(grep -oE 'Passed: +[0-9]+' "$log" | awk '{ total += $2 } END { print total + 0 }')"
      if [[ "$passed" -eq 0 ]]; then
        echo "| $name | $short | $pins | 0 | FAIL: no test ran |"
        status=1
      else
        echo "| $name | $short | $pins | $passed | pass |"
      fi
    else
      echo "| $name | $short | $pins | - | FAIL |"
      grep -E 'error [A-Z]+[0-9]+|Failed [A-Za-z_.]+|Failed!' "$log" | sort -u | head -20 \
        | sed 's/^/    /' >&2
      status=1
    fi
  else
    # A control beside every project: one ambient draw each. "0 findings" from a consumer is
    # only a result if the analyzer was running in that build, and this is what shows it was.
    # The controls are subtracted from the counts, and a project whose control was not seen
    # fails the run.
    controls="$(python3 - "$copy" <<'PY'
import pathlib, sys
root = pathlib.Path(sys.argv[1])
count = 0
for csproj in sorted(root.rglob("*.csproj")):
    if any(part in {"bin", "obj"} for part in csproj.relative_to(root).parts):
        continue
    (csproj.parent / "RulesKernelCalibrationControl.cs").write_text(
        "internal static class RulesKernelCalibrationControl\n{\n"
        "    internal static int Draw() => System.Random.Shared.Next();\n}\n", encoding="utf-8")
    count += 1
print(count)
PY
)"
    if ! (cd "$copy" && dotnet build -p:UseSharedCompilation=false \
          -p:CustomAfterMicrosoftCommonTargets="$WORK/analyzer.targets") >"$log" 2>&1; then
      echo "| $name | $short | build failed for a reason other than an RK finding | - |"
      grep -E 'error [A-Z]+[0-9]+' "$log" | sort -u | head -10 | sed 's/^/    /' >&2
      status=1
      continue
    fi
    summary="$(python3 - "$log" "$controls" <<'PY'
import collections, re, sys
seen, control_files = set(), set()
for line in open(sys.argv[1], encoding="utf-8", errors="replace"):
    m = re.search(r"([^\s:]+\.cs)\((\d+),(\d+)\): warning (RK\d{4}):", line)
    if not m:
        continue
    if m.group(1).endswith("RulesKernelCalibrationControl.cs"):
        control_files.add(m.group(1))
    else:
        seen.add((m.group(1), m.group(2), m.group(3), m.group(4)))
counts = collections.Counter(rule for *_, rule in seen)
rules = ", ".join(f"{rule} {counts[rule]}" for rule in sorted(counts)) or "none"
print(f"{rules}\t{len(seen)}\t{len(control_files)}")
PY
)"
    IFS=$'\t' read -r rules total seen_controls <<<"$summary"
    if [[ "$seen_controls" -ne "$controls" ]]; then
      echo "| $name | $short | FAIL: the analyzer ran in $seen_controls of $controls project(s) | - |"
      status=1
    else
      echo "| $name | $short | $rules | $total |"
    fi
  fi
done

exit "$status"
