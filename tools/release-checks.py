#!/usr/bin/env python3
"""release-checks -- the gates that only make sense at a tag.

    tools/release-checks.py             run every release check
    tools/release-checks.py --root DIR  run them against another tree (its own tests do)

Why this is a separate file from tools/repo-checks.py
-----------------------------------------------------
repo-checks.py holds invariants that must be true of every commit, and `scripts/validate.sh`
runs it on every change. The check in this file is not of that kind, and putting it there
would break ordinary development on purpose.

`PublicAPI.Unshipped.txt` is *supposed* to be non-empty while work is in flight -- that is
its entire function. A public member added on a branch belongs in Unshipped until the
release that publishes it; that is how `Microsoft.CodeAnalysis.PublicApiAnalyzers`
distinguishes API a consumer can already compile against from API still open for revision.
A repo-check asserting "Unshipped is empty" would fail the gate for every branch that adds
a public member, and the only way to get a green build would be to promote API that has not
shipped -- which is exactly the state this check exists to prevent.

So the obligation is real but it attaches to the *tag*, not to the commit. It runs from
`.github/workflows/publish.yml`, in the `validate` job, which completes before the `publish`
job packs anything. Nothing in `scripts/validate.sh` calls this file, and nothing should.

Two checks
----------
`unshipped-api-is-empty`, described below, and `calibration-recorded`, described beside its
code: a release that changes the kernel or the analyzer carries a calibration record naming
real consumers it was run against (docs/decisions/0021). Both attach to the tag for the same
reason -- an in-flight branch has not been calibrated yet, and should not have to be.

What the first check proves
---------------------------
At a release, every `PublicAPI.Unshipped.txt` in the repository is empty -- meaning the
release PR promoted the surface it is about to publish into `PublicAPI.Shipped.txt`.

This is stronger than "no already-released declaration is listed as unshipped", and it
needs no knowledge of what nuget.org already holds: if the file is empty at the tag, then
every declaration in the package is in Shipped, and any subsequent reshaping of it fails
the analyzer in a consumer-visible way rather than being edited quietly.

It exists because the state it forbids was reached and shipped. `v0.2.0` was tagged and
published with all 158 declarations of `RulesKernel`, `RulesKernel.Randomness` and
`RulesKernel.Testing` still in Unshipped, so for the whole life of that release the
analyzer could not tell published API from draft API, and the compatibility obligation the
baseline is meant to carry was recorded nowhere.

A check that examined nothing reports a failure, not success -- the same rule repo-checks.py
states at length. Every packaged project in this repository has a baseline pair, so finding
no `PublicAPI.Unshipped.txt` at all means the glob lost its inputs, not that the repository
is clean.
"""
from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

BASELINE_FILENAME = "PublicAPI.Unshipped.txt"

# Build output and tool caches. A packed or restored tree can contain copies of a baseline
# file under obj/, and those are not the repository's declaration of anything.
#
# `.claude` is here for a different reason: agent worktrees live under .claude/worktrees/,
# and a worktree is a SEPARATE CHECKOUT of this repository, usually on another branch. Its
# baselines are that branch's declaration, not this tree's. Without this the gate reports
# every in-flight branch on the machine -- which it did, naming three sibling worktrees on a
# tree that was itself correctly promoted. It fails safe (a stray checkout can only add
# findings, never hide one) and CI never sees it, since actions/checkout produces a clean
# tree. It is still wrong: a release gate must answer for the tree being released and
# nothing else.
IGNORED_PARTS = {
    ".git", ".claude", "bin", "obj", ".dotnet", ".venv", "__pycache__", "artifacts",
    "TestResults", "node_modules", ".vs", "packages",
}


def declarations(text: str) -> list[str]:
    """The lines of a baseline file that actually assert something about the surface.

    Blank lines are noise, and a `#`-prefixed line is a directive to the analyzer
    (`#nullable enable`) rather than a member. Everything else counts, including a
    `*REMOVED*` line: removing a public member at a release is a compatibility event with
    the same standing as adding one, and it is promoted the same way.
    """
    return [
        line for line in (raw.strip() for raw in text.splitlines())
        if line and not line.startswith("#")
    ]


