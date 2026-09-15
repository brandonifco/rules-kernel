#!/usr/bin/env python3
"""Tests for tools/repo-checks.py.

    python3 -m unittest discover -s tools/tests

repo-checks.py is the file that decides whether this repository's invariants hold, and it
had no tests. That is the worst place in a repository for an untested file: every other
check's credibility is downstream of it, and a check that quietly stops biting reports
`ok` exactly like a check that bites and finds nothing.

Every test here is built in a temporary directory. None of them reads the real repository,
because a test that asserts "the real repo passes" stops being a test of the checker the
moment the repo changes, and it can never demonstrate that a check FAILS when it should.

The shape of each check's coverage is deliberate and should be preserved:

  * a positive case -- a fixture that satisfies the rule and must produce no failure, which
    is what proves a check is not simply failing everything; and
  * a negative fixture -- a fixture that breaks the rule and MUST produce a failure.

Most negative fixtures here are transcriptions of real bypasses: source that passed the
previous version of repo-checks.py while violating the invariant the check is named after.
Each is marked. Those tests fail against the old file and pass against this one, which is
the only evidence that a fix to a checker is a fix.
"""
from __future__ import annotations

import contextlib
import importlib.util
import io
import json
import re
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

TOOLS = Path(__file__).resolve().parents[1]


def _load_repo_checks():
    """Import repo-checks.py, whose filename is not a Python identifier.

    The module must be registered in sys.modules before exec_module, or @dataclass raises
    while resolving the annotations of a class whose module it cannot find.
    """
    spec = importlib.util.spec_from_file_location("repo_checks", TOOLS / "repo-checks.py")
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    sys.modules["repo_checks"] = module
    spec.loader.exec_module(module)
    return module


rc = _load_repo_checks()


# --------------------------------------------------------------------------- fixtures


CSPROJ = """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>{name}</RootNamespace>
{extra_properties}  </PropertyGroup>
{items}</Project>
"""

WORKFLOW = """name: ci
on:
  push:
    branches: [main]
jobs:
  build:
    runs-on: ubuntu-24.04
    steps:
      - uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7.0.1
      - run: ./scripts/validate.sh full
"""


class Fixture:
    """A synthetic repository that satisfies every check, for tests to then break.

    It mirrors this repository's shape -- six projects, the same names, the same declared
    graph -- because the layering check is written against those names. It does not mirror
    its contents.
    """

    def __init__(self, root: Path | None = None) -> None:
        # `root` lets a test choose WHERE the checkout sits, which is itself a thing the
        # checks must be indifferent to.
        if root is None:
            self.root = Path(tempfile.mkdtemp(prefix="repo-checks-test-"))
        else:
            root.mkdir(parents=True, exist_ok=True)
            self.root = root
        self.write("README.md", "# Fixture\n\nSee [CLAUDE.md](CLAUDE.md).\n")
        self.write("CLAUDE.md", "# Fixture contract\n")
        self.write("global.json", '{\n  "sdk": {\n    "version": "10.0.112"\n  }\n}\n')
        self.write("Directory.Build.props", "<Project>\n  <PropertyGroup />\n</Project>\n")
        self.write(".github/workflows/build-and-test.yml", WORKFLOW)
        self.write("scripts/validate.sh", "#!/usr/bin/env bash\nexit 0\n")
        for number, slug in (
            ("0001", "kernel-scope-and-layering"),
            ("0002", "randomness-is-optional"),
            ("0003", "corpus-baselines"),
            ("0004", "unresolved-results"),
            ("0005", "pinned-pseudorandom-algorithm"),
        ):
            self.write(f"docs/decisions/{number}-{slug}.md", f"# {number}\n")
        self.write("docs/architecture.md", "# Architecture\n")

        self.project("src/RulesKernel", "RulesKernel", packable=True,
                     target_frameworks=["net8.0", "net10.0"])
        self.project("src/RulesKernel.Randomness", "RulesKernel.Randomness", packable=True,
                     target_frameworks=["net8.0", "net10.0"],
                     project_refs=["../RulesKernel/RulesKernel.csproj"])
        self.project("tests/RulesKernel.Testing", "RulesKernel.Testing", packable=True,
                     target_frameworks=["net8.0", "net10.0"],
                     project_refs=["../../src/RulesKernel.Randomness/RulesKernel.Randomness.csproj"])
        self.project("tests/RulesKernel.Tests", "RulesKernel.Tests", packable=False,
                     project_refs=["../../src/RulesKernel/RulesKernel.csproj"])
        self.project("tests/RulesKernel.Randomness.Tests", "RulesKernel.Randomness.Tests",
                     packable=False,
                     project_refs=["../../src/RulesKernel.Randomness/RulesKernel.Randomness.csproj",
                                   "../RulesKernel.Testing/RulesKernel.Testing.csproj"])
        # Referenced by nothing and referencing nothing: the analyzer is a build asset a
        # consumer opts into, not a layer of the stack. See docs/decisions/0011.
        self.project("src/RulesKernel.Analyzers", "RulesKernel.Analyzers", packable=True,
                     target_frameworks=["netstandard2.0"])
        self.project("tests/RulesKernel.Analyzers.Tests", "RulesKernel.Analyzers.Tests",
                     packable=False,
                     project_refs=["../../src/RulesKernel.Analyzers/RulesKernel.Analyzers.csproj"])
        self.project("probes/RegulatoryProbe.Tests", "RegulatoryProbe.Tests", packable=False,
                     project_refs=["../../src/RulesKernel/RulesKernel.csproj"])
        self.write_solution()

    # -- construction helpers

    def write(self, relative: str, content: str) -> Path:
        path = self.root / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(content, encoding="utf-8")
        return path

    def write_bytes(self, relative: str, content: bytes) -> Path:
        path = self.root / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(content)
        return path

    def project(self, directory: str, name: str, *, packable: bool,
                project_refs: list[str] | None = None,
                package_refs: list[str] | None = None,
                target_frameworks: list[str] | None = None,
                lock_frameworks: list[str] | None = None,
                write_lock: bool = True,
                raw_items: str = "") -> Path:
        extra = "" if packable else "    <IsPackable>false</IsPackable>\n"
        if packable:
            extra += f"    <PackageId>{name}</PackageId>\n"
        items = ""
        for ref in project_refs or []:
            items += f'  <ItemGroup>\n    <ProjectReference Include="{ref}" />\n  </ItemGroup>\n'
        for ref in package_refs or []:
            items += f'  <ItemGroup>\n    <PackageReference Include="{ref}" />\n  </ItemGroup>\n'
        items += raw_items
        if target_frameworks:
            # An emptied singular element alongside the plural one, exactly as the real
            # projects clear the Directory.Build.props default.
            extra += f"    <TargetFrameworks>{';'.join(target_frameworks)}</TargetFrameworks>\n"
            extra += "    <TargetFramework />\n"
        self.write(f"{directory}/{name}.csproj",
                   CSPROJ.format(name=name, extra_properties=extra, items=items))
        self.write(f"{directory}/AssemblyMarker.cs",
                   f"namespace {name};\n\npublic static class AssemblyMarker\n{{\n}}\n")
        # This helper also overwrites an existing project, so a lock file it does not write
        # must be removed rather than left over from the previous call.
        lock = self.root / directory / "packages.lock.json"
        if write_lock and (locked := lock_frameworks or target_frameworks):
            self.write(f"{directory}/packages.lock.json", json.dumps(
                {"version": 1, "dependencies": {fw: {} for fw in locked}}, indent=2) + "\n")
        elif lock.exists():
            lock.unlink()
        return self.root / directory

    def write_solution(self) -> None:
        entries = "\n".join(
            f'    <Project Path="{p.relative_to(self.root).as_posix()}" />'
            for p in rc.all_csproj(self.root)
        )
        self.write(rc.SOLUTION_FILE, f"<Solution>\n  <Folder Name=\"/all/\">\n{entries}\n  </Folder>\n</Solution>\n")

    def cleanup(self) -> None:
        shutil.rmtree(self.root, ignore_errors=True)


