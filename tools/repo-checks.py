#!/usr/bin/env python3
"""repo-checks -- mechanical enforcement of this kernel's invariants.

Every check here exists because the alternative is trusting a person or an agent to
remember a rule stated in prose. Prose does not fail a build. These do.

    tools/repo-checks.py            run every check
    tools/repo-checks.py --json     machine-readable result
    tools/repo-checks.py --only layering

Checks:
  text-hygiene        UTF-8, no BOM, LF endings, one trailing newline, no invisible or
                      homoglyph-capable characters
  parseable           every XML, YAML and JSON file in the repository actually parses
  layering            the declared project-dependency graph matches docs/architecture.md
  solution-membership every csproj on disk is in RulesKernel.slnx, and vice versa
  core-boundary       RulesKernel touches no filesystem, clock, environment, network or
                      randomness, and resolves from its arguments alone
  determinism         no ambient entropy, clock, environment, filesystem, network or
                      concurrency in any assembly this repository publishes
  ordering            packaged code does not re-sort a result that was ordered by
                      construction
  doc-references      every referenced repository path actually exists
  action-pins         every GitHub Action is pinned to a 40-hex commit SHA
  target-frameworks   every packaged project still targets what ADR 0008 committed to,
                      and its lock file covers the same set
  doc-samples         every C# block in living documentation is a compiled sample, verbatim
  dev-version         living documentation names no development version but the current one

A check that examined nothing reports `skip`, and a skip fails the run. Every check in
this file has scope in this repository -- there is no project, document or workflow it
could legitimately find nothing to look at -- so `examined == 0` means the check lost its
inputs, not that the repository is clean. `--allow-skip` exists for running this file
against a synthetic tree (its own test suite does exactly that) and must not be used in
the gate. A predecessor of this file printed `ok` for four checks whose inputs did not
exist, and a later one returned exit 0 from a run in which every check skipped: a gate
reporting PASS while proving less than it claims is worse than no gate, because it is
trusted.

What this file does NOT prove
-----------------------------
The determinism, core-boundary and ordering checks are pattern blacklists over C# source
text. A blacklist catches the ways a rule gets broken by accident and the ways it gets
broken by someone who does not know the rule. It does not catch someone who knows the
rule and wants around it: reflection, an extension method named to hide its receiver, a
`using` alias, a helper in another assembly, or source generation all defeat it. The
compiled-assembly `ArchitectureTests` and code review are the other nets. These checks
are the cheap one that runs on every commit.
"""
from __future__ import annotations

import argparse
import json
import re
import subprocess
import xml.etree.ElementTree as ElementTree
import xml.parsers.expat
from dataclasses import dataclass, field
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

# Directories that never contain repository source: build output and tool caches. Used
# when walking the filesystem directly rather than asking git.
#
# Every comparison below is against the path RELATIVE TO the root being walked, never the
# absolute one. Testing absolute parts makes the location of the checkout part of the
# answer: a clone under any directory named `packages`, `artifacts` or `obj` would have had
# every file skipped, and a check that examined nothing is a check that proved nothing. The
# same shape, with `worktrees`, really did zero validate.sh's test-project count when it ran
# from a git worktree.
IGNORED_PARTS = {
    ".git", "bin", "obj", ".dotnet", ".venv", "__pycache__", "artifacts", "TestResults",
    "node_modules", ".vs", "packages",
}

# Suffixes and bare filenames that are definitely text. A file outside this set is still
# treated as text if it contains no NUL byte -- the set exists to catch encoding errors in
# files we know are text, not to decide what a file is.
TEXT_SUFFIXES = {
    ".cs", ".csproj", ".props", ".targets", ".slnx", ".sln", ".json", ".md",
    ".yml", ".yaml", ".sh", ".ps1", ".py", ".txt", ".xml", ".nuspec", ".config",
    ".toml", ".h", ".c", ".editorconfig", ".gitattributes", ".gitignore",
}
TEXT_NAMES = {"LICENSE", "NOTICE", "AUTHORS", ".editorconfig", ".gitattributes", ".gitignore"}
BINARY_SUFFIXES = {
    ".png", ".jpg", ".jpeg", ".gif", ".ico", ".pdf", ".zip", ".nupkg", ".snupkg",
    ".dll", ".exe", ".pdb", ".so", ".dylib", ".woff", ".woff2", ".ttf",
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
    # No reference to RulesKernel, deliberately. The analyzer inspects a consumer's symbols;
    # it has no use for the kernel's types, and an edge here would put a Roslyn pin in the
    # graph of anything that took it. See docs/decisions/0011.
    "RulesKernel.Analyzers": set(),
    "RulesKernel.Testing": {"RulesKernel.Randomness"},
    "RulesKernel.Tests": {"RulesKernel"},
    "RulesKernel.Randomness.Tests": {"RulesKernel.Randomness", "RulesKernel.Testing"},
    "RulesKernel.Analyzers.Tests": {"RulesKernel.Analyzers"},
    # A probe: proves the kernel is usable by something that is not a game. Its
    # reference set is deliberately just the kernel -- see probes/README.md.
    "RegulatoryProbe.Tests": {"RulesKernel"},
    # README's C# blocks, compiled. The kernel alone, because that is all README shows.
    "RulesKernel.Documentation.Tests": {"RulesKernel"},
}

# ------------------------------------------------- the frameworks each package commits to
#
# docs/decisions/0008 makes net8.0 a commitment, not a convenience: SRD_Combat pins SDK
# 8.0.129 with rollForward disabled, and a net10.0-only package locked it out of the kernel
# entirely. Dropping a target framework is a breaking change for every consumer pinned to
# it, so it must be a decision that supersedes 0008 rather than an edit.
#
# `dotnet restore --locked-mode` already fails when a lock file and its project disagree,
# which is how a dependency bump that regenerates the lock files gets caught. It cannot
# catch the case this exists for: dropping a framework from BOTH, where the two agree and
# the commitment is simply gone.
#
# RulesKernel.Analyzers is netstandard2.0 and is not part of the 0008 commitment. An
# analyzer is loaded by the consumer's compiler, which loads netstandard2.0; see
# docs/decisions/0011.
EXPECTED_TARGET_FRAMEWORKS: dict[str, set[str]] = {
    "RulesKernel": {"net8.0", "net10.0"},
    "RulesKernel.Randomness": {"net8.0", "net10.0"},
    "RulesKernel.Testing": {"net8.0", "net10.0"},
    "RulesKernel.Analyzers": {"netstandard2.0"},
}

PROJECT_DIRS: dict[str, str] = {
    "RulesKernel": "src",
    "RulesKernel.Randomness": "src",
    "RulesKernel.Analyzers": "src",
    "RulesKernel.Testing": "tests",
    "RulesKernel.Tests": "tests",
    "RulesKernel.Randomness.Tests": "tests",
    "RulesKernel.Analyzers.Tests": "tests",
    "RegulatoryProbe.Tests": "probes",
    "RulesKernel.Documentation.Tests": "tests",
}

SRC_PROJECTS = {name for name, where in PROJECT_DIRS.items() if where == "src"}

SOLUTION_FILE = "RulesKernel.slnx"

# --------------------------------------------------------------------- banned patterns
#
# Scope, and why it is what it is.
#
# `determinism` applies to every assembly this repository PUBLISHES -- which is decided
# from the csproj on disk (packable unless it sets IsPackable=false), not from a list
# here, so adding a package cannot forget to add it to this check. That is the right
# boundary because a published assembly is executed inside somebody else's engine, and an
# ambient clock or an ambient thread pool inside RulesKernel.Testing corrupts a downstream
# engine's replay exactly as thoroughly as one inside RulesKernel. RulesKernel.Testing is
# packaged (it sets PackageId and does not set IsPackable=false) and was previously
# checked by nothing at all.
#
# `core-boundary` applies to RulesKernel alone and adds the two rules that are true only
# of the floor: it must contain no randomness whatsoever, not even the pinned kind, and it
# must not name an I/O namespace. RulesKernel.Randomness cannot be held to the first (a
# pseudorandom generator is its entire content) and neither package needs the second
# stated twice.
#
# Test projects (IsPackable=false) are deliberately outside both. A test may construct a
# clock, spawn a thread, or read a fixture file in order to prove the kernel does not.