def baseline_files(root: Path) -> list[Path]:
    return sorted(
        path for path in root.rglob(BASELINE_FILENAME)
        if not any(part in IGNORED_PARTS for part in path.relative_to(root).parts)
    )


def check_unshipped_api_is_empty(root: Path) -> tuple[list[str], int]:
    """Returns (failures, files examined)."""
    failures = []
    files = baseline_files(root)
    for path in files:
        pending = declarations(path.read_text(encoding="utf-8"))
        if pending:
            rel = path.relative_to(root)
            shown = ", ".join(pending[:3]) + (", ..." if len(pending) > 3 else "")
            failures.append(
                f"{rel}: {len(pending)} declaration(s) not promoted ({shown}). A release "
                "publishes this surface, so it is shipped API by definition: move these "
                f"lines into {rel.with_name('PublicAPI.Shipped.txt')} in the release PR. "
                "If the member should not ship yet, it should not be public in the tagged "
                "tree."
            )
    return failures, len(files)


# ------------------------------------------------------------------ calibration-recorded
#
# docs/decisions/0021. A release that changes what the analyzer reports is calibrated
# against at least one real consumer; a release that changes a kernel type, against at least
# two. The evidence is docs/calibration/<version>.md, written from tools/calibration/run.sh
# output, and this check holds it to three things a reviewer cannot reliably check by eye:
#
#   * it exists whenever the paths that need it changed since the previous release tag;
#   * the kernel commit it was run at is in this tag's history, and nothing under those
#     paths changed between that commit and the tag -- a calibration of an earlier tree is
#     not a calibration of this one;
#   * it names enough consumers from tools/calibration/consumers.json, each at the commit
#     the manifest pins, and a kernel run passed on every one it names.
#
# What it cannot check: that the numbers in the record are the numbers run.sh printed. The
# record is reviewed in the release PR, and publish.yml reruns the kernel calibration at
# the tag.

CALIBRATION_MANIFEST = "tools/calibration/consumers.json"
CALIBRATED_PATHS = {
    "analyzer": ("src/RulesKernel.Analyzers/",),
    "kernel": ("src/RulesKernel/",),
}
MINIMUM_CONSUMERS = {"analyzer": 1, "kernel": 2}
SECTION_TITLES = {"analyzer": "Analyzer", "kernel": "Kernel"}
RELEASE_TAG = re.compile(r"^v(\d+)\.(\d+)\.(\d+)$")


def _git(root: Path, *args: str) -> subprocess.CompletedProcess[str]:
    return subprocess.run(["git", *args], cwd=root, capture_output=True, text=True, check=False)


def release_version(root: Path) -> str | None:
    props = root / "Directory.Build.props"
    if not props.is_file():
        return None
    text = props.read_text(encoding="utf-8")
    prefix = re.search(r"<VersionPrefix>\s*([^<\s]+)\s*</VersionPrefix>", text)
    return prefix.group(1) if prefix else None


def previous_release_tag(root: Path, version: str) -> str | None:
    listed = _git(root, "tag", "--merged", "HEAD", "--list", "v*")
    tags = []
    for tag in listed.stdout.split():
        match = RELEASE_TAG.match(tag)
        if match and tag != f"v{version}":
            tags.append((tuple(int(part) for part in match.groups()), tag))
    return max(tags)[1] if tags else None


def _calibrated_changes(root: Path, base: str, mode: str) -> list[str]:
    diff = _git(root, "diff", "--name-only", base, "HEAD")
    return [
        name for name in diff.stdout.splitlines()
        if name.startswith(CALIBRATED_PATHS[mode])
        and (name.endswith(".cs") or "AnalyzerReleases" in name)
    ]


def _section(text: str, title: str) -> str | None:
    match = re.search(rf"^## {re.escape(title)}\s*$(.*?)(?=^## |\Z)", text, re.M | re.S)
    return match.group(1) if match else None