class CheckTestCase(unittest.TestCase):
    """Base class giving every test a fresh fixture and failure-shaped assertions."""

    def setUp(self) -> None:
        self.fixture = Fixture()
        self.root = self.fixture.root
        self.addCleanup(self.fixture.cleanup)

    def failures(self, check, root: Path | None = None) -> list[str]:
        return [str(f) for f in check(root or self.root).failures]

    def assertClean(self, check) -> None:
        found = self.failures(check)
        self.assertEqual([], found, f"expected no failures, got: {found}")

    def assertExamined(self, check) -> None:
        self.assertGreater(check(self.root).examined, 0, "the check examined nothing")

    def assertFailsWith(self, check, needle: str) -> None:
        found = self.failures(check)
        self.assertTrue(
            any(needle in f for f in found),
            f"expected a failure mentioning {needle!r}, got: {found}",
        )


# --------------------------------------------------- the baseline: everything passes


class FixtureIsCleanTests(CheckTestCase):
    """Every check passes on the untouched fixture.

    Without this, a check that failed everything would make every negative test below pass
    for the wrong reason.
    """

    def test_every_check_passes_and_examines_something(self) -> None:
        for name, check in rc.CHECKS.items():
            with self.subTest(check=name):
                self.assertEqual([], self.failures(check), name)
                self.assertGreater(check(self.root).examined, 0, f"{name} examined nothing")


# ------------------------------------------------- where the checkout itself happens to sit


class TheCheckoutLocationIsNotPartOfTheAnswerTests(unittest.TestCase):
    """A clone under a directory named like build output must not silently skip everything.

    Every IGNORED_PARTS comparison is against the path relative to the root being walked.
    Testing absolute parts made the location of the checkout part of the answer: a clone in
    ~/packages would have had every file skipped, and every check would have examined
    nothing. The same shape, with `worktrees`, really did zero scripts/validate.sh's
    test-project count when it ran from a git worktree -- see tools/expected-test-projects.py.
    """

    def setUp(self) -> None:
        self.tmp = Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.tmp, ignore_errors=True)

    def test_a_root_named_like_build_output_still_finds_its_projects(self) -> None:
        for ignored in sorted(rc.IGNORED_PARTS):
            with self.subTest(directory=ignored):
                fixture = Fixture(self.tmp / ignored / "repo")
                self.addCleanup(fixture.cleanup)

                self.assertEqual(
                    {"RulesKernel", "RulesKernel.Randomness", "RulesKernel.Testing",
                     "RulesKernel.Analyzers"},
                    set(rc.packaged_projects(fixture.root)),
                    f"a checkout under a directory named {ignored!r} found no projects")


# --------------------------------------------------------------- the framework commitment


class TargetFrameworkTests(CheckTestCase):
    """ADR 0008 made net8.0 a commitment. This is what keeps it one.

    `dotnet restore --locked-mode` already fails when a lock file and its project disagree,
    which is how a dependency bump that regenerates only the lock files gets caught -- it is
    what caught one. The case it cannot catch is a framework dropped from BOTH, where the
    two agree and the commitment is simply gone. That is the case this check exists for, and
    it is asserted directly below.
    """

    def test_the_committed_frameworks_pass_and_something_was_examined(self) -> None:
        self.assertClean(rc.check_target_frameworks)
        self.assertExamined(rc.check_target_frameworks)

    def test_dropping_a_framework_from_the_csproj_fails(self) -> None:
        self.fixture.project("src/RulesKernel.Randomness", "RulesKernel.Randomness",
                             packable=True, target_frameworks=["net10.0"],
                             lock_frameworks=["net8.0", "net10.0"])

        self.assertFailsWith(rc.check_target_frameworks, "targets ['net10.0']")

    def test_dropping_a_framework_from_the_lock_file_fails(self) -> None:
        self.fixture.project("src/RulesKernel.Randomness", "RulesKernel.Randomness",
                             packable=True, target_frameworks=["net8.0", "net10.0"],
                             lock_frameworks=["net10.0"])

        self.assertFailsWith(rc.check_target_frameworks, "locks ['net10.0']")

    def test_dropping_it_from_both_is_still_caught(self) -> None:
        # The whole reason this check exists. The csproj and the lock file agree, so
        # `dotnet restore --locked-mode` is perfectly happy and net8.0 is gone.
        self.fixture.project("src/RulesKernel.Randomness", "RulesKernel.Randomness",
                             packable=True, target_frameworks=["net10.0"])

        self.assertFailsWith(rc.check_target_frameworks, "superseding ADR 0008")

    def test_an_undeclared_packaged_project_fails(self) -> None:
        self.fixture.project("src/RulesKernel.Extra", "RulesKernel.Extra", packable=True,
                             target_frameworks=["net10.0"])

        self.assertFailsWith(rc.check_target_frameworks, "not declared in EXPECTED_TARGET_FRAMEWORKS")

    def test_a_missing_lock_file_fails(self) -> None:
        self.fixture.project("src/RulesKernel.Randomness", "RulesKernel.Randomness",
                             packable=True, target_frameworks=["net8.0", "net10.0"],
                             write_lock=False)

        self.assertFailsWith(rc.check_target_frameworks, "no packages.lock.json")

    def test_a_long_form_lock_entry_is_the_same_framework(self) -> None:
        # NuGet writes the short TFM for .NETCoreApp but the long form for .NETStandard, so
        # the first version of this check reported RulesKernel.Analyzers as broken when it
        # was correct. A false positive in a gate is not a smaller bug than a false negative.
        self.fixture.project("src/RulesKernel.Analyzers", "RulesKernel.Analyzers",
                             packable=True, target_frameworks=["netstandard2.0"],
                             lock_frameworks=[".NETStandard,Version=v2.0"])

        self.assertClean(rc.check_target_frameworks)

    def test_a_test_project_is_out_of_scope(self) -> None:
        # Only what ships carries the commitment. A test project targets whatever the
        # repository's own SDK is, and changing that breaks nobody downstream.
        self.fixture.project("tests/RulesKernel.Tests", "RulesKernel.Tests", packable=False,
                             target_frameworks=["net10.0"],
                             project_refs=["../../src/RulesKernel/RulesKernel.csproj"])

        self.assertClean(rc.check_target_frameworks)


