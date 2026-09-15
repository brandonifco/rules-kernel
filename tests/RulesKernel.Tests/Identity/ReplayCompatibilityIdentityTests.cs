using System;
using System.Collections.Generic;
using RulesKernel.Identity;

namespace RulesKernel.Tests.Identity;

public sealed class ReplayCompatibilityIdentityTests
{
    private const string HashA = "1111111111111111111111111111111111111111111111111111111111111111";
    private const string HashB = "2222222222222222222222222222222222222222222222222222222222222222";

    private static readonly RulesetVersion Ruleset = new("example", 1);
    private static readonly ReplaySchemaVersion Schema = new(1);
    private static readonly SourceBaselineId CoreBook = new("core", HashA, "pdf-bytes");
    private static readonly SourceBaselineId Supplement = new("supplement", HashB, "pdf-bytes");

    private static ReplayCompatibilityIdentity Identity(
        IEnumerable<SourceBaselineId>? baselines = null,
        RandomAlgorithmId? algorithm = null) =>
        new(Ruleset, Schema, baselines ?? [CoreBook], algorithm);

    [Fact]
    public void Equal_when_every_component_matches()
    {
        Assert.Equal(Identity(), Identity());
        Assert.Equal(Identity().GetHashCode(), Identity().GetHashCode());
        Assert.True(Identity() == Identity());
    }

    [Fact]
    public void Baseline_order_is_significant()
    {
        Assert.NotEqual(
            Identity([CoreBook, Supplement]),
            Identity([Supplement, CoreBook]));
    }

    [Fact]
    public void An_added_corpus_is_an_incompatibility()
    {
        Assert.NotEqual(Identity([CoreBook]), Identity([CoreBook, Supplement]));
    }

    [Fact]
    public void An_engine_consuming_no_randomness_is_distinct_from_one_that_does()
    {
        var without = Identity();
        var with = Identity(algorithm: RandomAlgorithmId.Pcg32SetSeq64XshRr32);

        Assert.NotEqual(without, with);
        Assert.True(without.IsDeterministicWithoutRandomness);
        Assert.False(with.IsDeterministicWithoutRandomness);
    }

    [Fact]
    public void Requires_at_least_one_corpus()
    {
        Assert.Throws<ArgumentException>(() => Identity([]));
    }

    [Fact]
    public void Rejects_struct_defaults_that_bypass_their_constructors()
    {
        Assert.Throws<ArgumentException>(
            () => new ReplayCompatibilityIdentity(default, Schema, [CoreBook]));
        Assert.Throws<ArgumentException>(
            () => new ReplayCompatibilityIdentity(Ruleset, Schema, [default(SourceBaselineId)]));
        Assert.Throws<ArgumentException>(
            () => new ReplayCompatibilityIdentity(Ruleset, Schema, [CoreBook], default(RandomAlgorithmId)));
    }

    /// <summary>
    /// A defensive-copy guard. This passed before the immutability work too -- the
    /// constructor already copied its input -- so it is a regression guard, not evidence for
    /// that fix. The defect was on the other side: the property handed the backing array out
    /// as <c>IReadOnlyList</c>, which a caller could cast straight back to an array. That is
    /// now closed by the property's type, which is why the test below asserts the type rather
    /// than attempting a cast that no longer compiles.
    /// </summary>
    [Fact]
    public void Mutating_the_array_that_was_passed_in_cannot_change_the_identity()
    {
        var baselines = new[] { CoreBook };
        var identity = new ReplayCompatibilityIdentity(Ruleset, Schema, baselines);
        int hashBefore = identity.GetHashCode();

        baselines[0] = Supplement;

        Assert.Equal(hashBefore, identity.GetHashCode());
        Assert.Equal(CoreBook, identity.SourceBaselines[0]);
        Assert.Equal(Identity(), identity);
    }

    /// <summary>
    /// The fix for the exposure defect is carried by the property's type: an
    /// <see cref="System.Collections.Immutable.ImmutableArray{T}"/> cannot be cast to
    /// <c>SourceBaselineId[]</c>, so the original one-line attack no longer compiles.
    ///
    /// <para>
    /// <c>ImmutableCollectionsMarshal.AsArray</c> still returns an array behind what the
    /// property hands out, but that array is now a copy; the test below writes through it.
    /// </para>
    /// </summary>
    [Fact]
    public void The_exposed_baselines_are_not_an_array_a_caller_can_write_through()
    {
        var exposed = Identity().SourceBaselines;

        Assert.IsType<System.Collections.Immutable.ImmutableArray<SourceBaselineId>>(exposed);
        Assert.False(exposed.GetType().IsArray);
    }

    /// <summary>
    /// The escape hatch the type used to document (#55): writing through the array behind
    /// what <c>SourceBaselines</c> returns must not change the identity it came from.
    /// </summary>
    [Fact]
    public void Writing_through_the_exposed_baselines_cannot_rewrite_the_identity()
    {
        var identity = Identity();
        int hashBefore = identity.GetHashCode();

        var backing = System.Runtime.InteropServices.ImmutableCollectionsMarshal.AsArray(identity.SourceBaselines)!;
        backing[0] = new SourceBaselineId("rewritten", HashB, "pdf-bytes");

        Assert.Equal(Identity(), identity);
        Assert.Equal(hashBefore, identity.GetHashCode());
        Assert.Equal(CoreBook, identity.SourceBaselines[0]);
    }

    /// <summary>
    /// A SourceLocator names a corpus by id alone, so two baselines sharing an id would make
    /// every citation into that corpus ambiguous about which baseline it was checked against.
    /// </summary>
    [Fact]
    public void Two_baselines_for_the_same_corpus_are_rejected()
    {
        var duplicate = new SourceBaselineId(CoreBook.SourceId, HashB, "pdf-bytes");

        var error = Assert.Throws<ArgumentException>(() => Identity([CoreBook, duplicate]));
        Assert.Contains(CoreBook.SourceId, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Is_not_equal_to_null_and_survives_reference_equality()
    {
        var identity = Identity();

        Assert.False(identity.Equals(null));
        Assert.True(identity.Equals(identity));
        Assert.False(identity == null);
        Assert.True(identity != null);
    }
}