# Rules for every packaged assembly.
DETERMINISM_BANNED: list[tuple[str, str, str]] = [
    # --- ambient entropy
    ("Random.Shared", r"\bRandom\s*\.\s*Shared\b", "Random.Shared is process-global ambient entropy"),
    ("new Random()", r"\bnew\s+Random\s*\(", "new Random() is ambient entropy and is not replay-stable across runtimes"),
    ("RandomNumberGenerator", r"\bRandomNumberGenerator\b", "a cryptographic RNG is not replayable"),
    # Guid.CreateVersion7 embeds a timestamp, so it is an ambient clock as well as ambient
    # entropy. Both Guid factories are banned; Guid.Parse and Guid.Empty are not.
    ("Guid factory", r"\bGuid\s*\.\s*(NewGuid|CreateVersion7)\s*\(",
     "a generated Guid as engine state is non-reproducible"),
    # --- ambient clock
    ("DateTime clock", r"\bDateTime\s*\.\s*(Now|UtcNow|Today)\b", "an ambient clock breaks replay"),
    ("DateTimeOffset clock", r"\bDateTimeOffset\s*\.\s*(Now|UtcNow)\b", "an ambient clock breaks replay"),
    # The whole type: TimeProvider.System is the ambient clock wearing an abstraction, and
    # a TimeProvider field in a packaged assembly is a clock the caller cannot see.
    ("TimeProvider", r"\bTimeProvider\b", "TimeProvider.System is an ambient clock behind an interface"),
    # The whole type. The previous rule listed GetTimestamp and StartNew, so
    # `stopwatch.ElapsedTicks` passed. Process timing is never a rules input.
    ("Stopwatch", r"\bStopwatch\b", "process timing is not a rules input"),
    ("Environment.TickCount", r"\bEnvironment\s*\.\s*TickCount", "the tick count is an ambient clock"),
    # --- ambient environment
    # The whole type. The previous rule named three members, so Environment.ProcessPath,
    # Environment.ProcessorCount and Environment.NewLine passed -- and NewLine in
    # particular differs between platforms, which is precisely a reproducibility bug.
    ("Environment.", r"\bEnvironment\s*\.\s*[A-Za-z_]", "reading the environment makes a result machine-dependent"),
    # --- filesystem
    # Whole types, not selected members. `File.AppendAllText` passed the previous rule.
    ("System.IO", r"\bSystem\s*\.\s*IO\b", "a packaged assembly must not touch the filesystem"),
    ("File.", r"\bFile\s*\.\s*[A-Za-z_]", "a packaged assembly must not touch the filesystem"),
    ("Directory.", r"\bDirectory\s*\.\s*[A-Za-z_]", "a packaged assembly must not touch the filesystem"),
    ("FileStream", r"\b(FileStream|StreamReader|StreamWriter|FileInfo|DirectoryInfo)\b",
     "a packaged assembly must not touch the filesystem"),
    # --- network
    ("HttpClient", r"\b(HttpClient|WebClient|HttpRequestMessage)\b", "a packaged assembly must not reach the network"),
    ("Socket", r"\b(Socket|TcpClient|UdpClient|NetworkStream)\b", "a packaged assembly must not reach the network"),
    ("Dns.", r"\bDns\s*\.\s*[A-Za-z_]", "a packaged assembly must not reach the network"),
    # --- concurrency
    # Task.Run was banned; Task.Factory.StartNew, Parallel.For, new Thread and
    # ThreadPool.QueueUserWorkItem were not. All four produce the same problem.
    ("Task.Run", r"\bTask\s*\.\s*(Run|Factory)\b", "concurrency makes resolution order non-deterministic"),
    ("Parallel.", r"\bParallel\s*\.\s*[A-Za-z_]", "concurrency makes resolution order non-deterministic"),
    ("new Thread", r"\bnew\s+Thread\s*\(", "concurrency makes resolution order non-deterministic"),
    ("ThreadPool.", r"\bThreadPool\s*\.\s*[A-Za-z_]", "concurrency makes resolution order non-deterministic"),
    ("AsParallel", r"\.\s*AsParallel\s*\(", "PLINQ makes iteration order non-deterministic"),
    # --- the escape from every member-qualified rule above
    # `using static System.DateTime;` turns `DateTime.UtcNow` into a bare `UtcNow`, which
    # no pattern above matches. Rather than guess at every bare member name, ban the
    # construct: a packaged assembly has no reason to import a System type's static
    # members into the global scope, and doing so makes this whole check unreadable.
    ("using static System.", r"^\s*using\s+static\s+System\s*\.",
     "a static import of a System type hides ambient members from every rule in this check"),
]

# Rules that are true only of the kernel, the floor of the graph and of the trust model.
CORE_BOUNDARY_BANNED: list[tuple[str, str, str]] = [
    ("Random", r"\bRandom\b", "randomness is optional and lives in RulesKernel.Randomness"),
    ("System.Net", r"\bSystem\s*\.\s*Net\b", "the kernel must not reach the network"),
    ("System.Threading", r"\bSystem\s*\.\s*Threading\b", "the kernel resolves synchronously from its arguments"),
]

# CLAUDE.md: "Never sort an ordered result afterwards; the order IS the evidence." An
# ordered history records the sequence in which things actually happened; sorting it
# destroys the evidence that the sequence was deterministic. There are legitimate sorts --
# producing a canonical form for a hash, for instance -- which is why this rule, uniquely,
# is expected to need the allow-marker occasionally rather than never.
ORDERING_BANNED: list[tuple[str, str, str]] = [
    ("OrderBy", r"\.\s*OrderBy(Descending)?\s*\(", "an ordered result is ordered by construction, never sorted afterwards"),
    (".Sort(", r"\.\s*Sort\s*\(", "an ordered result is ordered by construction, never sorted afterwards"),
    (".Reverse()", r"\.\s*Reverse\s*\(\s*\)", "reversing an ordered history discards the order that was the evidence"),
    ("Array.Sort", r"\bArray\s*\.\s*Sort\s*\(", "an ordered result is ordered by construction, never sorted afterwards"),
]

# A marker disables the banned-pattern rules on the line that carries it. It must state a
# reason: `// kernel:allow-nondeterminism: <why>`. An unexplained suppression is how a
# blacklist becomes decorative, so a marker with no reason after it is itself a failure,
# and every run reports how many allowances are in force. There are currently zero.
ALLOW_MARKER = "kernel:allow-nondeterminism"
ALLOW_MARKER_WITH_REASON = re.compile(
    re.escape(ALLOW_MARKER) + r"\s*:\s*(?P<reason>\S.{11,})"
)
MIN_ALLOW_REASON = 12

# ------------------------------------------------------------------- doc references
#
# Documents and files referenced from code, comments, prose or scripts. A reference to a
# path that does not exist is how a predecessor shipped 61 dangling pointers, several of
# them inside runtime error messages handed to users.
#
# The rule is existence, not extension: anything path-shaped that begins with a directory
# this repository actually has, or that names a root file this repository actually has,
# must resolve to a file or a directory. An earlier version allowlisted seven extensions
# and six prefixes, which meant `.github/workflows/build-and-test.yml`, `global.json`,
# `Directory.Build.props` and every extensionless project or directory name -- the usual
# way prose names a project -- were cited with nothing checking them.
REFERENCE_PREFIXES = ("docs", "tools", "scripts", "probes", "src", "tests", "samples", ".github")
# Deliberately no NOTICE or AUTHORS: those are ordinary English words as often as they
# are filenames, and the Apache licence text names NOTICE half a dozen times without
# claiming this repository has one.
ROOT_FILE_REFERENCES = (
    "CLAUDE.md", "AGENTS.md", "README.md", "LICENSE", "global.json",
    "Directory.Build.props", "Directory.Packages.props", "RulesKernel.slnx",
    ".editorconfig", ".gitattributes", ".gitignore",
)

# A URL's path looks exactly like a repository path, so
# `https://github.com/imneme/pcg-c-basic/blob/master/src/pcg_basic.c` used to fail the
# build on account of its `src/` segment. URLs are stripped before anything else runs.
URL = re.compile(r"\b(?:https?|ftp|git|ssh|mailto)://\S+|\bwww\.\S+", re.IGNORECASE)

