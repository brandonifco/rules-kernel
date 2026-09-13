# CLAUDE.md

Operating contract for this repository. Read [README.md](README.md) for what the kernel is
and [docs/architecture.md](docs/architecture.md) for how it is put together.

## What this repository is

`RulesKernel` is a versioned library that deterministic rules engines **reference**. It is
not a template and not a starter. Anything that makes it easier to copy than to reference
is working against its purpose.

## Governing principles

**1. The kernel is ruleset-agnostic.** No dice, no page numbers, no combat, no tax, no
subject-matter vocabulary. If a name only makes sense for one kind of rules, it belongs in a
domain pack, not here. `UniformInt`, not `D6`.

**2. Enforcement over prose.** A rule worth stating is worth a check. `tools/repo-checks.py`
is where rules become facts. If you find yourself writing "we always…" in a comment, ask
what would fail the build.

**3. A check that proves nothing says so.** A check whose inputs do not exist reports
`skip`, never `ok`. A gate that reports PASS while proving less than it claims is worse than
no gate, because it is trusted.

**4. A citation is a promise.** Every reference to a repository document must resolve;
`--only doc-references` enforces it. Never cite a document you have not written.

**5. Compatibility changes are decisions.** Changing a kernel type's shape, the reference
vectors, or the meaning of an identity component breaks every engine downstream. It needs a
record in `docs/decisions/`, not a commit message.

## Before you change anything

Run the gate. There is one:

```bash
./scripts/validate.sh full
```

## Working rules

- **Smallest coherent change.** One concern per change. If it needs "and", it is two.
- **Tests are the evidence.** A behavioural claim without a test is a claim. Draw counts,
  ordering, and equality semantics are part of the contract and are asserted directly.
- **Ordered by construction.** Never sort an ordered result afterwards; the order *is* the
  evidence.
- **Public API carries XML docs.** Packaged assemblies build with documentation required.
  Say why, not what — the code already says what.
- **Struct defaults are a real case.** A `readonly record struct` can always be produced by
  `default(T)`, bypassing its constructor. Either validate at every gate that consumes it,
  or expose `IsValid` and reject it where it matters. Both patterns are in use here.

## Adding a project

Create it, then declare it in `ALLOWED_PROJECT_REFS` and `PROJECT_DIRS` in
`tools/repo-checks.py`, and add it to `RulesKernel.slnx`. An undeclared project fails the
layering check rather than being silently unenforced. That is deliberate.

## What does not belong here

Corpus extraction and adapters, domain vocabulary, mechanics, serialization, persistence,
replay execution, and anything that reads a file. Those are other repositories' jobs.