# ------------------------------------------------------------------ the C# text lexer


class StripCsNoiseTests(unittest.TestCase):
    def strip(self, text: str) -> str:
        return rc.strip_cs_noise(text)

    def test_line_and_column_positions_are_preserved(self) -> None:
        text = 'var a = 1;\n// comment\nvar b = "x";\n'
        stripped = self.strip(text)
        self.assertEqual(len(text), len(stripped))
        self.assertEqual(len(text.splitlines()), len(stripped.splitlines()))

    def test_a_plain_comment_about_a_rule_is_not_code(self) -> None:
        # Regression. Both checks skipped only lines starting with `///`, so an ordinary
        # `//` comment containing DateTime or `new Random()` failed the build -- writing
        # about a rule broke it, which made the checks stricter on English than on C#.
        self.assertNotIn("DateTime", self.strip("// never call DateTime.UtcNow here\n"))
        self.assertNotIn("Random", self.strip("int x = 1; // not new Random(), obviously\n"))

    def test_a_doc_comment_is_not_a_hiding_place(self) -> None:
        # The other half of the same bug: `///` lines were skipped entirely, so they were
        # the one place real code could sit unexamined. Now they are blanked as comments,
        # and code on the same line before them is still code.
        self.assertNotIn("UtcNow", self.strip("/// <summary>DateTime.UtcNow</summary>\n"))
        self.assertIn("UtcNow", self.strip("var t = DateTime.UtcNow; /// trailing\n"))

    def test_string_literal_contents_are_blanked(self) -> None:
        self.assertNotIn("UtcNow", self.strip('var s = "DateTime.UtcNow";\n'))

    def test_verbatim_and_raw_strings_are_blanked(self) -> None:
        self.assertNotIn("UtcNow", self.strip('var s = @"C:\\DateTime.UtcNow";\n'))
        self.assertNotIn("UtcNow", self.strip('var s = """\nDateTime.UtcNow\n""";\n'))

    def test_a_doubled_quote_does_not_end_a_verbatim_string(self) -> None:
        self.assertNotIn("UtcNow", self.strip('var s = @"a""b DateTime.UtcNow";\n'))

    def test_an_interpolation_hole_is_code(self) -> None:
        # Deliberate: `$"{DateTime.UtcNow}"` is a call, and blanking it would have created
        # exactly the hiding place this function exists to remove.
        self.assertIn("DateTime.UtcNow", self.strip('var s = $"at {DateTime.UtcNow}";\n'))
        self.assertNotIn("literal", self.strip('var s = $"literal {x}";\n'))

    def test_a_doubled_brace_is_not_an_interpolation_hole(self) -> None:
        self.assertNotIn("UtcNow", self.strip('var s = $"{{DateTime.UtcNow}}";\n'))

    def test_a_quote_in_a_char_literal_does_not_open_a_string(self) -> None:
        self.assertIn("DateTime.UtcNow", self.strip("var q = '\"'; var t = DateTime.UtcNow;\n"))

    def test_block_comments_span_lines(self) -> None:
        stripped = self.strip("/* DateTime.UtcNow\n   new Random()\n*/ var ok = 1;\n")
        self.assertNotIn("UtcNow", stripped)
        self.assertNotIn("Random", stripped)
        self.assertIn("var ok", stripped)


# ------------------------------------------------------------------------ determinism


KERNEL_CS = "src/RulesKernel/Offender.cs"
TESTING_CS = "tests/RulesKernel.Testing/Offender.cs"
TEST_PROJECT_CS = "tests/RulesKernel.Tests/Offender.cs"


class DeterminismScopeTests(CheckTestCase):
    def test_the_packaged_test_support_package_is_in_scope(self) -> None:
        # Regression, and the most consequential hole in the previous file:
        # check_determinism globbed `src/**/*.cs`, so tests/RulesKernel.Testing -- which
        # sets PackageId, does not set IsPackable=false, and is therefore PUBLISHED to
        # nuget.org -- was examined by no source check at all. This exact file passed.
        self.fixture.write(TESTING_CS, """namespace RulesKernel.Testing;

public static class Offender
{
    public static uint Draw() => Random.Shared.NextUInt32();
    public static object Stamp() => DateTime.UtcNow;
    public static object Id() => Guid.NewGuid();
    public static void Go() => Task.Run(() => 1);
    public static string Read() => File.ReadAllText("x");
}
""")
        found = self.failures(rc.check_determinism)
        for expected in ("Random.Shared", "ambient clock", "non-reproducible",
                         "non-deterministic", "filesystem"):
            self.assertTrue(any(expected in f for f in found),
                            f"expected a failure mentioning {expected!r}, got: {found}")

    def test_packaged_scope_is_read_from_disk_not_from_a_list(self) -> None:
        packaged = rc.packaged_projects(self.root)
        self.assertEqual(
            {"RulesKernel", "RulesKernel.Randomness", "RulesKernel.Testing",
             "RulesKernel.Analyzers"},
            set(packaged),
        )

    def test_a_test_project_may_do_what_the_kernel_may_not(self) -> None:
        # Positive case for the scope decision: a test is allowed to construct a clock or
        # read a fixture, because doing so is often how it proves the kernel does not.
        self.fixture.write(TEST_PROJECT_CS, """namespace RulesKernel.Tests;

public static class Offender
{
    public static object Stamp() => DateTime.UtcNow;
    public static string Read() => File.ReadAllText("x");
}
""")
        self.assertClean(rc.check_determinism)
        self.assertClean(rc.check_core_boundary)

    def test_a_project_nested_deeper_than_one_level_is_still_packaged_scope(self) -> None:
        self.fixture.project("src/RulesKernel/Deep", "Deep", packable=True)
        self.fixture.write("src/RulesKernel/Deep/Offender.cs",
                           "namespace Deep;\npublic static class O { public static object T() => DateTime.UtcNow; }\n")
        self.assertFailsWith(rc.check_determinism, "ambient clock")


