; Shipped analyzer releases
; A diagnostic ID is a permanent contract: a consumer suppresses by ID in .editorconfig,
; so reusing or renumbering one silently changes what their suppression means. Everything
; below has been published and can never be reused for anything else.
; See docs/decisions/0011 and docs/decisions/0013.

## Release 0.3.0

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
RK0001 | Determinism | Warning | Ambient or unpinned entropy is not replayable
RK0002 | Determinism | Warning | Ambient clock is not replayable
RK0003 | Determinism | Warning | Ambient environment makes a result machine-dependent
RK0004 | Determinism | Warning | Concurrency makes resolution order non-deterministic
RK0005 | Determinism | Warning | A runtime hash code is not replay-stable
RK0006 | Determinism | Warning | Ambient culture and time zone make a result machine-dependent
RK0007 | Determinism | Warning | An unordered collection is materialized into an ordered result