_PATH_CHARS = r"[A-Za-z0-9_.*?{}~-]"
REFERENCE_TOKEN = re.compile(
    r"(?<![\w./-])"
    r"("
    r"(?:\.{1,2}/)+" + _PATH_CHARS + r"+(?:/" + _PATH_CHARS + r"+)*"      # ./x, ../x/y
    r"|(?:" + "|".join(re.escape(p) for p in REFERENCE_PREFIXES) + r")"
    r"(?:/" + _PATH_CHARS + r"+)+"                                        # docs/x/y
    r"|(?:" + "|".join(re.escape(p) for p in ROOT_FILE_REFERENCES) + r")"  # README.md
    r")"
)
GLOB_CHARS = set("*?{}")

# `docs/decisions/0002`, `ADR 0002`, `decision 0002`. A real mis-citation (ADR 0002 named
# where ADR 0005 was meant) lived in this repository undetected because only the first
# spelling was checked.
DOC_DECISION_SHORTHAND = re.compile(
    r"\bdocs/decisions/(\d{4})\b"
    r"|\b(?:ADR|adr|[Dd]ecision)\s+#?(\d{4})\b"
)

# This file defines the patterns, and its test suite is built out of deliberately dangling
# references used as fixtures. Neither is making a citation.
# LICENSE is verbatim upstream Apache text. This repository did not write it and cannot
# fix a reference inside it, so scanning it would produce failures nobody may act on.
DOC_REFERENCE_EXEMPT = ("tools/repo-checks.py", "tools/tests/", "LICENSE")

# Invisible and direction-changing characters. A repository whose claim is that two people
# reading the same bytes see the same thing cannot contain characters that make rendered
# text differ from executed text. This is the Trojan Source class of attack.
BIDI_CONTROLS = {
    "\u202a": "LEFT-TO-RIGHT EMBEDDING", "\u202b": "RIGHT-TO-LEFT EMBEDDING",
    "\u202c": "POP DIRECTIONAL FORMATTING", "\u202d": "LEFT-TO-RIGHT OVERRIDE",
    "\u202e": "RIGHT-TO-LEFT OVERRIDE", "\u2066": "LEFT-TO-RIGHT ISOLATE",
    "\u2067": "RIGHT-TO-LEFT ISOLATE", "\u2068": "FIRST STRONG ISOLATE",
    "\u2069": "POP DIRECTIONAL ISOLATE", "\u200e": "LEFT-TO-RIGHT MARK",
    "\u200f": "RIGHT-TO-LEFT MARK", "\u061c": "ARABIC LETTER MARK",
}
ZERO_WIDTH = {
    "\u200b": "ZERO WIDTH SPACE", "\u200c": "ZERO WIDTH NON-JOINER",
    "\u200d": "ZERO WIDTH JOINER", "\u2060": "WORD JOINER",
    "\ufeff": "ZERO WIDTH NO-BREAK SPACE", "\u00ad": "SOFT HYPHEN",
}

# Every GitHub Action must be pinned to an immutable commit. A tag is a moving pointer the
# action's owner can repoint at any time, which makes it somebody else's decision what code
# runs with this repository's OIDC token. All five `uses:` in this repository are already
# SHA-pinned; nothing kept them that way, and GitHub's own sha_pinning_required is off.
ACTION_PIN = re.compile(r"^[A-Za-z0-9][\w.-]*/[\w.-]+(?:/[\w./-]+)?@[0-9a-f]{40}$")
USES_LINE = re.compile(r"^\s*(?:-\s*)?uses\s*:\s*(?P<value>[^#]+?)\s*(?:#.*)?$")


class Failure(str):
    """A single human-readable check failure."""


@dataclass
class CheckResult:
    failures: list[Failure] = field(default_factory=list)
    examined: int = 0
    # Suppressions in force, reported on every run. A blacklist with unbounded, unreported
    # suppressions proves nothing about the lines that carry them.
    allowances: list[str] = field(default_factory=list)


# ---------------------------------------------------------------------- file discovery


def repo_files(root: Path) -> list[Path]:
    """Every file in the working tree that is not ignored -- tracked or not.

    Asking git only for tracked files was a hole with a specific shape: validate.sh is
    meant to run BEFORE `git add`, so a newly written .cs file is compiled by the build
    step, shipped in the package, and examined by none of the text checks. `--others
    --exclude-standard` adds exactly the untracked files that are not gitignored, which is
    the set the next commit will contain.
    """
    collected: list[Path] = []
    seen: set[Path] = set()
    ok = False
    for args in (["git", "ls-files", "-z"],
                 ["git", "ls-files", "-z", "--others", "--exclude-standard"]):
        result = subprocess.run(args, cwd=root, capture_output=True, text=True, check=False)
        if result.returncode != 0:
            continue
        ok = True
        for name in result.stdout.split("\0"):
            if not name:
                continue
            path = root / name
            if path not in seen and path.is_file():
                seen.add(path)
                collected.append(path)

    if ok and collected:
        return sorted(collected)

    # Either this is not a git checkout, or it is one with nothing tracked yet -- a freshly
    # initialised repository before its first `git add`. Both are the same situation for a
    # checker: there is a working tree in front of it and it must examine that rather than
    # examine nothing and report success. `git ls-files` succeeding with empty output is
    # the case that matters, because it does not look like a failure.
    return [
        p for p in sorted(root.rglob("*"))
        if p.is_file() and not any(part in IGNORED_PARTS for part in p.relative_to(root).parts)
    ]


# Kept under the old name because scripts and habits use it; repo_files is the honest one.
tracked_files = repo_files


def is_probably_text(path: Path, raw: bytes) -> bool:
    if path.suffix.lower() in BINARY_SUFFIXES:
        return False
    if path.suffix in TEXT_SUFFIXES or path.name in TEXT_NAMES:
        return True
    return b"\0" not in raw[:8192]


def all_csproj(root: Path) -> list[Path]:
    """Every csproj in the repository, at any depth.

    The previous glob was `{src,tests,probes}/*/*.csproj`: exactly one level deep, in
    exactly three directories. `src/X/Deep/Deep.csproj` and `samples/App/App.csproj` were
    invisible to the layering check, which is the one place a project must not be able to
    hide, because being absent from the declared graph is itself the violation.

    Scoped to the repository's own files -- tracked, or untracked but not gitignored --
    rather than to a raw filesystem walk. A raw walk descends into nested checkouts that
    live inside the working directory but belong to a different repository: agent
    worktrees under .claude/worktrees/ each contain a full copy of every project, and the
    first run of the solution-membership check reported 42 failures demanding they all be
    added to this repository's solution. A project that git does not consider part of this
    repository is not this repository's to enforce.
    """
    inventory = {f.resolve() for f in repo_files(root)}
    return sorted(
        p for p in root.rglob("*.csproj")
        if not any(part in IGNORED_PARTS for part in p.relative_to(root).parts)
        and p.resolve() in inventory
    )


def packaged_projects(root: Path) -> dict[str, Path]:
    """Projects that ship as NuGet packages, read from disk rather than listed here.

    A project is packable unless it says `<IsPackable>false</IsPackable>`; this repository
    sets no IsPackable default in Directory.Build.props, so that is MSBuild's actual
    behaviour and not a guess. Reading it from the csproj means adding a package cannot
    forget to add it to the determinism check -- which is how tests/RulesKernel.Testing, a
    published package, ended up covered by no source check at all.
    """
    packaged: dict[str, Path] = {}
    for csproj in all_csproj(root):
        text = csproj.read_text(encoding="utf-8", errors="replace")
        if re.search(r"<IsPackable>\s*false\s*</IsPackable>", text, re.IGNORECASE):
            continue
        packaged[csproj.stem] = csproj
    return packaged


def cs_files_under(directory: Path) -> list[Path]:
    if not directory.is_dir():
        return []
    return [
        p for p in sorted(directory.rglob("*.cs"))
        if not any(part in IGNORED_PARTS for part in p.relative_to(directory).parts)
    ]


# ------------------------------------------------------------------ the C# text lexer