class DeterminismPatternTests(CheckTestCase):
    """Each of these is a line that passed the previous blacklist."""

    def assertKernelFails(self, body: str, needle: str) -> None:
        self.fixture.write(KERNEL_CS, f"namespace RulesKernel;\n\npublic static class Offender\n{{\n{body}\n}}\n")
        self.assertFailsWith(rc.check_determinism, needle)

    def test_file_append_all_text(self) -> None:
        # Was: File.(Read|Write|Open|Exists|Delete). AppendAllText is none of those.
        self.assertKernelFails('    public static void W() => File.AppendAllText("a", "b");',
                               "filesystem")

    def test_stopwatch_elapsed_ticks(self) -> None:
        # Was: Stopwatch.(GetTimestamp|StartNew). A field's .ElapsedTicks is neither.
        self.assertKernelFails("    public static long T(Stopwatch w) => w.ElapsedTicks;",
                               "process timing")

    def test_environment_process_path(self) -> None:
        # Was: Environment.(GetEnvironmentVariable|CurrentDirectory|MachineName).
        self.assertKernelFails("    public static string? P() => Environment.ProcessPath;",
                               "machine-dependent")

    def test_environment_new_line_is_platform_dependent(self) -> None:
        self.assertKernelFails("    public static string N() => Environment.NewLine;",
                               "machine-dependent")

    def test_task_factory_start_new(self) -> None:
        self.assertKernelFails("    public static void G() => Task.Factory.StartNew(() => 1);",
                               "non-deterministic")

    def test_parallel_for(self) -> None:
        self.assertKernelFails("    public static void G() => Parallel.For(0, 10, i => { });",
                               "non-deterministic")

    def test_new_thread(self) -> None:
        self.assertKernelFails("    public static void G() { var t = new Thread(() => { }); t.Start(); }",
                               "non-deterministic")

    def test_thread_pool_queue_user_work_item(self) -> None:
        self.assertKernelFails("    public static void G() => ThreadPool.QueueUserWorkItem(_ => { });",
                               "non-deterministic")

    def test_guid_create_version7(self) -> None:
        # Version 7 embeds a timestamp: ambient entropy AND an ambient clock.
        self.assertKernelFails("    public static object I() => Guid.CreateVersion7();",
                               "non-reproducible")

    def test_time_provider(self) -> None:
        self.assertKernelFails("    public static object N() => TimeProvider.System.GetUtcNow();",
                               "ambient clock")

    def test_socket(self) -> None:
        self.assertKernelFails("    public static void G(Socket s) { }", "network")

    def test_using_static_defeats_every_member_qualified_rule(self) -> None:
        # `using static System.DateTime;` turns DateTime.UtcNow into a bare `UtcNow`, which
        # no member-qualified pattern can see. The construct itself is banned.
        self.fixture.write(KERNEL_CS, """using static System.DateTime;

namespace RulesKernel;

public static class Offender
{
    public static object Stamp() => UtcNow;
}
""")
        self.assertFailsWith(rc.check_determinism, "static import")

    def test_guid_parse_is_not_banned(self) -> None:
        # Positive case: the ban is on the two factories, not on the type. A parsed Guid
        # is a value the caller supplied.
        self.fixture.write(KERNEL_CS, """namespace RulesKernel;

public static class Offender
{
    public static object I(string s) => Guid.Parse(s);
    public static object E() => Guid.Empty;
}
""")
        self.assertClean(rc.check_determinism)

    def test_a_comment_mentioning_a_banned_call_does_not_fail(self) -> None:
        # Regression: this was hit three times while building these fixtures.
        self.fixture.write(KERNEL_CS, """namespace RulesKernel;

// This type exists so nothing needs new Random() or DateTime.UtcNow.
public static class Offender
{
    /// <summary>Never returns Guid.NewGuid().</summary>
    public static int Value => 1;
}
""")
        self.assertClean(rc.check_determinism)

    def test_implicit_usings_means_no_using_line_is_required(self) -> None:
        # Directory.Build.props sets ImplicitUsings=enable, so System.IO, System.Net.Http,
        # System.Threading and System.Linq are in scope with no `using` line. Any rule of
        # the form "does this file import System.IO" would prove nothing. These patterns
        # match the call, not the import; this fixture has no usings at all.
        self.fixture.write(KERNEL_CS, """namespace RulesKernel;

public static class Offender
{
    public static string R() => File.ReadAllText("x");
}
""")
        self.assertFailsWith(rc.check_determinism, "filesystem")


class CoreBoundaryTests(CheckTestCase):
    def test_the_kernel_may_not_name_randomness_at_all(self) -> None:
        self.fixture.write(KERNEL_CS,
                           "namespace RulesKernel;\npublic static class O { public static Random? R; }\n")
        self.assertFailsWith(rc.check_core_boundary, "randomness is optional")

    def test_the_randomness_package_may_name_randomness(self) -> None:
        # Positive case for the scope split: a pseudorandom generator is that package's
        # entire content, so the kernel-only rule must not reach it.
        self.fixture.write("src/RulesKernel.Randomness/Source.cs",
                           "namespace RulesKernel.Randomness;\npublic interface IRandomSource { uint NextUInt32(); }\n")
        self.assertClean(rc.check_core_boundary)
        self.assertClean(rc.check_determinism)

    def test_scope_is_the_kernel_only(self) -> None:
        self.fixture.write("src/RulesKernel.Randomness/Offender.cs",
                           "namespace RulesKernel.Randomness;\npublic static class O { public static Random? R; }\n")
        self.assertClean(rc.check_core_boundary)

    def test_running_this_check_alone_proves_what_the_docs_say_it_proves(self) -> None:
        # docs/architecture.md tells a reader that `--only core-boundary` forbids the
        # kernel from touching the filesystem, a clock, the environment or the network.
        # If those rules lived only in check_determinism, that sentence would be false
        # whenever anyone ran this check on its own -- and `--only` would be a way to get
        # a PASS out of a check that never looked.
        for body, needle in (
            ('    public static string R() => File.ReadAllText("x");', "filesystem"),
            ("    public static object T() => DateTime.UtcNow;", "ambient clock"),
            ("    public static string? E() => Environment.ProcessPath;", "machine-dependent"),
            ("    public static void N(HttpClient c) { }", "network"),
        ):
            with self.subTest(body=body.strip()):
                self.fixture.write(
                    KERNEL_CS,
                    f"namespace RulesKernel;\n\npublic static class Offender\n{{\n{body}\n}}\n")
                self.assertFailsWith(rc.check_core_boundary, needle)


class OrderingTests(CheckTestCase):
    def test_order_by_in_a_packaged_assembly_fails(self) -> None:
        self.fixture.write(KERNEL_CS, """using System.Linq;

namespace RulesKernel;

public static class Offender
{
    public static object S(IEnumerable<int> xs) => xs.OrderBy(x => x);
}
""")
        self.assertFailsWith(rc.check_ordering, "ordered by construction")

    def test_reverse_fails(self) -> None:
        self.fixture.write(KERNEL_CS,
                           "namespace RulesKernel;\npublic static class O { public static void S(List<int> x) => x.Reverse(); }\n")
        self.assertFailsWith(rc.check_ordering, "discards the order")

    def test_a_test_project_may_sort(self) -> None:
        self.fixture.write(TEST_PROJECT_CS,
                           "namespace RulesKernel.Tests;\npublic static class O { public static object S(IEnumerable<int> x) => x.OrderBy(i => i); }\n")
        self.assertClean(rc.check_ordering)


