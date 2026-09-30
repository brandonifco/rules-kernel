# 0023 — Resolution is closed at construction

## Status

Accepted — 2026-09-30. Found by the 1.0 public API freeze review (#74). Makes a documented
guarantee true; no public API change.

## Context

`Resolution<T>` documents itself as a closed hierarchy: its constructor is private, so
`Resolved` and `Unresolved` are "the only cases that can ever exist", and `Match` and a
`switch` over it are exhaustive by construction. That guarantee is what
[0018](0018-resolution-enforces-handling-not-honesty.md)'s enforced half rests on. No path to a
value skips the unresolved case only if there is no third case.

It was not true. A record that is not sealed gets a `protected` copy constructor from the
compiler, and a derived record can chain to it:

```csharp
record Impostor : Resolution<int>
{
    public Impostor(Resolution<int> original) : base(original) { }
}
```

This compiles against the published package. The instance is neither `Resolved` nor
`Unresolved`. `IsResolved` is false, a type test for `Unresolved` fails, and `Match` throws its
"unreachable" exception. The freeze review compiled exactly this.

## Decision

The copy constructor is declared explicitly, `protected` as the language requires of a record
that is not sealed, and it throws `InvalidOperationException` unless the instance being
constructed is `Resolved` or `Unresolved`. Every constructor of a derived type must chain to a
base constructor, and the only other one is private. So a third case can be declared, but no
instance of one can exist.

The public surface is unchanged. The baseline already lists this constructor, because the
compiler generated it. `with` on either case still works, because both pass the check.

## Alternatives considered

**An abstract class instead of a record**, with sealed nested classes. The compiler would then
reject a third case outright, which is stronger. It would also remove the record members that
0.3.0 shipped (`with`, `EqualityContract`, `PrintMembers`, the generated equality), and
re-implement value equality by hand, in the one type every engine returns. A shape change of
that size is the wrong trade for a hole that no consumer has used.

**Documenting the hole.** The guarantee is load-bearing for 0018, and a documented exception to
"exhaustive by construction" would put a default arm back into every consumer's reasoning.

## Consequences

Declaring a third case still compiles. Constructing one throws, the first time it is attempted.
No consumer derives from `Resolution<T>`: faa-part-107, hoyle-backgammon, srd-52-combat,
reykholt, hallertau and rules-factory were checked. A test constructs an impostor and asserts
the throw, and another asserts that `with` on both real cases still copies.

Reflection that creates an object without running a constructor still defeats this, as it
defeats every constructor check in the kernel
([0009](0009-what-the-source-blacklists-do-not-prove.md)).