def strip_cs_noise(text: str) -> str:
    """Blank out comment bodies and string-literal contents, preserving offsets.

    Every character that is not executable code becomes a space; newlines are kept, so
    line numbers and columns in the result match the original exactly.

    This replaces a `line.lstrip().startswith("///")` test that made the checks stricter
    on English than on C#. Under that test a plain `//` comment mentioning `DateTime` or
    `new Random()` failed the build -- writing about the rule broke it -- while a `///`
    comment was skipped entirely, so a `///` line was also the one place real code could
    have hidden. Lexing removes both problems at once.

    Interpolation holes are deliberately treated as code: `$"{DateTime.UtcNow}"` is a call,
    not a string, and blanking it would have created exactly the hiding place this function
    exists to remove.

    What this does NOT do: it is a lexer, not a parser. It knows regular, verbatim, raw and
    interpolated string literals, char literals, and both comment forms. It does not know
    preprocessor directives (they are left as code, which is the safe direction), and a
    banned call assembled from string fragments and invoked by reflection is invisible to
    it -- see the module docstring on what a blacklist proves.
    """
    out: list[str] = []
    i = 0
    n = len(text)
    mode = "code"
    interp = False          # the current string literal is interpolated
    verbatim = False        # the current string literal is @-verbatim
    raw_quotes = 0          # delimiter length for a raw string literal, else 0
    brace_depth = 0         # nesting inside the current interpolation hole
    stack: list[tuple[bool, bool, int]] = []  # string contexts suspended by a hole

    def push(ch: str) -> None:
        out.append(ch if ch == "\n" else " ")

    while i < n:
        ch = text[i]

        if mode == "code":
            if ch == "/" and i + 1 < n and text[i + 1] == "/":
                mode = "line"
                push(ch)
                i += 1
                continue
            if ch == "/" and i + 1 < n and text[i + 1] == "*":
                mode = "block"
                push(ch)
                push("*")
                i += 2
                continue
            if ch == "'":
                mode = "char"
                push(ch)
                i += 1
                continue
            if ch == '"':
                # Look back over the @ / $ prefix characters already emitted as code.
                j = i - 1
                prefix = ""
                while j >= 0 and text[j] in "@$":
                    prefix = text[j] + prefix
                    j -= 1
                verbatim = "@" in prefix
                interp = "$" in prefix
                quotes = 0
                while i + quotes < n and text[i + quotes] == '"':
                    quotes += 1
                if quotes >= 3:
                    mode = "raw"
                    raw_quotes = quotes
                    for _ in range(quotes):
                        push('"')
                    i += quotes
                    continue
                mode = "string"
                raw_quotes = 0
                push(ch)
                i += 1
                continue
            if stack:
                # Inside an interpolation hole: track braces so the closing one returns us
                # to the enclosing string rather than being read as code punctuation.
                if ch == "{":
                    brace_depth += 1
                elif ch == "}":
                    brace_depth -= 1
                    if brace_depth == 0:
                        interp, verbatim, raw_quotes = stack.pop()
                        mode = "raw" if raw_quotes else "string"
                        push(ch)
                        i += 1
                        continue
            out.append(ch)
            i += 1
            continue

        if mode == "line":
            if ch == "\n":
                mode = "code"
            push(ch)
            i += 1
            continue

        if mode == "block":
            if ch == "*" and i + 1 < n and text[i + 1] == "/":
                mode = "code"
                push("*")
                push("/")
                i += 2
                continue
            push(ch)
            i += 1
            continue

        if mode == "char":
            if ch == "\\" and i + 1 < n:
                push(ch)
                push(text[i + 1])
                i += 2
                continue
            if ch == "'" or ch == "\n":
                mode = "code"
            push(ch)
            i += 1
            continue

        if mode == "string":
            if interp and ch == "{":
                if i + 1 < n and text[i + 1] == "{":
                    push(ch)
                    push("{")
                    i += 2
                    continue
                stack.append((interp, verbatim, raw_quotes))
                mode = "code"
                brace_depth = 1
                push(ch)
                i += 1
                continue
            if verbatim:
                if ch == '"':
                    if i + 1 < n and text[i + 1] == '"':
                        push(ch)
                        push('"')
                        i += 2
                        continue
                    mode = "code"
                    push(ch)
                    i += 1
                    continue
                push(ch)
                i += 1
                continue
            if ch == "\\" and i + 1 < n:
                push(ch)
                push(text[i + 1])
                i += 2
                continue
            if ch == '"' or ch == "\n":
                mode = "code"
            push(ch)
            i += 1
            continue

        if mode == "raw":
            if interp and ch == "{":
                stack.append((interp, verbatim, raw_quotes))
                mode = "code"
                brace_depth = 1
                push(ch)
                i += 1
                continue
            if ch == '"':
                quotes = 0
                while i + quotes < n and text[i + quotes] == '"':
                    quotes += 1
                if quotes >= raw_quotes:
                    mode = "code"
                    raw_quotes = 0
                for _ in range(quotes):
                    push('"')
                i += quotes
                continue
            push(ch)
            i += 1
            continue

    return "".join(out)


def scan_banned(
    root: Path,
    paths: list[Path],
    rules: list[tuple[str, str, str]],
    result: CheckResult,
) -> None:
    """Apply banned patterns to the executable code of each file.

    Matching runs over strip_cs_noise's output, so prose about a rule never fails the
    build and a `///` comment is not a hiding place. The allow-marker is read from the RAW
    line, because the marker lives in a comment the stripper has already blanked.
    """
    for path in paths:
        result.examined += 1
        rel = path.relative_to(root)
        raw_text = path.read_text(encoding="utf-8")
        code_lines = strip_cs_noise(raw_text).splitlines()
        raw_lines = raw_text.splitlines()
        for lineno, code in enumerate(code_lines, 1):
            raw_line = raw_lines[lineno - 1] if lineno <= len(raw_lines) else ""
            if ALLOW_MARKER in raw_line:
                match = ALLOW_MARKER_WITH_REASON.search(raw_line)
                if match:
                    result.allowances.append(
                        f"{rel}:{lineno}: {match.group('reason').strip()[:70]}"
                    )
                    continue
                result.failures.append(Failure(
                    f"{rel}:{lineno}: '{ALLOW_MARKER}' with no reason after it; write "
                    f"'{ALLOW_MARKER}: <why this line is safe>' "
                    f"(at least {MIN_ALLOW_REASON} characters)"
                ))
                continue
            for _display, pattern, why in rules:
                if re.search(pattern, code):
                    result.failures.append(
                        Failure(f"{rel}:{lineno}: {why}  [{raw_line.strip()[:70]}]")
                    )


# --------------------------------------------------------------------------- checks


