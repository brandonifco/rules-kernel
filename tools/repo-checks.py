#!/usr/bin/env python3
"""repo-checks -- mechanical enforcement of this kernel's invariants.

Every check here exists because the alternative is trusting a person or an agent to
remember a rule stated in prose. Prose does not fail a build. These do.

    tools/repo-checks.py            run every check
    tools/repo-checks.py --json     machine-readable result
    tools/repo-checks.py --only layering

Checks:
  text-hygiene     UTF-8, no BOM, LF endings, exactly one trailing newline
  xml-wellformed   every csproj/props/targets/slnx actually parses
  layering         the declared ProjectReference graph matches docs/architecture.md
  core-boundary    RulesKernel touches no filesystem, clock, environment, or randomness
  determinism      no ambient randomness or ambient time anywhere in src/
  doc-references   every referenced repository document actually exists

A check that examined nothing reports `skip`, never `ok`. A predecessor of this file
printed `ok` for four checks whose inputs did not exist in the repository, which is the
precise failure mode the whole file exists to prevent -- a gate reporting PASS while
proving less than it claims.
"""
from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
import xml.parsers.expat
from dataclasses import dataclass, field
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

TEXT_SUFFIXES = {
    ".cs", ".csproj", ".props", ".targets", ".slnx", ".json", ".md",
    ".yml", ".yaml", ".sh", ".py", ".editorconfig", ".gitattributes", ".gitignore",
}

# ---------------------------------------------------------------- the declared graph
#
# The floor is RulesKernel; nothing points upward. RulesKernel.Testing lives under
# tests/ because it is test-support, not a layer of the engine -- but it is packaged,
# because an engine built on this kernel needs the same scripted source in its own
# tests. No src/ project may reference it.
#
# When the factory gains its configuration layer this moves into declared data so a
# consuming engine can add a layer without editing Python. Until then it lives here,
# once, and check_layering fails on any project present on disk but absent below --
# an undeclared project must not silently escape enforcement.
ALLOWED_PROJECT_REFS: dict[str, set[str]] = {
    "RulesKernel": set(),
    "RulesKernel.Randomness": {"RulesKernel"},
    "RulesKernel.Testing": {"RulesKernel.Randomness"},
    "RulesKernel.Tests": {"RulesKernel"},
    "RulesKernel.Randomness.Tests": {"RulesKernel.Randomness", "RulesKernel.Testing"},
    # A probe: proves the kernel is usable by something that is not a game. Its
    # reference set is deliberately just the kernel -- see probes/README.md.
    "RegulatoryProbe.Tests": {"RulesKernel"},
}

PROJECT_DIRS: dict[str, str] = {
    "RulesKernel": "src",
    "RulesKernel.Randomness": "src",
    "RulesKernel.Testing": "tests",
    "RulesKernel.Tests": "tests",
    "RulesKernel.Randomness.Tests": "tests",
    "RegulatoryProbe.Tests": "probes",
}

SRC_PROJECTS = {name for name, where in PROJECT_DIRS.items() if where == "src"}

# The kernel is the floor of the graph and the floor of the trust model: it must resolve
# a rule from its arguments alone. Anything below reaches outside them.
CORE_BOUNDARY_BANNED: list[tuple[str, str, str]] = [
    ("System.IO", r"\bSystem\s*\.\s*IO\b", "the kernel must not touch the filesystem"),
    ("File.", r"\bFile\s*\.\s*(Read|Write|Open|Exists|Delete)", "the kernel must not touch the filesystem"),
    ("Directory.", r"\bDirectory\s*\.\s*(GetFiles|CreateDirectory|Exists)", "the kernel must not touch the filesystem"),
    ("DateTime.Now", r"\bDateTime\s*\.\s*(Now|UtcNow|Today)\b", "an ambient clock breaks replay"),
    ("DateTimeOffset.Now", r"\bDateTimeOffset\s*\.\s*(Now|UtcNow)\b", "an ambient clock breaks replay"),
    ("Environment.", r"\bEnvironment\s*\.\s*(GetEnvironmentVariable|CurrentDirectory|MachineName)",
     "the kernel must not read its environment"),
    ("HttpClient", r"\bHttpClient\b", "the kernel must not reach the network"),
    ("Random", r"\bRandom\b", "randomness is optional and lives in RulesKernel.Randomness"),
]

