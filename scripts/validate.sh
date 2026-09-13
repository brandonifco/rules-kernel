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
  run "test Debug" dotnet test "$SOLUTION" -c Debug --no-build --nologo || true
else
  skipped "test Debug"
fi

if [[ "$MODE" != "fast" ]]; then
  # Release is not ceremonial: different optimisation settings are historically where
  # "works on my machine" determinism bugs surface.
  step "Build + test (Release)"
  if run "build Release (0 warnings)" dotnet build "$SOLUTION" -c Release --no-restore -warnaserror; then
    run "test Release" dotnet test "$SOLUTION" -c Release --no-build --nologo || true
  else
    skipped "test Release"
  fi

  # CI sets CI=true, which flips ContinuousIntegrationBuild on via Directory.Build.props.
  # That rewrites compile-time source paths, changing anything derived from them. Neither
  # pass above exercises it, so a test sensitive to it can pass locally and fail on every
  # CI run. Reproduce CI's own env var, not an equivalent property.
  step "Build + test (CI parity, CI=true)"
  if run "build Debug w/ CI=true" env CI=true dotnet build "$SOLUTION" -c Debug --no-restore -warnaserror; then
    run "test Debug w/ CI=true" env CI=true dotnet test "$SOLUTION" -c Debug --no-build --nologo || true
  else
    skipped "test Debug w/ CI=true"
  fi
fi

step "Repository invariants"
run "repo-checks" tools/repo-checks.py || true

step "Whitespace"
run "git diff --check" git diff --check || true

echo
if [[ "$FAILED" -eq 0 ]]; then
  printf '%s%svalidate.sh %s: PASS%s\n' "$BOLD" "$GREEN" "$MODE" "$OFF"
else
  printf '%s%svalidate.sh %s: FAIL%s\n' "$BOLD" "$RED" "$MODE" "$OFF"
fi
exit "$FAILED"