def check_text_hygiene(root: Path) -> CheckResult:
    """Bytes-level rules, so that two people reading a file read the same thing."""
    result = CheckResult()
    for path in repo_files(root):
        if not path.is_file():
            continue
        raw = path.read_bytes()
        if not is_probably_text(path, raw):
            continue
        result.examined += 1
        rel = path.relative_to(root)
        if raw.startswith(b"\xef\xbb\xbf"):
            result.failures.append(Failure(f"{rel}: UTF-8 BOM"))
        try:
            text = raw.decode("utf-8")
        except UnicodeDecodeError:
            result.failures.append(Failure(f"{rel}: not valid UTF-8"))
            continue
        if b"\r\n" in raw:
            result.failures.append(Failure(f"{rel}: CRLF line endings (LF required)"))
        # A lone CR is a line ending on classic Mac OS and, more to the point here, a
        # character that makes a terminal overwrite the line it just printed. The previous
        # rule tested for \r\n only, so a file full of lone CRs passed.
        if re.search(rb"\r(?!\n)", raw):
            result.failures.append(Failure(f"{rel}: lone CR line endings (LF required)"))
        # NuGet writes packages.lock.json without a trailing newline and rewrites it on
        # every regeneration, so enforcing the rule here would mean a failure that returns
        # the moment a dependency changes. The exemption is narrow on purpose: encoding,
        # BOM, line endings and invisible characters are still checked on these files,
        # because those would be real problems rather than a generator's house style.
        if path.name != "packages.lock.json":
            if raw and not raw.endswith(b"\n"):
                result.failures.append(Failure(f"{rel}: missing trailing newline"))
            if raw.endswith(b"\n\n"):
                result.failures.append(Failure(f"{rel}: more than one trailing newline"))

        for lineno, line in enumerate(text.splitlines(), 1):
            for char in line:
                if char in BIDI_CONTROLS:
                    result.failures.append(Failure(
                        f"{rel}:{lineno}: bidirectional control U+{ord(char):04X} "
                        f"({BIDI_CONTROLS[char]}); rendered text would differ from "
                        "executed text"
                    ))
                    break
            for char in line:
                # A BOM at offset 0 is already reported above as a BOM; anywhere else
                # U+FEFF is an invisible character inside content.
                if char in ZERO_WIDTH and not (lineno == 1 and char == "\ufeff" and line.startswith("\ufeff")):
                    result.failures.append(Failure(
                        f"{rel}:{lineno}: zero-width character U+{ord(char):04X} "
                        f"({ZERO_WIDTH[char]}); it is invisible in every editor"
                    ))
                    break

        # Identifiers must be ASCII. A Cyrillic small letter IE in place of the 'e' in `Resolve` produces an identifier
        # that renders identically to the ASCII one and is a different symbol to the
        # compiler -- two functions, one apparent name. Comments and string literals are
        # exempt because this repository legitimately needs the section sign in both; the rule is about
        # what the compiler binds, not about what a human reads. Enforced on .cs only,
        # because prose has no identifiers.
        if path.suffix == ".cs":
            for lineno, code in enumerate(strip_cs_noise(text).splitlines(), 1):
                for column, char in enumerate(code, 1):
                    if ord(char) > 127:
                        result.failures.append(Failure(
                            f"{rel}:{lineno}:{column}: non-ASCII U+{ord(char):04X} "
                            f"('{char}') in code; identifiers must be ASCII so two "
                            "distinct symbols cannot render identically"
                        ))
                        break
    return result


# MSBuild accepts single quotes on attributes. The previous pattern required double
# quotes, so a single-quoted ProjectReference from src/RulesKernel to
# tests/RulesKernel.Testing -- the one violation docs/architecture.md says is specifically
# enforced -- went straight through the check that says it enforces it.
PROJECT_REF = re.compile(r"""ProjectReference\s+Include\s*=\s*(?:"([^"]+)"|'([^']+)')""")
PACKAGE_REF = re.compile(r"""PackageReference\s+Include\s*=\s*(?:"([^"]+)"|'([^']+)')""")
ANY_PROJECT_REF = re.compile(r"<\s*ProjectReference\b")


def _includes(pattern: re.Pattern[str], text: str) -> set[str]:
    return {a or b for a, b in pattern.findall(text)}


def check_layering(root: Path) -> CheckResult:
    """The declared project-dependency graph must match the architecture, exactly.

    This reads the csproj files rather than compiled output, which makes it exact: the C#
    compiler omits references a compilation does not actually use, so the reflective
    ArchitectureTests pass vacuously until real code crosses a boundary. This check bites
    the moment a reference is written, including on an empty project.

    Three edges count as a dependency, not one:
      * ProjectReference, in either quoting style;
      * PackageReference naming a package this repository itself publishes -- the floor
        can otherwise depend on the layer above it through nuget.org, which is the same
        cycle with a slower delivery mechanism;
      * a ProjectReference in Directory.Build.props, which MSBuild injects into every
        project in the tree and which no amount of reading csproj text can see. That one
        is reported as a failure rather than resolved, because a check cannot honestly
        claim to read a graph it is structurally blind to.
    """
    result = CheckResult()
    found: dict[str, Path] = {}
    for csproj in all_csproj(root):
        rel = csproj.relative_to(root)
        if csproj.stem in found:
            result.failures.append(Failure(
                f"{rel}: a second project named '{csproj.stem}' "
                f"(the first is {found[csproj.stem].relative_to(root)}); "
                "project names are the identity this check is written in"
            ))
            continue
        found[csproj.stem] = csproj

    for props_name in ("Directory.Build.props", "Directory.Build.targets"):
        for props in sorted(root.rglob(props_name)):
            if any(part in IGNORED_PARTS for part in props.relative_to(root).parts):
                continue
            if ANY_PROJECT_REF.search(props.read_text(encoding="utf-8", errors="replace")):
                result.failures.append(Failure(
                    f"{props.relative_to(root)}: declares a ProjectReference. MSBuild "
                    "injects it into every project beneath this file, where this check "
                    "cannot see it -- the layering graph would no longer be what the "
                    "csproj files say it is. Declare references in the csproj."
                ))

    packaged = set(packaged_projects(root))

    for name, csproj in sorted(found.items()):
        result.examined += 1
        rel = csproj.relative_to(root)
        text = csproj.read_text(encoding="utf-8")
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

        actual = {Path(m).stem for m in _includes(PROJECT_REF, text)}
        # A PackageReference to something this repository publishes is a dependency on
        # that project, delivered through a package feed.
        via_package = {p for p in _includes(PACKAGE_REF, text) if p in packaged and p != name}
        allowed = ALLOWED_PROJECT_REFS[name]

        for forbidden in sorted(actual - allowed):
            result.failures.append(
                Failure(f"{rel}: references '{forbidden}', which the declared graph forbids")
            )
        for forbidden in sorted(via_package):
            if forbidden in allowed:
                result.failures.append(Failure(
                    f"{rel}: depends on '{forbidden}' through a PackageReference. The "
                    "dependency is allowed but the mechanism is not: an in-repository "
                    "dependency must be a ProjectReference, or the build silently tests "
                    "a published version instead of the source next to it."
                ))
            else:
                result.failures.append(Failure(
                    f"{rel}: PackageReference to '{forbidden}', which the declared graph "
                    "forbids; a package feed is not an exemption from layering"
                ))
        if name in SRC_PROJECTS and "RulesKernel.Testing" in (actual | via_package):
            result.failures.append(
                Failure(f"{rel}: a src/ project must never reference the test-support package")
            )

    for declared in ALLOWED_PROJECT_REFS:
        if declared not in found:
            result.failures.append(
                Failure(f"declared project '{declared}' has no csproj on disk")
            )
    return result


def check_solution_membership(root: Path) -> CheckResult:
    """Every csproj on disk is in the solution, and every solution entry exists.

    A project absent from RulesKernel.slnx is never built and never tested, and the gate
    stays green the entire time -- deleting one line from the slnx removes a project from
    CI with no other symptom. CLAUDE.md's "Adding a project" instructions and
    probes/README.md's claim that probes are "kept in the solution and run by the gate"
    both depend on this, and until now nothing verified either.
    """
    result = CheckResult()
    solution = root / SOLUTION_FILE
    on_disk = {p.relative_to(root).as_posix() for p in all_csproj(root)}

    if not solution.is_file():
        result.failures.append(Failure(f"{SOLUTION_FILE}: the solution file does not exist"))
        return result

    try:
        tree = ElementTree.parse(solution)
    except ElementTree.ParseError as error:
        result.failures.append(Failure(f"{SOLUTION_FILE}: not well-formed XML: {error}"))
        return result

    listed: set[str] = set()
    for element in tree.iter():
        if element.tag.rsplit("}", 1)[-1] != "Project":
            continue
        path = element.get("Path") or element.get("path")
        if path:
            listed.add(path.replace("\\", "/").lstrip("./"))

    result.examined = len(on_disk | listed)

    for missing in sorted(on_disk - listed):
        result.failures.append(Failure(
            f"{missing}: on disk but not in {SOLUTION_FILE}; it is never built and never "
            "tested by the gate"
        ))
    for dangling in sorted(listed - on_disk):
        result.failures.append(Failure(
            f"{SOLUTION_FILE}: lists '{dangling}', which is not on disk"
        ))
    return result


def check_core_boundary(root: Path) -> CheckResult:
    """The kernel resolves from its arguments alone, and contains no randomness at all.

    Scope is src/RulesKernel. It applies the rules every packaged assembly is held to AND
    the two that are true only of the floor, which means a kernel line that reaches outside
    its arguments is reported by both this check and check_determinism.

    The duplication is deliberate. docs/architecture.md tells a reader that
    `--only core-boundary` "forbids the kernel itself from touching the filesystem, a
    clock, the environment, the network"; if this check held only the randomness rule,
    that sentence would be false whenever anyone ran the check on its own. A check named
    after a claim has to prove the claim when invoked alone -- otherwise `--only` is a way
    to get a PASS out of a check that never looked.
    """
    result = CheckResult()
    scan_banned(root, cs_files_under(root / "src" / "RulesKernel"),
                CORE_BOUNDARY_BANNED + DETERMINISM_BANNED, result)
    return result