# Applies to every src/ project, the randomness package included: the kernel provides a
# pinned generator precisely so nothing reaches for an ambient one.
DETERMINISM_BANNED: list[tuple[str, str, str]] = [
    ("Random.Shared", r"\bRandom\s*\.\s*Shared\b", "Random.Shared is process-global ambient entropy"),
    ("new Random()", r"\bnew\s+Random\s*\(", "new Random() is ambient entropy and is not replay-stable across runtimes"),
    ("RandomNumberGenerator", r"\bRandomNumberGenerator\b", "a cryptographic RNG is not replayable"),
    ("Guid.NewGuid()", r"\bGuid\s*\.\s*NewGuid\s*\(", "Guid.NewGuid() as engine state is non-reproducible"),
    ("DateTime.Now", r"\bDateTime\s*\.\s*(Now|UtcNow|Today)\b", "an ambient clock breaks replay"),
    ("Stopwatch", r"\bStopwatch\s*\.\s*(GetTimestamp|StartNew)", "process timing is not a rules input"),
    ("Task.Run", r"\bTask\s*\.\s*Run\b", "concurrency makes resolution order non-deterministic"),
    ("AsParallel", r"\.\s*AsParallel\s*\(", "PLINQ makes iteration order non-deterministic"),
]

ALLOW_MARKER = "kernel:allow-nondeterminism"

# Documents referenced from code, comments, or prose. A reference to a file that does not
# exist is how a predecessor shipped 61 dangling pointers inside runtime error messages.
DOC_REFERENCE = re.compile(r"\b(?:docs/[A-Za-z0-9_./-]+\.md|CLAUDE\.md|AGENTS\.md|README\.md)\b")
DOC_DECISION_SHORTHAND = re.compile(r"\bdocs/decisions/(\d{4})\b")


class Failure(str):
    """A single human-readable check failure."""


@dataclass
class CheckResult:
    failures: list[Failure] = field(default_factory=list)
    examined: int = 0


def tracked_files(root: Path) -> list[Path]:
    result = subprocess.run(
        ["git", "ls-files", "-z"], cwd=root, capture_output=True, text=True, check=False
    )
    if result.returncode == 0:
        tracked = [root / name for name in result.stdout.split("\0") if name]
        if tracked:
            return tracked

    # Either this is not a git checkout, or it is one with nothing tracked yet -- a freshly
    # initialised repository before its first `git add`. Both are the same situation for a
    # checker: there is a working tree in front of it and it must examine that rather than
    # examine nothing and report success. `git ls-files` succeeding with empty output is
    # the case that matters, because it does not look like a failure.
    ignored = {".git", "bin", "obj", ".dotnet", ".venv", "__pycache__", "artifacts", "TestResults"}
    return [
        p for p in sorted(root.rglob("*"))
        if p.is_file() and not any(part in ignored for part in p.parts)
    ]


def source_files(root: Path) -> list[Path]:
    return [
        p for p in sorted((root / "src").rglob("*.cs"))
        if not any(part in {"obj", "bin"} for part in p.parts)
    ] if (root / "src").is_dir() else []


# --------------------------------------------------------------------------- checks


def check_text_hygiene(root: Path) -> CheckResult:
    result = CheckResult()
    for path in tracked_files(root):
        if path.suffix not in TEXT_SUFFIXES and path.name not in TEXT_SUFFIXES:
            continue
        if not path.is_file():
            continue
        result.examined += 1
        raw = path.read_bytes()
        rel = path.relative_to(root)
        if raw.startswith(b"\xef\xbb\xbf"):
            result.failures.append(Failure(f"{rel}: UTF-8 BOM"))
        try:
            raw.decode("utf-8")
        except UnicodeDecodeError:
            result.failures.append(Failure(f"{rel}: not valid UTF-8"))
            continue
        if b"\r\n" in raw:
            result.failures.append(Failure(f"{rel}: CRLF line endings (LF required)"))
        # NuGet writes packages.lock.json without a trailing newline and rewrites it on
        # every regeneration, so enforcing the rule here would mean a failure that returns
        # the moment a dependency changes. The exemption is narrow on purpose: encoding,
        # BOM and line endings are still checked on these files, because those would be
        # real problems rather than a generator's house style.
        if path.name != "packages.lock.json":
            if raw and not raw.endswith(b"\n"):
                result.failures.append(Failure(f"{rel}: missing trailing newline"))
            if raw.endswith(b"\n\n"):
                result.failures.append(Failure(f"{rel}: more than one trailing newline"))
    return result


