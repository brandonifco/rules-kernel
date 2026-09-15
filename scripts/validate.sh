#!/usr/bin/env bash
# validate.sh -- the single canonical gate for this repository.
#
# Humans call it, agents call it, CI calls it. There is exactly one definition of "this
# change is acceptable" and it lives here, rather than being reimplemented in a workflow
# file where the two would silently drift apart.
#
#   ./scripts/validate.sh full   merge-equivalent gate (default)
#   ./scripts/validate.sh fast   Debug only; for the inner loop
#
# `full` requires no network beyond ordinary NuGet restore and no corpus of any kind: this
# is the kernel, and it must be verifiable by anyone who can clone it.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

MODE="${1:-full}"
SOLUTION="RulesKernel.slnx"
FAILED=0
STEP=0

export DOTNET_NOLOGO=1
export DOTNET_CLI_TELEMETRY_OPTOUT=1

if [[ -t 1 ]]; then
  BOLD=$'\033[1m'; RED=$'\033[31m'; GREEN=$'\033[32m'; YEL=$'\033[33m'; OFF=$'\033[0m'
else
  BOLD=""; RED=""; GREEN=""; YEL=""; OFF=""
fi

step() { STEP=$((STEP + 1)); printf '\n%s==> [%d] %s%s\n' "$BOLD" "$STEP" "$1" "$OFF"; }
fail() { printf '%sFAIL%s %s\n' "$RED" "$OFF" "$1"; FAILED=1; }
run() {
  local label="$1"; shift
  if "$@"; then printf '%sok%s   %s\n' "$GREEN" "$OFF" "$label"; return 0; fi
  fail "$label"; return 1
}
# Without this, a failed build is followed by `dotnet test --no-build` against stale
# binaries, which prints "ok". The overall verdict would still be FAIL, but the evidence
# someone pastes into a PR would say the tests passed. Misleading output is its own defect.
skipped() { printf '%sskip%s %s (depends on a step that failed)\n' "$YEL" "$OFF" "$1"; }

# `dotnet test` exits 0 when it finds nothing to test. Verified on this SDK (10.0.112):
# an .slnx with zero projects prints "Unable to find a project to restore!" and exits 0.
# The same silent-pass shape is reachable without anything that dramatic: a project can
# stop being part of $SOLUTION (a typo'd path, a dropped <Project> line, an accidental
# Condition) while every *other* test project still runs and passes, and the run still
# reports "ok". Exit code alone is not evidence that this solution's tests ran.
#
# The fix asks two independent questions of the actual test-run output (a TRX file per
# project, requested below), not of the exit code:
#   1. did every test project ON DISK (<IsTestProject>true</IsTestProject> in its own
#      .csproj, found by walking the repository) produce a result file? Deriving that
#      expectation from the repository rather than from $SOLUTION is the whole point: a
#      project dropped from the solution also drops out of a count taken FROM the solution,
#      so the expectation falls in step with the actual and the assertion can never fail.
#      Verified -- against a solution-derived expectation, deleting a project line from the
#      slnx still reported PASS.
#   2. did the sum of tests those files record come to more than zero? A solution that
#      declares no test projects at all -- or a result wipeout -- is caught even though
#      (1) is vacuously satisfied.
# Neither question is answerable from "dotnet test exited 0", which is the entire point.
expected_test_projects() {
  # Extracted to tools/ so it can be tested: this guard's own correctness is the point, and
  # comparing absolute path parts once made it count zero whenever the checkout itself sat
  # under a directory named like build output -- which is what a git worktree does.
  tools/expected-test-projects.py "$REPO_ROOT"
}

assert_tests_ran() {
  local results_dir="$1" expected="$2"
  python3 - "$results_dir" "$expected" <<'PY'
import glob
import sys
import xml.etree.ElementTree as ET

results_dir, expected = sys.argv[1], int(sys.argv[2])
NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}

trx_files = sorted(glob.glob(results_dir + "/**/*.trx", recursive=True))
total = 0
for f in trx_files:
    counters = ET.parse(f).getroot().find(".//t:ResultSummary/t:Counters", NS)
    if counters is not None:
        total += int(counters.get("total", "0"))

problems = []
if len(trx_files) != expected:
    problems.append(
        f"expected {expected} test-project result file(s) (from "
        f"<IsTestProject>true</IsTestProject> in the solution's own .csproj files), "
        f"found {len(trx_files)}. A test project silently stopped running."
    )
