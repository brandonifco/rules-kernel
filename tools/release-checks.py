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

What the check proves
---------------------
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


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        prog="release-checks.py", description=__doc__.splitlines()[0])
    parser.add_argument("--root", default=str(ROOT))
    args = parser.parse_args(argv)

    root = Path(args.root).resolve()
    failures, examined = check_unshipped_api_is_empty(root)

    if failures:
        print(f"FAIL  unshipped-api-is-empty  ({len(failures)})")
        for problem in failures:
            print(f"        {problem}")
        print()
        print("release-checks: FAIL")
        return 1

    if examined == 0:
        print("skip  unshipped-api-is-empty  (no "
              f"{BASELINE_FILENAME} found -- this check proved nothing)")
        print()
        print("release-checks: FAIL (a check that proved nothing cannot report PASS)")
        return 1

    print(f"ok    unshipped-api-is-empty  ({examined} examined)")
    print()
    print("release-checks: PASS")
    return 0


if __name__ == "__main__":
    sys.exit(main())