def check_determinism(root: Path) -> CheckResult:
    """No ambient anything in an assembly this repository publishes.

    Scope is every packable project on disk, which today is RulesKernel,
    RulesKernel.Randomness and RulesKernel.Testing. The previous scope was `src/**/*.cs`,
    which silently excluded tests/RulesKernel.Testing -- a published NuGet package, and
    the only packaged assembly that was covered by no source check whatsoever.
    """
    result = CheckResult()
    paths: list[Path] = []
    for csproj in sorted(packaged_projects(root).values()):
        paths.extend(cs_files_under(csproj.parent))
    scan_banned(root, sorted(set(paths)), DETERMINISM_BANNED, result)
    return result


def check_ordering(root: Path) -> CheckResult:
    """An ordered result is ordered by construction; a later sort destroys the evidence.

    Scope is the packaged assemblies, the same as determinism. This is the one rule here
    with a legitimate counter-example -- canonicalising a set before hashing it is a sort
    that is correct -- so it is expected to need `kernel:allow-nondeterminism: <why>`
    occasionally. That marker requires a stated reason and every run reports how many are
    in force, which is the difference between an escape hatch and a hole. There are zero
    sorts in the packaged assemblies today; this check is here to keep it a decision.
    """
    result = CheckResult()
    paths: list[Path] = []
    for csproj in sorted(packaged_projects(root).values()):
        paths.extend(cs_files_under(csproj.parent))
    scan_banned(root, sorted(set(paths)), ORDERING_BANNED, result)
    return result


def _strip_trailing_punctuation(token: str) -> str:
    return token.rstrip(".,;:!?)]}'\"`>")


def check_doc_references(root: Path) -> CheckResult:
    """Every repository path a file points at must exist.

    A predecessor shipped 61 references to deleted documents, several inside runtime error
    messages handed to users. A reference is a promise; this check keeps it.

    What it does NOT prove: that a citation is the RIGHT document, only that it resolves.
    `docs/architecture.md` cited where `docs/decisions/0001` was meant passes here.
    """
    result = CheckResult()
    decisions = root / "docs" / "decisions"
    known_decisions = (
        {p.name[:4] for p in decisions.glob("*.md") if p.name[:4].isdigit()}
        if decisions.is_dir() else set()
    )

    for path in repo_files(root):
        if not path.is_file():
            continue
        raw = path.read_bytes()
        if not is_probably_text(path, raw):
            continue
        try:
            text = raw.decode("utf-8")
        except UnicodeDecodeError:
            continue
        rel = path.relative_to(root)
        posix = rel.as_posix()
        if any(posix == e or posix.startswith(e) for e in DOC_REFERENCE_EXEMPT):
            continue
        result.examined += 1

        # A URL's path is shaped exactly like a repository path. Strip URLs first, or a
        # link to pcg-c-basic's src/pcg_basic.c fails the build over its `src/` segment.
        text = URL.sub(" ", text)

        seen: set[str] = set()
        for match in REFERENCE_TOKEN.finditer(text):
            token = _strip_trailing_punctuation(match.group(1))
            if not token or token in seen:
                continue
            seen.add(token)
            # A glob or a brace expansion names a set, not a path. It cannot be resolved
            # and reporting it would punish writing accurate documentation about globs.
            if GLOB_CHARS & set(token):
                continue
            # `docs/decisions/0002` is decision shorthand, handled below against the set
            # of decisions that exist rather than as a literal path.
            if re.fullmatch(r"docs/decisions/\d{4}", token):
                continue
            if token.startswith("./") or token.startswith("../"):
                # A relative reference has two plausible bases and this check cannot tell
                # which was meant: a markdown link is relative to the DOCUMENT, while
                # `./scripts/validate.sh` in a workflow or a shell script is relative to
                # the working directory, which for this repository is the root. Resolving
                # only against the root -- the previous behaviour -- checked
                # `../docs/foo.md` written inside docs/decisions/ as a path it never
                # meant. Accepting either base is weaker than picking the right one, but
                # it still catches the case that matters: a name that exists under neither
                # base is dangling however you read it.
                candidates = [(path.parent / token).resolve(), (root / token).resolve()]
                inside = []
                for candidate in candidates:
                    try:
                        candidate.relative_to(root)
                    except ValueError:
                        continue
                    inside.append(candidate)
                if not inside:
                    result.failures.append(Failure(
                        f"{rel}: references '{token}', which escapes the repository"
                    ))
                    continue
                if not any(candidate.exists() for candidate in inside):
                    result.failures.append(
                        Failure(f"{rel}: references '{token}', which does not exist "
                                "relative to this file or to the repository root")
                    )
                continue
            if not (root / token).exists():
                result.failures.append(
                    Failure(f"{rel}: references '{token}', which does not exist")
                )

        for first, second in DOC_DECISION_SHORTHAND.findall(text):
            number = first or second
            if number not in known_decisions:
                result.failures.append(
                    Failure(f"{rel}: references decision {number}, which does not exist")
                )
    return result


def _check_yaml(path: Path, rel: Path, result: CheckResult) -> None:
    try:
        import yaml  # noqa: PLC0415  -- optional, and the failure path below is the point
    except ImportError:
        result.failures.append(Failure(
            f"{rel}: YAML well-formedness was NOT verified -- PyYAML is not importable. "
            "A malformed workflow file is not an error on GitHub: the workflow silently "
            "does not run, and with no branch protection CI simply stops existing. That "
            "is exactly the failure this check is for, so an unverifiable YAML file is a "
            "failure rather than a pass. Install it: python3 -m pip install pyyaml"
        ))
        return
    try:
        list(yaml.safe_load_all(path.read_bytes()))
    except yaml.YAMLError as error:
        result.failures.append(Failure(f"{rel}: not well-formed YAML: {error}"))


def check_parseable(root: Path) -> CheckResult:
    """Every structured file in the repository must actually parse.

    XML: an XML comment may not contain a double hyphen. Writing a flag such as the restore
    regeneration switch inside a comment in Directory.Build.props therefore makes the file
    malformed -- and MSBuild does not report that as a parse error. It reports a downstream
    symptom instead: every project loses the properties that file was supposed to set, and
    the build fails with an empty TargetFramework, several layers away from the cause.

    YAML: a malformed workflow file is worse, because it is not an error anywhere. GitHub
    declines to run the workflow and says so only on a page nobody opens. With no branch
    protection on this repository, CI would simply stop existing and every subsequent pull
    request would look unblocked.

    JSON: global.json and the packages.lock.json files are read by the SDK and by
    scripts/validate.sh, which parses global.json with python3 before anything else runs.

    What this does NOT prove: that the content is correct. A well-formed workflow that
    runs the wrong command, and a well-formed csproj that declares the wrong framework,
    both pass.
    """
    result = CheckResult()
    for path in repo_files(root):
        if not path.is_file():
            continue
        suffix = path.suffix.lower()
        rel = path.relative_to(root)
        if suffix in {".csproj", ".props", ".targets", ".slnx", ".nuspec", ".xml", ".config"}:
            result.examined += 1
            parser = xml.parsers.expat.ParserCreate()
            try:
                parser.Parse(path.read_bytes(), True)
            except xml.parsers.expat.ExpatError as error:
                result.failures.append(Failure(f"{rel}: not well-formed XML: {error}"))
        elif suffix in {".yml", ".yaml"}:
            result.examined += 1
            _check_yaml(path, rel, result)
        elif suffix == ".json":
            result.examined += 1
            try:
                json.loads(path.read_bytes().decode("utf-8"))
            except (json.JSONDecodeError, UnicodeDecodeError) as error:
                result.failures.append(Failure(f"{rel}: not well-formed JSON: {error}"))
    return result


