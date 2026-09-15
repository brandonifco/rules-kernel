#!/usr/bin/env python3
"""Tests for tools/release-checks.py.

    python3 -m unittest discover -s tools/tests

The same standard the repo-checks tests hold themselves to applies here, and applies harder:
this gate runs on exactly one occasion -- a pushed tag -- so nobody exercises it in the
inner loop, and a broken version of it would be discovered by the release it failed to
stop. A guard without a negative fixture is not a guard.

Every test of the check itself builds its own temporary tree. None of them asserts that this
repository currently passes: such a test goes green on the day the check stops biting, and
can never show the check FAILS when it should. The one group that does read the real
repository -- TheGateIsNotInTheDevelopmentPathTests -- asserts wiring rather than verdicts,
because where this file is invoked from is the design constraint, and a constraint stated
only in a comment is not one.
"""
from __future__ import annotations

import contextlib
import importlib.util
import io
import json
import os
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

TOOLS = Path(__file__).resolve().parents[1]


def _load_release_checks():
    """Import release-checks.py, whose filename is not a Python identifier."""
    spec = importlib.util.spec_from_file_location("release_checks", TOOLS / "release-checks.py")
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    sys.modules["release_checks"] = module
    spec.loader.exec_module(module)
    return module


rel = _load_release_checks()

# The three baselines that v0.2.0 published while still classifying them as unshipped, plus
# the analyzer, which is packed by the same `dotnet pack RulesKernel.slnx` and therefore
# owes the same promotion at whatever tag first publishes it.
PACKAGED = (
    "src/RulesKernel",
    "src/RulesKernel.Randomness",
    "src/RulesKernel.Analyzers",
    "tests/RulesKernel.Testing",
)

A_DECLARATION = "RulesKernel.Identity.RulesetVersion.IsValid.get -> bool"


CONSUMERS = {
    "consumers": [
        {"name": "engine-a", "repository": "https://example.invalid/a", "commit": "a" * 40,
         "modes": ["kernel", "analyzer"], "why": "fixture"},
        {"name": "engine-b", "repository": "https://example.invalid/b", "commit": "b" * 40,
         "modes": ["kernel", "analyzer"], "why": "fixture"},
        {"name": "engine-c", "repository": "https://example.invalid/c", "commit": "c" * 40,
         "modes": ["analyzer"], "why": "fixture"},
    ]
}

GIT_ENV = {
    **os.environ,
    "GIT_AUTHOR_NAME": "fixture", "GIT_AUTHOR_EMAIL": "fixture@example.invalid",
    "GIT_COMMITTER_NAME": "fixture", "GIT_COMMITTER_EMAIL": "fixture@example.invalid",
}


class ReleaseTree:
    """A git tree shaped like this repository at a tag: every baseline promoted, an earlier
    release tagged v0.3.0, and nothing calibrated changed since it."""

    def __init__(self) -> None:
        self.root = Path(tempfile.mkdtemp(prefix="release-checks-test-"))
        for project in PACKAGED:
            self.write(f"{project}/PublicAPI.Shipped.txt", "#nullable enable\n")
            self.write(f"{project}/PublicAPI.Unshipped.txt", "")
        self.write("Directory.Build.props",
                   "<Project>\n  <PropertyGroup>\n    <VersionPrefix>0.4.0</VersionPrefix>\n"
                   "    <VersionSuffix></VersionSuffix>\n  </PropertyGroup>\n</Project>\n")
        self.write("tools/calibration/consumers.json", json.dumps(CONSUMERS, indent=2) + "\n")
        self.write("src/RulesKernel/Identity/Thing.cs", "namespace RulesKernel.Identity;\n")
        self.write("src/RulesKernel.Analyzers/Rule.cs", "namespace RulesKernel.Analyzers;\n")
        self.git("init", "-q")
        self.commit("the 0.3.0 release")
        self.git("tag", "v0.3.0")

    def git(self, *args: str) -> str:
        return subprocess.run(["git", *args], cwd=self.root, env=GIT_ENV, check=True,
                              capture_output=True, text=True).stdout.strip()

    def commit(self, message: str) -> str:
        self.git("add", "-A")
        self.git("commit", "-q", "--allow-empty", "-m", message)
        return self.git("rev-parse", "HEAD")

    def write(self, relative: str, content: str) -> Path:
        path = self.root / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(content, encoding="utf-8")
        return path

    def cleanup(self) -> None:
        shutil.rmtree(self.root, ignore_errors=True)