PROJECT_REF = re.compile(r'ProjectReference\s+Include\s*=\s*"([^"]+)"')


def check_layering(root: Path) -> CheckResult:
    """The declared ProjectReference graph must match the architecture, exactly.

    This reads the csproj files rather than compiled output, which makes it exact: the C#
    compiler omits references a compilation does not actually use, so the reflective
    ArchitectureTests pass vacuously until real code crosses a boundary. This check bites
    the moment a reference is written, including on an empty project.
    """
    result = CheckResult()
    found: dict[str, Path] = {}
    for where in ("src", "tests", "probes"):
        directory = root / where
        if not directory.is_dir():
            continue
        for csproj in sorted(directory.glob("*/*.csproj")):
            found[csproj.stem] = csproj

    for name, csproj in found.items():
        result.examined += 1
        rel = csproj.relative_to(root)
        if name not in ALLOWED_PROJECT_REFS:
            result.failures.append(
                Failure(f"{rel}: project '{name}' is not declared in ALLOWED_PROJECT_REFS; "
                        "an undeclared project escapes layering enforcement")
            )
            continue
        expected_dir = PROJECT_DIRS.get(name)
        actual_dir = rel.parts[0]
        if expected_dir != actual_dir:
            result.failures.append(
                Failure(f"{rel}: project '{name}' is declared to live under "
                        f"'{expected_dir}/' but sits under '{actual_dir}/'")
            )
        actual = {Path(m).stem for m in PROJECT_REF.findall(csproj.read_text(encoding="utf-8"))}
        allowed = ALLOWED_PROJECT_REFS[name]
        for forbidden in sorted(actual - allowed):
            result.failures.append(
                Failure(f"{rel}: references '{forbidden}', which the declared graph forbids")
            )
        if name in SRC_PROJECTS and "RulesKernel.Testing" in actual:
            result.failures.append(
                Failure(f"{rel}: a src/ project must never reference the test-support package")
            )

    for declared in ALLOWED_PROJECT_REFS:
        if declared not in found:
            result.failures.append(
                Failure(f"declared project '{declared}' has no csproj on disk")
            )
    return result


def check_core_boundary(root: Path) -> CheckResult:
    """RulesKernel resolves from its arguments alone."""
    result = CheckResult()
    kernel = root / "src" / "RulesKernel"
    if not kernel.is_dir():
        return result
    for path in sorted(kernel.rglob("*.cs")):
        if any(part in {"obj", "bin"} for part in path.parts):
            continue
        result.examined += 1
        rel = path.relative_to(root)
        for lineno, line in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
            if ALLOW_MARKER in line or line.lstrip().startswith("///"):
                continue
            for _display, pattern, why in CORE_BOUNDARY_BANNED:
                if re.search(pattern, line):
                    result.failures.append(
                        Failure(f"{rel}:{lineno}: {why}  [{line.strip()[:70]}]")
                    )
    return result


def check_determinism(root: Path) -> CheckResult:
    result = CheckResult()
    for path in source_files(root):
        result.examined += 1
        rel = path.relative_to(root)
        for lineno, line in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
            if ALLOW_MARKER in line or line.lstrip().startswith("///"):
                continue
            for _display, pattern, why in DETERMINISM_BANNED:
                if re.search(pattern, line):
                    result.failures.append(
                        Failure(f"{rel}:{lineno}: {why}  [{line.strip()[:70]}]")
                    )
    return result