def check_action_pins(root: Path) -> CheckResult:
    """Every GitHub Action must be pinned to a 40-hex commit SHA.

    A tag is a pointer its owner can move. `actions/checkout@v7` means "whatever that
    owner decides v7 is on the morning the job runs", and the publish workflow hands what
    runs an OIDC token that nuget.org will accept for this repository. All five `uses:` in
    this repository are already SHA-pinned. Nothing kept them that way: GitHub's own
    sha_pinning_required setting is off, so the only thing standing between this repository
    and a mutable third-party dependency was that nobody had typed a tag yet.

    Local actions (`./.github/actions/...`) are exempt: they are this repository's own
    files, already covered by everything else here.

    This is read as text rather than through the YAML tree on purpose -- the check must
    report the line number, and it must still work when PyYAML is absent. check_parseable
    is what proves the file is valid YAML.
    """
    result = CheckResult()
    workflows = root / ".github" / "workflows"
    if not workflows.is_dir():
        return result
    for path in sorted(workflows.iterdir()):
        if path.suffix.lower() not in {".yml", ".yaml"} or not path.is_file():
            continue
        rel = path.relative_to(root)
        for lineno, line in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
            match = USES_LINE.match(line)
            if not match:
                continue
            value = match.group("value").strip().strip("'\"")
            result.examined += 1
            if value.startswith("./") or value.startswith("docker://"):
                continue
            if not ACTION_PIN.fullmatch(value):
                result.failures.append(Failure(
                    f"{rel}:{lineno}: '{value}' is not pinned to a 40-hex commit SHA; "
                    "a tag is a pointer its owner can move under this repository"
                ))
    return result



def declared_target_frameworks(csproj: Path, root: Path) -> list[str]:
    """The frameworks a csproj actually builds for, honouring an emptied default.

    A project clears the repo-wide `<TargetFramework>` with an empty element and declares
    `<TargetFrameworks>` instead, so the singular form is only meaningful when it has a
    value. A project that declares neither inherits the Directory.Build.props default.
    """
    text = csproj.read_text(encoding="utf-8", errors="replace")

    plural = re.search(r"<TargetFrameworks>([^<]*)</TargetFrameworks>", text)
    if plural:
        frameworks = [part.strip() for part in plural.group(1).split(";") if part.strip()]
        if frameworks:
            return frameworks

    singular = re.search(r"<TargetFramework>([^<]*)</TargetFramework>", text)
    if singular and singular.group(1).strip():
        return [singular.group(1).strip()]

    props = root / "Directory.Build.props"
    if props.is_file():
        inherited = re.search(
            r"<TargetFramework>([^<]*)</TargetFramework>",
            props.read_text(encoding="utf-8", errors="replace"))
        if inherited and inherited.group(1).strip():
            return [inherited.group(1).strip()]

    return []


# A lock file does not spell frameworks the way a csproj does. NuGet writes the short TFM
# for .NETCoreApp ("net8.0") but the long form for others (".NETStandard,Version=v2.0"), so
# comparing the two sets verbatim reports a difference that does not exist.
LOCK_FRAMEWORK_FORMS = {
    ".NETStandard": "netstandard",
    ".NETCoreApp": "net",
    ".NETFramework": "net",
}


def normalize_framework(value: str) -> str:
    """One spelling for a target framework, whichever form it arrived in."""
    match = re.fullmatch(r"(\.NET[A-Za-z]+),Version=v([0-9.]+)", value.strip())
    if not match:
        return value.strip()

    prefix = LOCK_FRAMEWORK_FORMS.get(match.group(1))
    if prefix is None:
        return value.strip()

    version = match.group(2)
    # .NETFramework is the one family whose short form drops the dots: v4.8 is net48.
    if match.group(1) == ".NETFramework":
        version = version.replace(".", "")
    return prefix + version


def check_target_frameworks(root: Path) -> CheckResult:
    """Every packaged project still targets what this repository committed to.

    Two things are asserted per package, because either alone is satisfiable while the
    commitment is broken: the csproj declares exactly the expected frameworks, and its
    committed lock file covers exactly the same set. A lock file that has quietly lost a
    framework restores clean the moment the csproj loses it too.

    An undeclared packaged project fails rather than passing unchecked, the same forcing
    function ALLOWED_PROJECT_REFS applies to layering: adding a package must be a decision
    about what it targets.
    """
    result = CheckResult()
    for name, csproj in sorted(packaged_projects(root).items()):
        rel = csproj.relative_to(root)
        result.examined += 1

        expected = EXPECTED_TARGET_FRAMEWORKS.get(name)
        if expected is None:
            result.failures.append(Failure(
                f"{rel}: packaged project '{name}' is not declared in "
                "EXPECTED_TARGET_FRAMEWORKS; what a package targets is a commitment to its "
                "consumers, not a default"
            ))
            continue

        declared = declared_target_frameworks(csproj, root)
        if set(declared) != expected:
            result.failures.append(Failure(
                f"{rel}: targets {sorted(declared) or ['nothing']}, expected "
                f"{sorted(expected)}; dropping one breaks every consumer pinned to it and "
                "needs a decision superseding ADR 0008"
            ))

        lock = csproj.parent / "packages.lock.json"
        if not lock.is_file():
            result.failures.append(Failure(
                f"{rel}: no packages.lock.json; RestorePackagesWithLockFile is on, so a "
                "missing lock file means restore is free to resolve differently"
            ))
            continue

        try:
            locked = {
                normalize_framework(key)
                for key in json.loads(lock.read_text(encoding="utf-8")).get("dependencies", {})
            }
        except (ValueError, OSError) as error:
            # check_parseable owns malformed JSON; this only needs to not crash before it
            # reports.
            result.failures.append(Failure(f"{lock.relative_to(root)}: unreadable ({error})"))
            continue

        if locked != expected:
            result.failures.append(Failure(
                f"{lock.relative_to(root)}: locks {sorted(locked) or ['nothing']}, expected "
                f"{sorted(expected)}; regenerate with "
                "`dotnet restore RulesKernel.slnx --force-evaluate`"
            ))
    return result


# ------------------------------------------------------------------ documentation samples
#
# README.md shipped in 0.2.0 and 0.3.0 with SourceBaselineId examples that did not compile:
# the constructor gained a required hashDerivation in 0.2.0 (docs/decisions/0007) and the
# prose kept the old call. Nothing compiled the prose. Now a C# block in documentation is
# preceded by `<!-- sample: NAME -->` and is a verbatim copy of the region
# `// sample: NAME` ... `// end sample` in a compiled project, which the gate builds and runs.
#
# Decision records are exempt. They record what was decided at the time, including the
# shape of an API that has since changed, and rewriting them to track the code would destroy
# the record.
SAMPLE_DOC_EXEMPT = ("docs/decisions/", "tools/tests/")
SAMPLE_MARKER = re.compile(r"^<!--\s*sample:\s*([A-Za-z0-9_.-]+)\s*-->\s*$")
CSHARP_FENCE = re.compile(r"^```\s*(?:csharp|cs|c#)\s*$", re.IGNORECASE)
REGION_START = re.compile(r"^\s*//\s*sample:\s*([A-Za-z0-9_.-]+)\s*$")
REGION_END = re.compile(r"^\s*//\s*end sample\s*$")


def _dedent(lines: list[str]) -> list[str]:
    lines = [line.rstrip() for line in lines]
    while lines and not lines[0]:
        lines.pop(0)
    while lines and not lines[-1]:
        lines.pop()
    indents = [len(line) - len(line.lstrip(" ")) for line in lines if line]
    cut = min(indents) if indents else 0
    return [line[cut:] for line in lines]


def _sample_regions(root: Path, result: CheckResult) -> dict[str, tuple[Path, list[str]]]:
    project_dirs = [csproj.parent for csproj in all_csproj(root)]
    regions: dict[str, tuple[Path, list[str]]] = {}
    for path in repo_files(root):
        if path.suffix != ".cs":
            continue
        rel = path.relative_to(root)
        if any(rel.as_posix().startswith(e) for e in SAMPLE_DOC_EXEMPT):
            continue
        lines = path.read_text(encoding="utf-8", errors="replace").splitlines()
        index = 0
        while index < len(lines):
            start = REGION_START.match(lines[index])
            if not start:
                index += 1
                continue
            name = start.group(1)
            end = next((j for j in range(index + 1, len(lines)) if REGION_END.match(lines[j])), None)
            if end is None:
                result.failures.append(Failure(f"{rel}:{index + 1}: sample '{name}' has no '// end sample'"))
                break
            if name in regions:
                result.failures.append(Failure(
                    f"{rel}:{index + 1}: sample '{name}' is also defined in {regions[name][0]}"))
            elif not any(path.is_relative_to(d) for d in project_dirs):
                # A region in a file no project compiles proves nothing about compiling.
                result.failures.append(Failure(
                    f"{rel}:{index + 1}: sample '{name}' is not inside any project, so nothing compiles it"))
            else:
                regions[name] = (rel, _dedent(lines[index + 1:end]))
            index = end + 1
    return regions


