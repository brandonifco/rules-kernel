# 0008 — Multi-targeting, so adoption is not a framework upgrade

## Status

Accepted — 2026-09-13. The packaged assemblies target `net8.0;net10.0`.

## Context

0.1.0 shipped `lib/net10.0` only, because the repository is developed against a pinned
.NET 10 SDK and nothing pushed back.

That made the kernel unusable by one of the two engines it was extracted from. SRD_Combat
targets `net8.0` with `global.json` pinning SDK `8.0.129` and `rollForward: disable`. It
could not reference the kernel at all — not `RulesKernel.Randomness`, not even the
dependency-free `RulesKernel`.

This was raised earlier as a low-priority question about broad reuse and dismissed on the
grounds that the kernel's audience is this author's own engines, all on a pinned SDK. That
reasoning was wrong in its own terms: one of those engines was locked out, and moving it to
net10.0 is a separate project of days-to-weeks that has nothing to do with adopting a
library.

## Decision

`RulesKernel`, `RulesKernel.Randomness` and `RulesKernel.Testing` target `net8.0;net10.0`.

Nothing in them needed `net10.0`: the surface is `ArgumentException.ThrowIfNullOrWhiteSpace`,
`ArgumentOutOfRangeException.ThrowIfZero`/`ThrowIfNegative`, `ImmutableArray`, `DateOnly`
and `HashCode`, all available on `net8.0`. The randomness package is integer arithmetic.

Verified by building a real `net8.0` console project against the packed 0.2.0 packages and
running it, rather than by inspecting the nupkg layout.

Test projects and probes stay `net10.0`. They are not shipped, and pinning them to the
repository's own SDK keeps the gate testing one configuration rather than two.

## Consequences

A kernel whose purpose is being referenced by engines that outlive it cannot make the
framework the price of entry. Dropping a target framework later is itself a breaking change,
so `net8.0` is now a commitment until a decision supersedes this one.

Two target frameworks means the analyzers, including the public API baseline, run twice.
That has already proved useful rather than costly: a breaking change to `SourceBaselineId`
was reported once per framework.