def check_doc_references(root: Path) -> CheckResult:
    """Every repository document a file points at must exist.

    A predecessor shipped 61 references to deleted documents, several inside runtime error
    messages handed to users. A reference is a promise; this check keeps it.
    """
    result = CheckResult()
    decisions = root / "docs" / "decisions"
    known_decisions = (
        {p.name[:4] for p in decisions.glob("*.md") if p.name[:4].isdigit()}
        if decisions.is_dir() else set()
    )

    for path in tracked_files(root):
        if path.suffix not in TEXT_SUFFIXES and path.name not in TEXT_SUFFIXES:
            continue
        if not path.is_file():
            continue
        try:
            text = path.read_text(encoding="utf-8")
        except (UnicodeDecodeError, OSError):
            continue
        result.examined += 1
        rel = path.relative_to(root)
        if rel.as_posix() == "tools/repo-checks.py":
            continue  # defines the patterns
        for match in sorted(set(DOC_REFERENCE.findall(text))):
            if not (root / match).is_file():
                result.failures.append(Failure(f"{rel}: references '{match}', which does not exist"))
        for number in sorted(set(DOC_DECISION_SHORTHAND.findall(text))):
            if number not in known_decisions:
                result.failures.append(
                    Failure(f"{rel}: references decision {number}, which does not exist")
                )
    return result


def check_xml_wellformed(root: Path) -> CheckResult:
    """Every MSBuild file must actually parse.

    An XML comment may not contain a double hyphen. Writing a flag such as the restore
    regeneration switch inside a comment in Directory.Build.props therefore makes the file
    malformed -- and MSBuild does not report that as a parse error. It reports a downstream
    symptom instead: every project loses the properties that file was supposed to set, and
    the build fails with an empty TargetFramework, several layers away from the cause.
    This check names the real problem at the file that has it.
    """
    result = CheckResult()
    for path in tracked_files(root):
        if path.suffix not in {".csproj", ".props", ".targets", ".slnx"} or not path.is_file():
            continue
        result.examined += 1
        rel = path.relative_to(root)
        parser = xml.parsers.expat.ParserCreate()
        try:
            parser.Parse(path.read_bytes(), True)
        except xml.parsers.expat.ExpatError as error:
            result.failures.append(Failure(f"{rel}: not well-formed XML: {error}"))
    return result


CHECKS = {
    "text-hygiene": check_text_hygiene,
    "xml-wellformed": check_xml_wellformed,
    "layering": check_layering,
    "core-boundary": check_core_boundary,
    "determinism": check_determinism,
    "doc-references": check_doc_references,
}


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="repo-checks.py", description=__doc__.splitlines()[0])
    parser.add_argument("--root", default=str(ROOT))
    parser.add_argument("--only", action="append", choices=sorted(CHECKS), default=None)
    parser.add_argument("--json", action="store_true")
    args = parser.parse_args(argv)

    root = Path(args.root).resolve()
    names = args.only or sorted(CHECKS)
    results = {name: CHECKS[name](root) for name in names}

    total = sum(len(r.failures) for r in results.values())
    skipped = [name for name, r in results.items() if r.examined == 0]

    if args.json:
        print(json.dumps({
            "failures": total,
            "skipped": skipped,
            "checks": {
                name: {"failures": [str(f) for f in r.failures], "examined": r.examined}
                for name, r in results.items()
            },
        }, indent=2))
    else:
        for name in names:
            r = results[name]
            if r.failures:
                print(f"FAIL  {name}  ({len(r.failures)})")
                for problem in r.failures:
                    print(f"        {problem}")
            elif r.examined == 0:
                print(f"skip  {name}  (nothing in scope -- this check proved nothing)")
            else:
                print(f"ok    {name}  ({r.examined} examined)")
        print()
        if skipped:
            print(f"repo-checks: {len(skipped)} check(s) examined nothing: {', '.join(skipped)}")
        print("repo-checks: PASS" if total == 0 else f"repo-checks: {total} failure(s)")

    return 1 if total else 0


if __name__ == "__main__":
    raise SystemExit(main())
