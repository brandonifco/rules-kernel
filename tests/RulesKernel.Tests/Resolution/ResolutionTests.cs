using System;
using RulesKernel.Provenance;
using RulesKernel.Resolution;

namespace RulesKernel.Tests.Resolution;

public sealed class ResolutionTests
{
    private static readonly SourceLocator Locator = new("core-rules", "printed p. 45");

    private static UnresolvedResult Gap(UnresolvedReason reason = UnresolvedReason.UnsupportedRule) =>
        new(reason, "resolve an opposed check", Locator);

    [Fact]
    public void Resolved_carries_the_value()
    {
        var resolution = Resolution<int>.FromValue(7);

        Assert.True(resolution.IsResolved);
        Assert.Equal(7, resolution.Match(value => value, _ => -1));
    }

    [Fact]
    public void Unresolved_carries_the_reason_and_the_locator()
    {
        var resolution = Resolution<int>.FromUnresolved(Gap(UnresolvedReason.RequiresInterpretation));

        Assert.False(resolution.IsResolved);
        var result = resolution.Match<UnresolvedResult?>(_ => null, gap => gap);
        Assert.NotNull(result);
        Assert.Equal(UnresolvedReason.RequiresInterpretation, result!.Reason);
        Assert.Equal(Locator, result.Locator);
    }

    [Fact]
    public void Match_requires_both_handlers()
    {
        var resolution = Resolution<int>.FromValue(1);

        Assert.Throws<ArgumentNullException>(
            () => resolution.Match(null!, _ => 0));
        Assert.Throws<ArgumentNullException>(
            () => resolution.Match(value => value, null!));
    }

    [Fact]
    public void Equality_is_by_value_so_two_identical_outcomes_compare_equal()
    {
        Assert.Equal(Resolution<int>.FromValue(3), Resolution<int>.FromValue(3));
        Assert.NotEqual(Resolution<int>.FromValue(3), Resolution<int>.FromValue(4));
        Assert.Equal(Resolution<int>.FromUnresolved(Gap()), Resolution<int>.FromUnresolved(Gap()));
    }

    [Fact]
    public void FromUnresolved_rejects_a_null_result()
    {
        Assert.Throws<ArgumentNullException>(() => Resolution<int>.FromUnresolved(null!));
    }
}