def check_doc_samples(root: Path) -> CheckResult:
    """Every C# block in living documentation is a compiled sample, character for character.

    What it does NOT prove: that the sample's test asserts what the prose around it claims.
    It proves the code shown is code that compiles and runs; the assertions beside each
    region are the reviewer's to keep honest.
    """
    result = CheckResult()
    regions = _sample_regions(root, result)
    used: set[str] = set()

    for path in repo_files(root):
        if path.suffix.lower() != ".md":
            continue
        rel = path.relative_to(root)
        if any(rel.as_posix().startswith(e) for e in SAMPLE_DOC_EXEMPT):
            continue
        lines = path.read_text(encoding="utf-8", errors="replace").splitlines()
        index = 0
        while index < len(lines):
            if not CSHARP_FENCE.match(lines[index]):
                index += 1
                continue
            result.examined += 1
            end = next((j for j in range(index + 1, len(lines)) if lines[j].startswith("```")), len(lines))
            body = _dedent(lines[index + 1:end])
            marker = next((lines[j] for j in range(index - 1, -1, -1) if lines[j].strip()), "")
            named = SAMPLE_MARKER.match(marker)
            if not named:
                result.failures.append(Failure(
                    f"{rel}:{index + 1}: a C# block with no '<!-- sample: NAME -->' above it; "
                    "nothing compiles it"))
            elif named.group(1) not in regions:
                result.failures.append(Failure(
                    f"{rel}:{index + 1}: sample '{named.group(1)}' has no '// sample: "
                    f"{named.group(1)}' region in any compiled project"))
            else:
                name = named.group(1)
                used.add(name)
                source, expected = regions[name]
                if body != expected:
                    line = next(
                        (k for k in range(max(len(body), len(expected)))
                         if k >= len(body) or k >= len(expected) or body[k] != expected[k]),
                        0)
                    shown = body[line] if line < len(body) else "(block ends)"
                    compiled = expected[line] if line < len(expected) else "(region ends)"
                    result.failures.append(Failure(
                        f"{rel}:{index + 2 + line}: sample '{name}' differs from {source}: "
                        f"documented {shown!r}, compiled {compiled!r}"))
            index = end + 1

    for name, (source, _) in sorted(regions.items()):
        if name not in used:
            result.failures.append(Failure(
                f"{source}: sample '{name}' is shown in no document; delete it or show it"))
    return result


# ------------------------------------------------------------------ development version
#
# A comment in Directory.Build.props said `produces 0.3.0-dev` for the whole 0.4.0 cycle.
# A development version named in living prose goes stale at every version bump, silently,
# and a reader believes it. Prose that needs one names the current one, or says `-dev` in
# general. Decision records are exempt for the reason doc-samples gives.
DEV_VERSION = re.compile(r"(?<![\w.])(\d+\.\d+\.\d+)-dev\b")
DEV_VERSION_EXEMPT = ("docs/decisions/", "tools/tests/", "tools/repo-checks.py")


def current_version(root: Path) -> str | None:
    props = root / "Directory.Build.props"
    if not props.is_file():
        return None
    text = props.read_text(encoding="utf-8", errors="replace")
    prefix = re.search(r"<VersionPrefix>\s*([^<\s]+)\s*</VersionPrefix>", text)
    suffix = re.search(r"<VersionSuffix>\s*([^<\s]*)\s*</VersionSuffix>", text)
    if not prefix:
        return None
    return prefix.group(1) + (f"-{suffix.group(1)}" if suffix and suffix.group(1) else "")


def check_dev_version(root: Path) -> CheckResult:
    """Living documentation names no development version other than the tree's own."""
    result = CheckResult()
    version = current_version(root)
    if version is None:
        result.failures.append(Failure("Directory.Build.props declares no VersionPrefix"))
        return result
    for path in repo_files(root):
        raw = path.read_bytes()
        if not is_probably_text(path, raw):
            continue
        rel = path.relative_to(root)
        if any(rel.as_posix().startswith(e) for e in DEV_VERSION_EXEMPT):
            continue
        result.examined += 1
        text = raw.decode("utf-8", errors="replace")
        for match in DEV_VERSION.finditer(text):
            if match.group(0) != version:
                line = text.count("\n", 0, match.start()) + 1
                result.failures.append(Failure(
                    f"{rel}:{line}: names '{match.group(0)}', but this tree is {version}"))
    return result


CHECKS = {
    "text-hygiene": check_text_hygiene,
    "parseable": check_parseable,
    "layering": check_layering,
    "solution-membership": check_solution_membership,
    "core-boundary": check_core_boundary,
    "determinism": check_determinism,
    "ordering": check_ordering,
    "doc-references": check_doc_references,
    "action-pins": check_action_pins,
    "target-frameworks": check_target_frameworks,
    "doc-samples": check_doc_samples,
    "dev-version": check_dev_version,
}


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="repo-checks.py", description=__doc__.splitlines()[0])
    parser.add_argument("--root", default=str(ROOT))
    parser.add_argument("--only", action="append", choices=sorted(CHECKS), default=None)
    parser.add_argument("--json", action="store_true")
    parser.add_argument(
        "--allow-skip", action="store_true",
        help="do not fail when a check examined nothing. For running this file against a "
             "synthetic tree; never for the gate.",
    )
    args = parser.parse_args(argv)

    root = Path(args.root).resolve()
    names = args.only or sorted(CHECKS)
    results = {name: CHECKS[name](root) for name in names}

    failures = sum(len(r.failures) for r in results.values())
    skipped = [name for name, r in results.items() if r.examined == 0]
    allowances = sum(len(r.allowances) for r in results.values())
    # A skip is a failure. Every check here has scope in this repository, so a check that
    # examined nothing has lost its inputs -- a renamed directory, a broken glob, a git
    # invocation that returned nothing -- and the run proved less than it says it did.
    skip_is_failure = bool(skipped) and not args.allow_skip

    if args.json:
        print(json.dumps({
            "failures": failures,
            "skipped": skipped,
            "skip_fails_run": skip_is_failure,
            "allowances": allowances,
            "exit": 1 if (failures or skip_is_failure) else 0,
            "checks": {
                name: {
                    "failures": [str(f) for f in r.failures],
                    "examined": r.examined,
                    "allowances": r.allowances,
                }
                for name, r in results.items()
            },
        }, indent=2))
    else:
        for name in names:
            r = results[name]
            suffix = f", {len(r.allowances)} allowed" if r.allowances else ""
            if r.failures:
                print(f"FAIL  {name}  ({len(r.failures)})")
                for problem in r.failures:
                    print(f"        {problem}")
            elif r.examined == 0:
                print(f"skip  {name}  (nothing in scope -- this check proved nothing)")
            else:
                print(f"ok    {name}  ({r.examined} examined{suffix})")
        print()
        if allowances:
            print(f"repo-checks: {allowances} line(s) suppressed by '{ALLOW_MARKER}':")
            for name in names:
                for allowance in results[name].allowances:
                    print(f"        {allowance}")
        if skipped:
            verdict = "FAIL" if skip_is_failure else "allowed by --allow-skip"
            print(f"repo-checks: {len(skipped)} check(s) examined nothing "
                  f"({', '.join(skipped)}) -- {verdict}")
        if failures:
            print(f"repo-checks: {failures} failure(s)")
        elif skip_is_failure:
            print("repo-checks: FAIL (a check that proved nothing cannot report PASS)")
        else:
            print("repo-checks: PASS")

    return 1 if (failures or skip_is_failure) else 0


if __name__ == "__main__":
    raise SystemExit(main())