class AllowMarkerTests(CheckTestCase):
    def test_a_marker_with_a_reason_suppresses_and_is_reported(self) -> None:
        self.fixture.write(KERNEL_CS, f"""namespace RulesKernel;

public static class Offender
{{
    public static object S() => DateTime.UtcNow; // {rc.ALLOW_MARKER}: canonicalising for a hash, never observed
}}
""")
        result = rc.check_determinism(self.root)
        self.assertEqual([], [str(f) for f in result.failures])
        self.assertEqual(1, len(result.allowances),
                         "a suppression that is not reported is a hole, not an escape hatch")
        self.assertIn("canonicalising", result.allowances[0])

    def test_a_marker_with_no_reason_is_itself_a_failure(self) -> None:
        # The previous marker disabled both checks on any line, with no inventory, no
        # justification and no cap.
        self.fixture.write(KERNEL_CS, f"""namespace RulesKernel;

public static class Offender
{{
    public static object S() => DateTime.UtcNow; // {rc.ALLOW_MARKER}
}}
""")
        self.assertFailsWith(rc.check_determinism, "with no reason after it")

    def test_a_marker_with_a_token_reason_is_still_a_failure(self) -> None:
        self.fixture.write(KERNEL_CS, f"""namespace RulesKernel;

public static class Offender
{{
    public static object S() => DateTime.UtcNow; // {rc.ALLOW_MARKER}: ok
}}
""")
        self.assertFailsWith(rc.check_determinism, "with no reason after it")


# ---------------------------------------------------------------------------- layering


class LayeringTests(CheckTestCase):
    def test_a_double_quoted_forbidden_reference_fails(self) -> None:
        self.fixture.project(
            "src/RulesKernel", "RulesKernel", packable=True,
            project_refs=["../../tests/RulesKernel.Testing/RulesKernel.Testing.csproj"])
        self.fixture.write_solution()
        self.assertFailsWith(rc.check_layering, "test-support package")

    def test_a_single_quoted_reference_is_not_a_bypass(self) -> None:
        # Regression. MSBuild accepts single quotes; the previous PROJECT_REF regex
        # required double ones. So a single-quoted reference from src/RulesKernel to
        # tests/RulesKernel.Testing -- the one violation docs/architecture.md says is
        # "specifically enforced" -- passed the check that claims to enforce it.
        self.fixture.project(
            "src/RulesKernel", "RulesKernel", packable=True,
            raw_items="  <ItemGroup>\n"
                      "    <ProjectReference Include='../../tests/RulesKernel.Testing/RulesKernel.Testing.csproj' />\n"
                      "  </ItemGroup>\n")
        self.fixture.write_solution()
        self.assertFailsWith(rc.check_layering, "test-support package")

    def test_a_package_reference_to_a_sibling_package_is_a_dependency(self) -> None:
        # Regression. The floor could depend on the layer above it through nuget.org:
        # `<PackageReference Include="RulesKernel.Randomness" />` inside src/RulesKernel
        # was unchecked, because only ProjectReference was read.
        self.fixture.project("src/RulesKernel", "RulesKernel", packable=True,
                             package_refs=["RulesKernel.Randomness"])
        self.fixture.write_solution()
        self.assertFailsWith(rc.check_layering, "PackageReference to 'RulesKernel.Randomness'")

    def test_a_package_reference_to_an_allowed_project_is_still_the_wrong_mechanism(self) -> None:
        self.fixture.project("src/RulesKernel.Randomness", "RulesKernel.Randomness",
                             packable=True, package_refs=["RulesKernel"])
        self.fixture.write_solution()
        self.assertFailsWith(rc.check_layering, "the mechanism is not")

    def test_a_third_party_package_reference_is_not_a_layering_violation(self) -> None:
        self.fixture.project("tests/RulesKernel.Tests", "RulesKernel.Tests", packable=False,
                             project_refs=["../../src/RulesKernel/RulesKernel.csproj"],
                             package_refs=["xunit", "Microsoft.NET.Test.Sdk"])
        self.fixture.write_solution()
        self.assertClean(rc.check_layering)

    def test_a_project_reference_injected_from_directory_build_props_is_reported(self) -> None:
        # MSBuild injects it into every project in the tree, where a csproj-text check is
        # structurally blind to it. The check cannot resolve that, so it says so rather
        # than continuing to claim it has read the graph.
        self.fixture.write("Directory.Build.props", """<Project>
  <ItemGroup>
    <ProjectReference Include="tests/RulesKernel.Testing/RulesKernel.Testing.csproj" />
  </ItemGroup>
</Project>
""")
        self.assertFailsWith(rc.check_layering, "injects it into every project")

    def test_a_deeply_nested_project_is_not_invisible(self) -> None:
        # Regression. The glob was `{src,tests,probes}/*/*.csproj`: one level deep, three
        # directories. src/X/Deep/Deep.csproj was invisible, and being absent from the
        # declared graph is itself the violation this check exists to catch.
        self.fixture.project("src/RulesKernel/Deep", "Deep", packable=False)
        self.assertFailsWith(rc.check_layering, "not declared in ALLOWED_PROJECT_REFS")

    def test_a_project_outside_the_three_known_directories_is_not_invisible(self) -> None:
        self.fixture.project("samples/App", "App", packable=False)
        self.assertFailsWith(rc.check_layering, "not declared in ALLOWED_PROJECT_REFS")

    def test_a_declared_project_missing_from_disk_fails(self) -> None:
        shutil.rmtree(self.root / "probes")
        self.fixture.write_solution()
        self.assertFailsWith(rc.check_layering, "has no csproj on disk")

    def test_a_project_in_the_wrong_directory_fails(self) -> None:
        shutil.rmtree(self.root / "probes/RegulatoryProbe.Tests")
        self.fixture.project("tests/RegulatoryProbe.Tests", "RegulatoryProbe.Tests",
                             packable=False,
                             project_refs=["../../src/RulesKernel/RulesKernel.csproj"])
        self.fixture.write_solution()
        self.assertFailsWith(rc.check_layering, "sits under")

    def test_two_projects_with_the_same_name_are_reported(self) -> None:
        self.fixture.project("samples/RulesKernel", "RulesKernel", packable=False)
        self.assertFailsWith(rc.check_layering, "a second project named")