class ReleaseCheckTestCase(unittest.TestCase):
    def setUp(self) -> None:
        self.tree = ReleaseTree()
        self.root = self.tree.root
        self.addCleanup(self.tree.cleanup)

    def failures(self) -> list[str]:
        return rel.check_unshipped_api_is_empty(self.root)[0]

    def examined(self) -> int:
        return rel.check_unshipped_api_is_empty(self.root)[1]

    def run_main(self, argv: list[str]) -> tuple[int, str]:
        buffer = io.StringIO()
        with contextlib.redirect_stdout(buffer):
            code = rel.main(argv)
        return code, buffer.getvalue()


class APromotedTreeIsCleanTests(ReleaseCheckTestCase):
    """Without this, a check that failed everything would pass every negative test below."""

    def test_a_fully_promoted_tree_passes_and_examined_something(self) -> None:
        self.assertEqual([], self.failures())
        self.assertEqual(len(PACKAGED), self.examined())

    def test_it_exits_zero(self) -> None:
        code, output = self.run_main(["--root", str(self.root)])
        self.assertEqual(0, code, output)
        self.assertIn("release-checks: PASS", output)


class AnUnpromotedBaselineFailsTests(ReleaseCheckTestCase):
    """The state v0.2.0 was actually tagged and published in."""

    def test_one_pending_declaration_fails(self) -> None:
        self.tree.write("src/RulesKernel/PublicAPI.Unshipped.txt", A_DECLARATION + "\n")
        found = self.failures()
        self.assertEqual(1, len(found), found)
        self.assertIn("PublicAPI.Unshipped.txt", found[0])
        self.assertIn("1 declaration(s) not promoted", found[0])

    def test_it_exits_one(self) -> None:
        self.tree.write("src/RulesKernel/PublicAPI.Unshipped.txt", A_DECLARATION + "\n")
        code, output = self.run_main(["--root", str(self.root)])
        self.assertEqual(1, code)
        self.assertIn("release-checks: FAIL", output)

    def test_every_unpromoted_project_is_named_not_just_the_first(self) -> None:
        # A gate that stops at the first problem makes a release a sequence of one-line
        # fixes and re-tags. Each tag is a permanent artifact; say everything at once.
        for project in PACKAGED:
            self.tree.write(f"{project}/PublicAPI.Unshipped.txt", A_DECLARATION + "\n")
        found = self.failures()
        self.assertEqual(len(PACKAGED), len(found), found)

    def test_the_v0_2_0_state_of_the_whole_repository_fails(self) -> None:
        # The exact shape of the defect: Shipped holds only the directive, the surface sits
        # in Unshipped, and the tag goes out anyway.
        for project in ("src/RulesKernel", "src/RulesKernel.Randomness", "tests/RulesKernel.Testing"):
            self.tree.write(f"{project}/PublicAPI.Shipped.txt", "#nullable enable\n")
            self.tree.write(f"{project}/PublicAPI.Unshipped.txt", A_DECLARATION + "\n")
        code, _ = self.run_main(["--root", str(self.root)])
        self.assertEqual(1, code)

    def test_a_removal_counts_as_unpromoted(self) -> None:
        # Deleting a public member is a compatibility event with the same standing as
        # adding one; PublicApiAnalyzers records it as a *REMOVED: line in Unshipped, and
        # a release must promote it rather than ship a removal nothing recorded.
        self.tree.write("src/RulesKernel/PublicAPI.Unshipped.txt",
                        f"*REMOVED*{A_DECLARATION}\n")
        self.assertEqual(1, len(self.failures()))


class WhatDoesNotCountAsPendingTests(ReleaseCheckTestCase):
    def test_the_nullable_directive_alone_is_an_empty_baseline(self) -> None:
        # Some projects carry `#nullable enable` in Unshipped as well. It is a directive to
        # the analyzer, not a member, and a tree holding only that has promoted everything.
        self.tree.write("src/RulesKernel/PublicAPI.Unshipped.txt", "#nullable enable\n")
        self.assertEqual([], self.failures())

    def test_blank_lines_are_not_a_pending_declaration(self) -> None:
        self.tree.write("src/RulesKernel/PublicAPI.Unshipped.txt", "\n\n")
        self.assertEqual([], self.failures())

    def test_a_baseline_under_obj_is_build_output_not_a_declaration(self) -> None:
        # `dotnet pack` and restore leave copies under obj/. Failing a release on one would
        # be a gate that cannot be satisfied by editing the repository.
        self.tree.write("src/RulesKernel/obj/Debug/PublicAPI.Unshipped.txt",
                        A_DECLARATION + "\n")
        self.assertEqual([], self.failures())
        self.assertEqual(len(PACKAGED), self.examined())


    def test_a_baseline_in_a_nested_worktree_belongs_to_another_branch(self) -> None:
        # Agent worktrees live under .claude/worktrees/. A worktree is a separate checkout,
        # usually mid-change on another branch, so its unpromoted baselines say nothing
        # about the tree being released -- and reporting them made a correctly promoted
        # tree look like four failing ones.
        self.tree.write(".claude/worktrees/agent-x/src/RulesKernel/PublicAPI.Unshipped.txt",
                        A_DECLARATION + "\n")
        self.assertEqual([], self.failures())
        self.assertEqual(len(PACKAGED), self.examined())


