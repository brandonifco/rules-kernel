; Unshipped analyzer release
; A diagnostic ID is a permanent contract: a consumer suppresses by ID in .editorconfig,
; so reusing or renumbering one silently changes what their suppression means.
; See docs/decisions/0011.

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
RK0001 | Determinism | Warning | Ambient entropy is not replayable
RK0002 | Determinism | Warning | Ambient clock is not replayable
RK0003 | Determinism | Warning | Ambient environment makes a result machine-dependent
RK0004 | Determinism | Warning | Concurrency makes resolution order non-deterministic
RK0005 | Determinism | Warning | A runtime hash code is not replay-stable
RK0006 | Determinism | Warning | Ambient culture and time zone make a result machine-dependent