class SolutionMembershipTests(CheckTestCase):
    def test_a_project_missing_from_the_solution_fails(self) -> None:
        # Nothing verified this. Deleting one line from RulesKernel.slnx removes a project
        # from every build and every test run, and the gate stays green throughout.
        text = (self.root / rc.SOLUTION_FILE).read_text(encoding="utf-8")
        pruned = "\n".join(line for line in text.splitlines()
                           if "RegulatoryProbe.Tests" not in line)
        self.fixture.write(rc.SOLUTION_FILE, pruned + "\n")
        self.assertFailsWith(rc.check_solution_membership, "never built and never tested")

    def test_a_solution_entry_with_no_project_on_disk_fails(self) -> None:
        text = (self.root / rc.SOLUTION_FILE).read_text(encoding="utf-8")
        self.fixture.write(rc.SOLUTION_FILE, text.replace(
            "</Folder>", '    <Project Path="src/Ghost/Ghost.csproj" />\n  </Folder>'))
        self.assertFailsWith(rc.check_solution_membership, "which is not on disk")

    def test_a_missing_solution_file_fails(self) -> None:
        (self.root / rc.SOLUTION_FILE).unlink()
        self.assertFailsWith(rc.check_solution_membership, "does not exist")

    def test_backslash_paths_are_understood(self) -> None:
        # Visual Studio writes Windows separators. A path that differs only in separator
        # must not be read as a project missing from the solution.
        text = (self.root / rc.SOLUTION_FILE).read_text(encoding="utf-8")
        windows = re.sub(
            r'Path="([^"]+)"',
            lambda m: 'Path="' + m.group(1).replace("/", "\\") + '"',
            text,
        )
        self.assertIn("\\", windows)
        self.fixture.write(rc.SOLUTION_FILE, windows)
        self.assertClean(rc.check_solution_membership)


# ----------------------------------------------------------------------- doc-references


class DocReferenceTests(CheckTestCase):
    def test_an_external_url_containing_a_repository_prefix_is_not_a_reference(self) -> None:
        # Regression introduced the same day this file was written: a link to
        # https://github.com/imneme/pcg-c-basic/blob/master/src/pcg_basic.c failed the
        # build because of its `src/` segment.
        self.fixture.write("docs/links.md", """# Links

The reference implementation is
<https://github.com/imneme/pcg-c-basic/blob/master/src/pcg_basic.c>, and the expected
output lives at https://github.com/imneme/pcg-c-basic/blob/master/test-high/expected/check-pcg32.out
""")
        self.assertClean(rc.check_doc_references)

    def test_a_dangling_docs_reference_still_fails(self) -> None:
        self.fixture.write("docs/links.md", "See docs/nothing-here.md for details.\n")
        self.assertFailsWith(rc.check_doc_references, "docs/nothing-here.md")

    def test_a_github_workflow_reference_is_checked(self) -> None:
        # Uncaught before: the prefix allowlist was (docs|tools|scripts|probes|src|tests).
        self.fixture.write("docs/ci.md", "CI is .github/workflows/nope.yml\n")
        self.assertFailsWith(rc.check_doc_references, ".github/workflows/nope.yml")

    def test_an_existing_github_workflow_reference_passes(self) -> None:
        self.fixture.write("docs/ci.md", "CI is .github/workflows/build-and-test.yml\n")
        self.assertClean(rc.check_doc_references)

    def test_root_files_cited_in_prose_are_checked(self) -> None:
        for name in ("global.json", "Directory.Build.props", "Directory.Packages.props",
                     "RulesKernel.slnx", "LICENSE", ".editorconfig", ".gitattributes"):
            with self.subTest(name=name):
                self.fixture.write("docs/roots.md", f"The pin lives in {name}.\n")
                exists = (self.root / name).exists()
                found = self.failures(rc.check_doc_references)
                if exists:
                    self.assertEqual([], found)
                else:
                    self.assertTrue(any(name in f for f in found),
                                    f"{name} was cited and does not exist, but nothing failed")

    def test_extensions_outside_the_old_allowlist_are_checked(self) -> None:
        # The old extension allowlist missed .ps1 .txt .h .xml .nuspec .config .sln .toml.
        for name in ("scripts/build.ps1", "docs/notes.txt", "src/native/pcg.h",
                     "tools/settings.xml", "src/pack.nuspec", "tools/app.config",
                     "Legacy.sln", "tools/config.toml"):
            with self.subTest(name=name):
                self.fixture.write("docs/ext.md", f"See {name}.\n")
                if name == "Legacy.sln":
                    # Not under a known prefix and not a known root file: deliberately
                    # out of scope, because "Legacy.sln" in prose is as likely to be a
                    # product name as a path. Documented, not silently missing.
                    self.assertClean(rc.check_doc_references)
                else:
                    self.assertFailsWith(rc.check_doc_references, name)

    def test_extensionless_paths_are_checked(self) -> None:
        # This is how prose usually names a project or a directory, and it was entirely
        # unchecked: only paths ending in .md were matched at all.
        self.fixture.write("docs/names.md",
                           "The doubles live in tests/RulesKernel.Testing and the identity "
                           "types in src/RulesKernel/Identity.\n")
        self.assertFailsWith(rc.check_doc_references, "src/RulesKernel/Identity")

    def test_an_extensionless_directory_that_exists_passes(self) -> None:
        self.fixture.write("docs/names.md",
                           "The doubles live in tests/RulesKernel.Testing; decisions are in "
                           "docs/decisions.\n")
        self.assertClean(rc.check_doc_references)

    def test_adr_shorthand_is_checked(self) -> None:
        # A real mis-citation (ADR 0002 where ADR 0005 was meant) lived in this repository
        # undetected, because only the `docs/decisions/NNNN` spelling was checked.
        for text in ("See ADR 0009 for the rationale.\n",
                     "See decision 0009 for the rationale.\n",
                     "See docs/decisions/0009 for the rationale.\n"):
            with self.subTest(text=text.strip()):
                self.fixture.write("docs/adr.md", text)
                self.assertFailsWith(rc.check_doc_references, "decision 0009")

    def test_adr_shorthand_that_resolves_passes(self) -> None:
        self.fixture.write("docs/adr.md",
                           "See ADR 0002, decision 0005, and docs/decisions/0001.\n")
        self.assertClean(rc.check_doc_references)

    def test_a_relative_reference_resolves_against_the_citing_file(self) -> None:
        # Relative refs used to resolve against the repository root, so `../docs/foo.md`
        # written inside docs/decisions/ was checked as a path it never meant.
        self.fixture.write("docs/decisions/0002-randomness-is-optional.md",
                           "# 0002\n\nSee [architecture](../architecture.md).\n")
        self.assertClean(rc.check_doc_references)

    def test_a_relative_reference_that_resolves_nowhere_fails(self) -> None:
        self.fixture.write("docs/decisions/0002-randomness-is-optional.md",
                           "# 0002\n\nSee [nope](../nowhere.md).\n")
        self.assertFailsWith(rc.check_doc_references, "../nowhere.md")

    def test_a_relative_reference_escaping_the_repository_fails(self) -> None:
        self.fixture.write("docs/escape.md", "See ../../../etc/passwd for details.\n")
        self.assertFailsWith(rc.check_doc_references, "escapes the repository")

    def test_a_glob_is_not_a_path(self) -> None:
        self.fixture.write("docs/globs.md",
                           "The old glob was {src,tests,probes}/*/*.csproj and src/**/*.cs.\n")
        self.assertClean(rc.check_doc_references)

    def test_a_reference_inside_a_cs_file_is_checked(self) -> None:
        # A predecessor shipped 61 dangling pointers, several inside runtime error messages
        # handed to users. Those live in .cs, not in .md.
        self.fixture.write(KERNEL_CS,
                           'namespace RulesKernel;\n'
                           '// Regenerating: tools/pcg-vectors/generate.sh\n'
                           'public static class O { }\n')
        self.assertFailsWith(rc.check_doc_references, "tools/pcg-vectors/generate.sh")

    def test_the_check_does_not_prove_a_citation_is_the_right_one(self) -> None:
        # Stated so the limit is a decision rather than a surprise: existence is all this
        # check establishes.
        self.fixture.write("docs/wrong.md", "The layering rule is in docs/architecture.md.\n")
        self.assertClean(rc.check_doc_references)