class CalibrationRecordedTests(ReleaseCheckTestCase):
    """docs/decisions/0021: a release that changes the kernel or the analyzer names the real
    consumers it was calibrated against, at the tree being released."""

    def check(self) -> list[str]:
        return rel.check_calibration_recorded(self.root)[0]

    def change(self, path: str) -> str:
        self.tree.write(path, self.tree.root.joinpath(path).read_text(encoding="utf-8") + "// changed\n")
        return self.tree.commit(f"change {path}")

    def record(self, body: str) -> None:
        self.tree.write("docs/calibration/0.4.0.md", "# Calibration for 0.4.0\n\n" + body)
        self.tree.commit("record calibration")

    @staticmethod
    def kernel_section(commit: str, rows: str) -> str:
        return ("## Kernel\n\nKernel commit: " + commit + "\n\n"
                "| consumer | commit | pins | tests | result |\n|---|---|---|---|---|\n" + rows + "\n")

    @staticmethod
    def analyzer_section(commit: str, rows: str) -> str:
        return ("## Analyzer\n\nKernel commit: " + commit + "\n\n"
                "| consumer | commit | findings by rule | total |\n|---|---|---|---|\n" + rows + "\n")

    GOOD_KERNEL_ROWS = "| engine-a | aaaaaaa | 1 | 10 | pass |\n| engine-b | bbbbbbb | 2 | 20 | pass |"

    def test_nothing_calibrated_changed_so_no_record_is_needed(self) -> None:
        self.tree.write("README.md", "prose\n")
        self.tree.commit("docs only")
        self.assertEqual([], self.check())

    def test_a_kernel_change_without_a_record_fails(self) -> None:
        self.change("src/RulesKernel/Identity/Thing.cs")
        self.assertTrue(any("docs/calibration/0.4.0.md is missing" in f for f in self.check()), self.check())

    def test_an_analyzer_change_without_a_record_fails(self) -> None:
        self.change("src/RulesKernel.Analyzers/Rule.cs")
        self.assertTrue(any("is missing" in f for f in self.check()))

    def test_a_kernel_change_calibrated_against_two_consumers_passes(self) -> None:
        calibrated = self.change("src/RulesKernel/Identity/Thing.cs")
        self.record(self.kernel_section(calibrated, self.GOOD_KERNEL_ROWS))
        self.assertEqual([], self.check())

    def test_one_consumer_is_not_enough_for_a_kernel_change(self) -> None:
        calibrated = self.change("src/RulesKernel/Identity/Thing.cs")
        self.record(self.kernel_section(calibrated, "| engine-a | aaaaaaa | 1 | 10 | pass |"))
        self.assertTrue(any("1 consumer(s) calibrated, 2 required" in f for f in self.check()), self.check())

    def test_one_consumer_is_enough_for_an_analyzer_change(self) -> None:
        calibrated = self.change("src/RulesKernel.Analyzers/Rule.cs")
        self.record(self.analyzer_section(calibrated, "| engine-c | ccccccc | RK0001 3 | 3 |"))
        self.assertEqual([], self.check())

    def test_a_failing_consumer_does_not_count(self) -> None:
        calibrated = self.change("src/RulesKernel/Identity/Thing.cs")
        self.record(self.kernel_section(
            calibrated, "| engine-a | aaaaaaa | 1 | 10 | pass |\n| engine-b | bbbbbbb | 2 | - | FAIL |"))
        self.assertTrue(any("engine-b did not pass" in f for f in self.check()))

    def test_a_consumer_at_a_commit_the_manifest_does_not_pin_fails(self) -> None:
        calibrated = self.change("src/RulesKernel/Identity/Thing.cs")
        self.record(self.kernel_section(
            calibrated, "| engine-a | 1234567 | 1 | 10 | pass |\n| engine-b | bbbbbbb | 2 | 20 | pass |"))
        self.assertTrue(any("engine-a was run at '1234567'" in f for f in self.check()))

    def test_a_consumer_not_listed_for_the_mode_fails(self) -> None:
        calibrated = self.change("src/RulesKernel/Identity/Thing.cs")
        self.record(self.kernel_section(
            calibrated, self.GOOD_KERNEL_ROWS + "\n| engine-c | ccccccc | 1 | 5 | pass |"))
        self.assertTrue(any("engine-c is not a kernel consumer" in f for f in self.check()))

    def test_a_calibration_of_an_earlier_tree_fails(self) -> None:
        calibrated = self.change("src/RulesKernel/Identity/Thing.cs")
        self.change("src/RulesKernel/Identity/Thing.cs")
        self.record(self.kernel_section(calibrated, self.GOOD_KERNEL_ROWS))
        self.assertTrue(any("changed after it" in f for f in self.check()), self.check())

    def test_a_kernel_commit_outside_the_release_history_fails(self) -> None:
        self.change("src/RulesKernel/Identity/Thing.cs")
        self.record(self.kernel_section("d" * 40, self.GOOD_KERNEL_ROWS))
        self.assertTrue(any("is not in this release's history" in f for f in self.check()))

    def test_a_record_for_the_kernel_does_not_cover_an_analyzer_change(self) -> None:
        self.change("src/RulesKernel.Analyzers/Rule.cs")
        calibrated = self.change("src/RulesKernel/Identity/Thing.cs")
        self.record(self.kernel_section(calibrated, self.GOOD_KERNEL_ROWS))
        self.assertTrue(any("no '## Analyzer' section" in f for f in self.check()))

    def test_no_earlier_release_tag_is_a_failure_not_a_pass(self) -> None:
        self.tree.git("tag", "-d", "v0.3.0")
        self.assertTrue(any("no earlier release tag" in f for f in self.check()))

    def test_the_tag_being_released_is_not_its_own_baseline(self) -> None:
        self.change("src/RulesKernel/Identity/Thing.cs")
        self.tree.git("tag", "v0.4.0")
        self.assertTrue(any("is missing" in f for f in self.check()))


