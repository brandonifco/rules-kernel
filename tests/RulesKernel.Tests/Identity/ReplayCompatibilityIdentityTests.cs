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
    private static readonly SourceBaselineId CoreBook = new("core", HashA);
    private static readonly SourceBaselineId Supplement = new("supplement", HashB);

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
