# Probes

Small, deliberately non-tabletop consumers of the kernel, kept in the solution and run by
the gate.

They exist because the kernel's two real engines are both tabletop games with one undated
corpus and dice. That means the three properties the kernel was generalized *for* —
several corpora at once, a corpus pinned to a date, and an engine that consumes no
randomness — have no coverage from real work. A probe is the cheapest way to keep those
paths honest.

A probe is not a product. It implements no rule anyone should rely on, and its data is
fabricated. What it asserts is *shape*: that the kernel's types are usable for something
that is not a game, and that they stay usable.

| Probe | Exercises |
|---|---|
| `RegulatoryProbe.Tests` | multiple corpus baselines, the `AsOf` temporal axis, the no-randomness path, all five unresolved reasons, `§`-style citations |
| `Part107Probe.Tests` | real regulation text pinned by the same hash as a second engine, rule text in force by date, definitions split across corpora, an interpretation the engine records and pins, waivers whose validity turns on two dates. Its [FINDINGS.md](Part107Probe.Tests/FINDINGS.md) records where the kernel was uncomfortable |