if total == 0:
    problems.append("zero tests were discovered/executed across all test projects")

if problems:
    for p in problems:
        print(f"error: {p}", file=sys.stderr)
    sys.exit(1)
print(f"{total} test(s) across {len(trx_files)} project(s) actually ran")
PY
}

EXPECTED_TEST_PROJECTS="$(expected_test_projects)"

# `env` with no VAR=val arguments before the command just execs it unchanged, so
# `test_pass Debug` (no third argument) and `test_pass Debug CI=true` share one code path.
test_pass() {
  local config="$1"; shift
  local results_dir status
  results_dir="$(mktemp -d)"
  status=0
  env "$@" dotnet test "$SOLUTION" -c "$config" --no-build --nologo \
    --logger "trx" --results-directory "$results_dir" || status=$?
  if ! assert_tests_ran "$results_dir" "$EXPECTED_TEST_PROJECTS"; then
    status=1
  fi
  rm -rf "$results_dir"
  return "$status"
}

step "SDK pin"
pinned="$(python3 -c 'import json;print(json.load(open("global.json"))["sdk"]["version"])')"
roll="$(python3 -c 'import json;print(json.load(open("global.json"))["sdk"].get("rollForward",""))')"
status=0
actual="$(dotnet --version 2>/dev/null)" || status=$?
if [[ "$roll" != "disable" ]]; then
  fail "global.json rollForward is '$roll', expected 'disable'"
elif [[ "$status" -ne 0 ]]; then
  fail "no installed SDK satisfies global.json's pin of $pinned"
elif [[ "$pinned" != "$actual" ]]; then
  fail "global.json pins $pinned but 'dotnet --version' reports $actual"
else
  printf '%sok%s   SDK %s (rollForward=%s)\n' "$GREEN" "$OFF" "$actual" "$roll"
fi

step "Restore"
# Locked mode fails the moment a fresh resolution would pick something other than what
# the committed lock files record, instead of letting a transitive version drift in
# quietly. Regenerate with `dotnet restore RulesKernel.slnx --force-evaluate` when you
# deliberately change a dependency.
run "dotnet restore --locked-mode" dotnet restore "$SOLUTION" --locked-mode || true

step "Format"
run "dotnet format --verify-no-changes" \
    dotnet format "$SOLUTION" --verify-no-changes --no-restore || true

step "Build + test (Debug)"
if run "build Debug (0 warnings)" dotnet build "$SOLUTION" -c Debug --no-restore -warnaserror; then
  run "test Debug" test_pass Debug || true
else
  skipped "test Debug"
fi

if [[ "$MODE" != "fast" ]]; then
  # This is the pass that matters most, and it changed shape from the original three:
  #
  # Release used to be built here WITHOUT CI=true, while a separate pass built Debug WITH
  # it. That meant the one configuration that is actually packed and published -- Release
  # -- never got ContinuousIntegrationBuild applied by anything *this script* controls.
  # It happened to come out right when a caller's environment already had CI=true set for
  # the whole step (as build-and-test.yml's Validate step does), because child processes
  # inherit environment variables -- but that made CI parity for the shipped bits an
  # accident of the caller, not a guarantee of the gate. A hermetic gate should not depend
  # on what its caller's shell already exported.
  #
  # So: force CI=true on the Release pass explicitly, regardless of the ambient
  # environment. This is also the exact configuration `publish.yml` now rebuilds before
  # packing (see that workflow's `publish` job), so what gets tested here, right down to
  # ContinuousIntegrationBuild's source-path normalisation, is what gets shipped. Nothing
  # from the old Release-without-CI pass is lost: optimisation-level coverage (the original
  # reason for a Release pass) and CI-env coverage are now exercised together, on the
  # combination that is actually real -- Release+CI=true is the only Release configuration
  # any workflow in this repository ever produces.
  step "Build + test (Release, CI=true -- this is what ships)"
  if run "build Release w/ CI=true (0 warnings)" env CI=true dotnet build "$SOLUTION" -c Release --no-restore -warnaserror; then
    run "test Release w/ CI=true" test_pass Release CI=true || true
  else
    skipped "test Release w/ CI=true"
  fi

  # A second, cheap CI-parity data point on the fast (Debug) configuration: this catches an
  # env-conditional bug that has nothing to do with optimisation settings, without paying
  # for a second Release build. Kept from the original three passes for exactly the
  # coverage it always provided.
  step "Build + test (CI parity, Debug w/ CI=true)"
  if run "build Debug w/ CI=true (0 warnings)" env CI=true dotnet build "$SOLUTION" -c Debug --no-restore -warnaserror; then
    run "test Debug w/ CI=true" test_pass Debug CI=true || true
  else
    skipped "test Debug w/ CI=true"
  fi