# --------------------------------------------------------------------- text hygiene


class TextHygieneTests(CheckTestCase):
    def test_a_bom_fails(self) -> None:
        self.fixture.write_bytes("docs/bom.md", b"\xef\xbb\xbf# Title\n")
        self.assertFailsWith(rc.check_text_hygiene, "BOM")

    def test_crlf_fails(self) -> None:
        self.fixture.write_bytes("docs/crlf.md", b"# Title\r\n")
        self.assertFailsWith(rc.check_text_hygiene, "CRLF")

    def test_a_lone_cr_fails(self) -> None:
        # The previous rule tested for \r\n only, so a file whose every line ended in a
        # bare CR passed. A bare CR also makes a terminal overwrite the line it just
        # printed, which is a way to hide a line from anyone reading output.
        self.fixture.write_bytes("docs/cr.md", b"# Title\rhidden\n")
        self.assertFailsWith(rc.check_text_hygiene, "lone CR")

    def test_a_missing_trailing_newline_fails(self) -> None:
        self.fixture.write_bytes("docs/eof.md", b"# Title")
        self.assertFailsWith(rc.check_text_hygiene, "missing trailing newline")

    def test_two_trailing_newlines_fail(self) -> None:
        self.fixture.write_bytes("docs/eof.md", b"# Title\n\n")
        self.assertFailsWith(rc.check_text_hygiene, "more than one trailing newline")

    def test_invalid_utf8_fails(self) -> None:
        self.fixture.write_bytes("docs/bad.md", b"# T\xfe\xff\n")
        self.assertFailsWith(rc.check_text_hygiene, "not valid UTF-8")

    def test_packages_lock_json_keeps_its_narrow_exemption(self) -> None:
        # No trailing newline is tolerated because NuGet rewrites the file that way on
        # every regeneration. Encoding and line endings are not tolerated.
        self.fixture.write_bytes("src/RulesKernel/packages.lock.json", b'{"version": 1}')
        self.assertClean(rc.check_text_hygiene)
        self.fixture.write_bytes("src/RulesKernel/packages.lock.json", b'{"version": 1}\r\n')
        self.assertFailsWith(rc.check_text_hygiene, "CRLF")

    def test_a_bidi_override_fails(self) -> None:
        # Trojan Source. A right-to-left override inside a comment makes the rendered order
        # of a line differ from the order the compiler reads, which breaks this
        # repository's foundational claim that two people reading the same bytes see the
        # same thing.
        self.fixture.write(KERNEL_CS,
                           "namespace RulesKernel;\n"
                           "// if (isAdmin) \u202e { return; }\n"
                           "public static class O { }\n")
        self.assertFailsWith(rc.check_text_hygiene, "bidirectional control")

    def test_a_bidi_isolate_fails(self) -> None:
        self.fixture.write("docs/bidi.md", "Title \u2066reordered\u2069 here\n")
        self.assertFailsWith(rc.check_text_hygiene, "bidirectional control")

    def test_a_zero_width_character_fails(self) -> None:
        self.fixture.write(KERNEL_CS,
                           "namespace RulesKernel;\npublic static class O\u200b { }\n")
        self.assertFailsWith(rc.check_text_hygiene, "zero-width")

    def test_a_cyrillic_homoglyph_identifier_fails(self) -> None:
        # U+0435 renders identically to ASCII 'e'. Two symbols, one apparent name.
        self.fixture.write(KERNEL_CS,
                           "namespace RulesKernel;\n"
                           "public static class O { public static int R\u0435solve() => 1; }\n")
        self.assertFailsWith(rc.check_text_hygiene, "non-ASCII")

    def test_non_ascii_in_a_cs_string_or_comment_is_allowed(self) -> None:
        # Positive case for the scope of the ASCII rule: this repository legitimately needs
        # the section sign in citations, and the rule is about what the compiler binds.
        self.fixture.write(KERNEL_CS,
                           "namespace RulesKernel;\n"
                           "// A citation such as \u00a7 402(g)(1).\n"
                           'public static class O { public const string C = "\u00a7 414(v)"; }\n')
        self.assertClean(rc.check_text_hygiene)

    def test_non_ascii_prose_in_markdown_is_allowed(self) -> None:
        self.fixture.write("docs/prose.md", "An em dash \u2014 and a section sign \u00a7.\n")
        self.assertClean(rc.check_text_hygiene)


# ------------------------------------------------------------------------- parseable


class ParseableTests(CheckTestCase):
    def test_a_malformed_csproj_fails(self) -> None:
        self.fixture.write("src/RulesKernel/RulesKernel.csproj", "<Project><PropertyGroup>\n")
        self.assertFailsWith(rc.check_parseable, "not well-formed XML")

    def test_a_double_hyphen_in_an_xml_comment_fails(self) -> None:
        # The concrete case this check was written for: MSBuild reports a downstream
        # symptom (an empty TargetFramework, layers away) rather than a parse error.
        self.fixture.write("Directory.Build.props",
                           "<Project>\n  <!-- regenerate with --force-evaluate -->\n</Project>\n")
        self.assertFailsWith(rc.check_parseable, "not well-formed XML")

    def test_a_malformed_workflow_fails(self) -> None:
        # A malformed workflow is NOT an error on GitHub: the workflow silently does not
        # run, and with no branch protection CI simply stops existing.
        self.fixture.write(".github/workflows/broken.yml",
                           "name: ci\non:\n  push:\n   branches: [main\njobs: {}\n")
        found = self.failures(rc.check_parseable)
        self.assertTrue(any("broken.yml" in f for f in found),
                        f"a malformed workflow must be reported, got: {found}")

    def test_a_valid_workflow_passes(self) -> None:
        self.assertClean(rc.check_parseable)

    def test_a_malformed_json_fails(self) -> None:
        self.fixture.write("global.json", '{\n  "sdk": {\n}\n')
        self.assertFailsWith(rc.check_parseable, "not well-formed JSON")

    def test_yaml_is_either_parsed_or_reported_as_unverified(self) -> None:
        # The honest failure mode: if PyYAML cannot be imported the check must say the
        # file was not verified rather than report `ok`, because a gate that reports PASS
        # while proving less than it claims is worse than no gate.
        self.fixture.write(".github/workflows/broken.yml", "a:\n  b: [1\n")
        found = self.failures(rc.check_parseable)
        self.assertTrue(
            any("not well-formed YAML" in f or "NOT verified" in f for f in found),
            f"expected a YAML verdict of some kind, got: {found}",
        )