def check_calibration_recorded(root: Path) -> tuple[list[str], int]:
    """Returns (failures, releases examined)."""
    version = release_version(root)
    if version is None:
        return ["Directory.Build.props declares no VersionPrefix"], 0
    if _git(root, "rev-parse", "HEAD").returncode != 0:
        return [], 0

    previous = previous_release_tag(root, version)
    if previous is None:
        return [f"no earlier release tag is reachable from HEAD, so what changed since the "
                f"last release cannot be computed; fetch tags (fetch-depth: 0)"], 0

    required = [mode for mode in ("kernel", "analyzer") if _calibrated_changes(root, previous, mode)]
    if not required:
        return [], 1

    record_path = root / "docs" / "calibration" / f"{version}.md"
    if not record_path.is_file():
        return [f"{record_path.relative_to(root)} is missing. Since {previous} this release "
                f"changes {' and '.join(CALIBRATED_PATHS[m][0] for m in required)}, which "
                "must be calibrated against real consumers before it ships "
                "(docs/decisions/0021); run tools/calibration/run.sh and record it."], 1

    manifest = json.loads((root / CALIBRATION_MANIFEST).read_text(encoding="utf-8"))
    pinned = {c["name"]: (c["commit"], set(c["modes"])) for c in manifest["consumers"]}
    record = record_path.read_text(encoding="utf-8")
    rel_record = record_path.relative_to(root)
    failures: list[str] = []

    for mode in required:
        title = SECTION_TITLES[mode]
        section = _section(record, title)
        if section is None:
            failures.append(f"{rel_record}: no '## {title}' section, and this release changes "
                            f"{CALIBRATED_PATHS[mode][0]} since {previous}")
            continue

        commit = re.search(r"^Kernel commit:\s*([0-9a-f]{40})\s*$", section, re.M)
        if not commit:
            failures.append(f"{rel_record} ({title}): no 'Kernel commit: <40 hex>' line")
            continue
        recorded = commit.group(1)
        if _git(root, "merge-base", "--is-ancestor", recorded, "HEAD").returncode != 0:
            failures.append(f"{rel_record} ({title}): kernel commit {recorded[:7]} is not in "
                            "this release's history")
            continue
        since = _calibrated_changes(root, recorded, mode)
        if since:
            failures.append(f"{rel_record} ({title}): calibrated at {recorded[:7]}, but "
                            f"{', '.join(since[:3])} changed after it; calibrate the tree "
                            "being released")
            continue

        consumers = set()
        for row in re.findall(r"^\|(.+)\|\s*$", section, re.M):
            cells = [cell.strip() for cell in row.split("|")]
            if len(cells) < 3 or cells[0] not in pinned:
                continue
            name, short = cells[0], cells[1]
            manifest_commit, modes = pinned[name]
            if mode not in modes:
                failures.append(f"{rel_record} ({title}): {name} is not a {mode} consumer "
                                f"in {CALIBRATION_MANIFEST}")
            elif len(short) < 7 or not manifest_commit.startswith(short):
                failures.append(f"{rel_record} ({title}): {name} was run at '{short}', but "
                                f"{CALIBRATION_MANIFEST} pins {manifest_commit[:7]}")
            elif mode == "kernel" and cells[-1] != "pass":
                failures.append(f"{rel_record} ({title}): {name} did not pass ('{cells[-1]}')")
            elif mode == "analyzer" and not cells[-1].isdigit():
                failures.append(f"{rel_record} ({title}): {name} has no finding count")
            else:
                consumers.add(name)

        if len(consumers) < MINIMUM_CONSUMERS[mode]:
            failures.append(f"{rel_record} ({title}): {len(consumers)} consumer(s) calibrated, "
                            f"{MINIMUM_CONSUMERS[mode]} required (docs/decisions/0021)")

    return failures, 1


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        prog="release-checks.py", description=__doc__.splitlines()[0])
    parser.add_argument("--root", default=str(ROOT))
    args = parser.parse_args(argv)

    root = Path(args.root).resolve()
    checks = {
        "unshipped-api-is-empty": check_unshipped_api_is_empty,
        "calibration-recorded": check_calibration_recorded,
    }
    failed = False
    for name, check in checks.items():
        failures, examined = check(root)
        if failures:
            failed = True
            print(f"FAIL  {name}  ({len(failures)})")
            for problem in failures:
                print(f"        {problem}")
        elif examined == 0:
            failed = True
            print(f"skip  {name}  (nothing examined -- this check proved nothing)")
        else:
            print(f"ok    {name}  ({examined} examined)")

    print()
    if failed:
        print("release-checks: FAIL")
        return 1
    print("release-checks: PASS")
    return 0


if __name__ == "__main__":
    sys.exit(main())