class ACheckThatProvedNothingFailsTests(unittest.TestCase):
    """Principle 3: a check whose inputs vanished must not report ok.

    Every packaged project here carries a baseline pair, so finding none means the glob or
    the filename changed -- and a release gate reporting PASS on the strength of having
    looked at nothing is worse than no gate, because the tag is trusted afterwards.
    """

    def setUp(self) -> None:
        self.empty = Path(tempfile.mkdtemp(prefix="release-checks-empty-"))
        self.addCleanup(shutil.rmtree, self.empty, True)

    def test_no_baselines_found_is_a_failure_not_a_pass(self) -> None:
        buffer = io.StringIO()
        with contextlib.redirect_stdout(buffer):
            code = rel.main(["--root", str(self.empty)])
        self.assertEqual(1, code)
        self.assertIn("proved nothing", buffer.getvalue())


class TheGateIsNotInTheDevelopmentPathTests(unittest.TestCase):
    """The design constraint, asserted rather than left to a comment.

    A non-empty Unshipped baseline is the normal state of a branch in flight. If this file
    ever gets wired into scripts/validate.sh or into repo-checks.py's registry, ordinary
    development starts failing the gate, and the only way back to green is promoting API
    that has not shipped -- the exact defect the check exists to prevent.
    """

    REPO = TOOLS.parent

    def test_validate_sh_does_not_call_the_release_checks(self) -> None:
        script = (self.REPO / "scripts" / "validate.sh").read_text(encoding="utf-8")
        self.assertNotIn("release-checks", script)

    def test_the_release_path_does_call_them(self) -> None:
        # The complement: a gate nothing invokes is prose with a shebang.
        workflow = (self.REPO / ".github" / "workflows" / "publish.yml").read_text(
            encoding="utf-8")
        self.assertIn("tools/release-checks.py", workflow)

    def test_the_ordinary_ci_workflow_does_not_call_them_either(self) -> None:
        workflow = (self.REPO / ".github" / "workflows" / "build-and-test.yml").read_text(
            encoding="utf-8")
        self.assertNotIn("release-checks", workflow)


if __name__ == "__main__":
    unittest.main()