fi

# The analyzer's unit tests drive it as a library, which proves the rules and proves nothing
# about packaging. An analyzer that lands outside analyzers/dotnet/cs, or is built against a
# Roslyn the consumer cannot load, passes all of them and then does nothing in a real build.
# This packs it and builds a real net8.0 consumer against the .nupkg, then builds this
# repository's own packaged projects, from a copy, with that analyzer attached. `full` only:
# it packs and restores, which the inner loop should not pay for on every run.
if [[ "$MODE" == "full" ]]; then
  step "Analyzer reaches a consumer, and the kernel passes it"
  run "analyzer-probe" tools/analyzer-probe/check.sh || true
fi

# The same argument one layer up. Everything above this point builds the packages from
# source and tests them through project references; an engine restores a .nupkg and binds
# against whatever is inside it. 0.1.0 shipped lib/net10.0 only and locked a net8.0 engine
# out of the kernel entirely with every test passing (docs/decisions/0008), and it was
# found by building and RUNNING a real consumer. This packs the three published packages
# under a version that cannot shadow a release, restores them into an isolated package
# root, and executes a consumer on both committed frameworks. `full` only, for the same
# reason as the analyzer probe: it packs and restores, which the inner loop should not pay
# for on every run. It costs about nine seconds, which is the cheaper half of that
# decision -- if it ever stops being, the alternative is a scheduled workflow, at the price
# of learning about a broken package after the merge rather than before it.
if [[ "$MODE" == "full" ]]; then
  step "Packages are usable by a consumer that runs"
  run "package-probe" tools/package-probe/check.sh || true
fi

step "Repository invariants"
run "repo-checks" tools/repo-checks.py || true

# The checks are code, and until now nothing tested them. Two of them shipped with holes
# that a single negative fixture would have caught. This step is what makes the enforcement
# layer subject to the same standard it imposes on everything else.
#
# `unittest` exits 0 when tests SKIP, so a skipped suite is indistinguishable from a passing
# one by exit code alone -- and these tests are the only evidence the guards bite. A skip
# here is a failure, not a note.
#
# `|| tooling_status=$?` is load-bearing: under `set -e` a bare `var=$(failing-command)`
# ends the shell immediately, which would skip the FAIL line, the output, and the verdict
# banner -- in a gate whose whole job is producing evidence someone can paste into a PR.
step "Tooling tests"
tooling_status=0
tooling_output="$(python3 -m unittest discover -s tools/tests 2>&1)" || tooling_status=$?
if [[ "$tooling_status" -ne 0 ]]; then
  printf '%s\n' "$tooling_output" | tail -20
  fail "python tooling tests"
elif printf '%s' "$tooling_output" | grep -q "skipped="; then
  printf '%s\n' "$tooling_output" | tail -3
  fail "python tooling tests skipped some cases -- the gate must prove all of them"
else
  printf '%sok%s   %s\n' "$GREEN" "$OFF" "$(printf '%s' "$tooling_output" | grep -E '^Ran ' || echo 'python tooling tests')"
fi

# This step's name says what it actually checks, which is narrower than "Whitespace" implied.
# `git diff --check` only flags whitespace errors (trailing blanks, mixed tabs) in the
# working tree relative to the index/HEAD. After `actions/checkout` the tree is clean, so
# in CI this step diffs nothing, is unconditionally exit 0, and printing "ok" there proves
# nothing about the commit being validated -- it is only meaningful when a human or agent
# runs validate.sh against a dirty working tree before committing. It stays in `full`
# because that is still a real and useful catch locally; it is named and commented here so
# nobody mistakes a green CI run of this step for a whitespace audit of the change itself.
step "Whitespace (uncommitted working-tree changes only -- a no-op in CI after checkout)"
run "git diff --check" git diff --check || true

echo
if [[ "$FAILED" -eq 0 ]]; then
  printf '%s%svalidate.sh %s: PASS%s\n' "$BOLD" "$GREEN" "$MODE" "$OFF"
else
  printf '%s%svalidate.sh %s: FAIL%s\n' "$BOLD" "$RED" "$MODE" "$OFF"
fi
exit "$FAILED"