# ------------------------------------------------------------------------ action pins


class ActionPinTests(CheckTestCase):
    def test_a_sha_pinned_action_passes(self) -> None:
        self.assertClean(rc.check_action_pins)
        self.assertExamined(rc.check_action_pins)

    def test_a_tag_pinned_action_fails(self) -> None:
        self.fixture.write(".github/workflows/build-and-test.yml",
                           WORKFLOW.replace("actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1",
                                            "actions/checkout@v7"))
        self.assertFailsWith(rc.check_action_pins, "not pinned")

    def test_a_branch_pinned_action_fails(self) -> None:
        self.fixture.write(".github/workflows/build-and-test.yml",
                           WORKFLOW.replace("actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1",
                                            "actions/checkout@main"))
        self.assertFailsWith(rc.check_action_pins, "not pinned")

    def test_a_short_sha_fails(self) -> None:
        self.fixture.write(".github/workflows/build-and-test.yml",
                           WORKFLOW.replace("actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1",
                                            "actions/checkout@3d3c42e"))
        self.assertFailsWith(rc.check_action_pins, "not pinned")

    def test_a_local_action_is_exempt(self) -> None:
        self.fixture.write(".github/workflows/build-and-test.yml",
                           WORKFLOW.replace("actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1",
                                            "./.github/actions/setup"))
        self.assertClean(rc.check_action_pins)

    def test_a_nested_action_path_is_accepted_when_pinned(self) -> None:
        self.fixture.write(".github/workflows/build-and-test.yml",
                           WORKFLOW.replace("actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1",
                                            "owner/repo/sub/action@3d3c42e5aac5ba805825da76410c181273ba90b1"))
        self.assertClean(rc.check_action_pins)


# ------------------------------------------------------------------- file discovery


class RepoFilesTests(CheckTestCase):
    def _git(self, *args: str) -> None:
        subprocess.run(["git", *args], cwd=self.root, check=True,
                       capture_output=True, text=True)

    @unittest.skipIf(shutil.which("git") is None, "git is not installed")
    def test_an_untracked_file_is_examined(self) -> None:
        # validate.sh is meant to run BEFORE `git add`, so a newly created .cs file is
        # compiled by the build step and shipped in the package while being invisible to
        # every check that asked git for its file list.
        self._git("init", "-q")
        self._git("config", "user.email", "t@example.invalid")
        self._git("config", "user.name", "T")
        self._git("add", "-A")
        self._git("commit", "-qm", "fixture")
        self.fixture.write(KERNEL_CS,
                           "namespace RulesKernel;\n"
                           "public static class O { public static object T() => DateTime.UtcNow; }\n")
        names = {p.relative_to(self.root).as_posix() for p in rc.repo_files(self.root)}
        self.assertIn(KERNEL_CS, names)
        self.fixture.write("docs/untracked.md", "See docs/nowhere.md\n")
        self.assertFailsWith(rc.check_doc_references, "docs/nowhere.md")

    @unittest.skipIf(shutil.which("git") is None, "git is not installed")
    def test_a_gitignored_file_is_not_examined(self) -> None:
        self._git("init", "-q")
        self.fixture.write(".gitignore", "ignored/\n")
        self.fixture.write("ignored/generated.md", "See docs/nowhere.md\n")
        names = {p.relative_to(self.root).as_posix() for p in rc.repo_files(self.root)}
        self.assertNotIn("ignored/generated.md", names)
        self.assertClean(rc.check_doc_references)

    def test_a_tree_that_is_not_a_git_checkout_is_still_examined(self) -> None:
        self.assertGreater(len(rc.repo_files(self.root)), 10)
        self.assertClean(rc.check_doc_references)


# ------------------------------------------------------------------------- exit code


class ExitCodeTests(unittest.TestCase):
    """The gate's verdict must match what the run actually proved."""

    def setUp(self) -> None:
        self.empty = Path(tempfile.mkdtemp(prefix="repo-checks-empty-"))
        self.addCleanup(shutil.rmtree, self.empty, True)

    @staticmethod
    def run_main(argv: list[str]) -> int:
        """Call main() with its report captured, so test output stays readable."""
        with contextlib.redirect_stdout(io.StringIO()):
            return rc.main(argv)

    def test_a_run_in_which_every_check_skips_does_not_report_pass(self) -> None:
        # The exact bug: main() returned `1 if total else 0`, and `skipped` was computed,
        # printed, and then discarded. A repository in which all six checks examined
        # nothing exited 0, and scripts/validate.sh printed PASS.
        code = self.run_main(["--root", str(self.empty), "--json"])
        self.assertEqual(1, code, "a run that proved nothing must not exit 0")

    def test_one_skipped_check_fails_the_run(self) -> None:
        (self.empty / "docs").mkdir()
        (self.empty / "docs" / "x.md").write_text("# x\n", encoding="utf-8")
        code = self.run_main(["--root", str(self.empty), "--only", "action-pins", "--json"])
        self.assertEqual(1, code)

    def test_allow_skip_is_the_documented_escape_and_is_not_the_default(self) -> None:
        code = self.run_main(["--root", str(self.empty), "--only", "action-pins",
                        "--allow-skip", "--json"])
        self.assertEqual(0, code)

    def test_a_clean_repository_exits_zero(self) -> None:
        fixture = Fixture()
        self.addCleanup(fixture.cleanup)
        self.assertEqual(0, self.run_main(["--root", str(fixture.root), "--json"]))

    def test_a_failing_check_exits_one(self) -> None:
        fixture = Fixture()
        self.addCleanup(fixture.cleanup)
        fixture.write("docs/dangling.md", "See docs/nowhere.md\n")
        self.assertEqual(1, self.run_main(["--root", str(fixture.root), "--json"]))

    def test_every_check_is_registered(self) -> None:
        # A check function that exists but is not in CHECKS never runs, and nothing else
        # would notice.
        defined = {name for name in dir(rc)
                   if name.startswith("check_") and callable(getattr(rc, name))}
        registered = {f.__name__ for f in rc.CHECKS.values()}
        self.assertEqual(defined, registered)


if __name__ == "__main__":
    unittest.main()
