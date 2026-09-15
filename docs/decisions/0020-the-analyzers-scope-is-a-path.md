# 0020 — The analyzer's scope is a path

## Status

Accepted — 2026-09-15. No code change to `RulesKernel.Analyzers`. Decides how scope will be
expressed before any rule that needs it is written.

## Context

RK0001–RK0005 and RK0007 apply to every file of every project that references the package.
Calibration ([0015](0015-calibrating-the-rule-set-before-it-freezes.md)) showed what that
costs. Several true findings were about code nobody claims is deterministic: a `Guid.NewGuid`
for a temporary file in a test, and `Environment.NewLine` in console output. The intended
direction runs the other way. Some future rules should be far stricter than the current set
(no floating point in a resolution path, say, or no unbounded iteration) and should hold only
over the deterministic engine core, never over console, UI or test infrastructure.

Before adding rules like that, the question was what "scope" can be, on the compilers the
analyzer must load under.

## What was measured

A throwaway analyzer built against `Microsoft.CodeAnalysis.CSharp` 4.8.0 reported four
diagnostics on every type. A consumer arranged `Engine/Rules/`, `Tests/` and `Console/`
directories under `.editorconfig` sections. Both SDK 8.0.130 (Roslyn 4.8.0, the floor) and
SDK 10.0.111 gave identical results:

| Mechanism | Result |
|---|---|
| `dotnet_diagnostic.ID.severity = none` in a `[Tests/**.cs]` section | silences the rule for those files only |
| the same key in a nested, non-root `.editorconfig` | applies to its directory |
| a rule with `isEnabledByDefault: false`, enabled by `dotnet_diagnostic.ID.severity = warning` in `[Engine/Rules/**.cs]` | reported in that directory only |
| the same rule, with only `dotnet_analyzer_diagnostic.category-X.severity = warning` | **not reported**: category configuration does not enable a disabled rule |
| a custom key (`rules_kernel.scope = core`) read through `AnalyzerConfigOptionsProvider.GetOptions(tree)` | visible per file, including from a nested `.editorconfig` |
| `build_property.IsTestProject`, after `<CompilerVisibleProperty Include="IsTestProject" />` | visible as a global option |

The last row corrects README, which said the analyzer "cannot know which projects are tests".
It can, if the package declares the property visible. This decision is not to use it.

## Decision

**Scope is a file path, declared in `.editorconfig`.** Not a namespace, and not a project.

- A **namespace** does not bound code. Any file can declare any namespace, nothing native
  configures analyzers by namespace, and a scope an author can leave by editing one line is not
  a boundary.
- A **project** is too coarse. An engine's console runner and its rules can share a project,
  and `IsTestProject` misses exactly the infrastructure that is not a test.
- A **path** is what `.editorconfig` already scopes. The IDE, `dotnet format` and the build all
  honour it the same way, and every consumer already has the file.

**The current rules stay global**, and a consumer narrows them by path with standard severity
configuration, as README shows. Nothing changes for them.

**A future strict tier is opted into by path with one key**, `rules_kernel.scope = core`. Its
rules are enabled-by-default descriptors that the analyzer reports only in files where that key
is set. The measurements rule out the alternative of shipping them disabled by default and
having consumers enable them. That needs one severity line per rule per path, category
configuration cannot enable them, and a strict rule added in a later release would then do
nothing for a consumer who had opted into "strict". With the key, opting a path in means every
current and future rule in the tier.

That last property is also the cost. A release that adds a rule to the tier can fail the build
of every consumer who opted in, so a tier rule is calibrated against real consumers before
release, like any other change to what the analyzer reports.

**Nothing is implemented until the first tier rule exists.** Reading a key that no rule uses
would be dead code with a public name. The key name and its one value are reserved here.

## Alternatives considered

**`rules_kernel.scope = infrastructure` to silence the current rules en bloc.** Rejected for
now. It duplicates severity configuration that already works, and a rule suppressed by a
custom key looks enabled to every standard tool that reads the file.

**Detect tests through `IsTestProject`.** Rejected, for the reason in the Decision.

## Consequences

README's adoption guidance no longer says the analyzer cannot tell tests apart. It says the
consumer declares scope by path. `tools/analyzer-probe/check.sh` builds a consumer with the
README's test-project `.editorconfig` block applied, taken from README itself, and requires it
to silence exactly the files it covers.
