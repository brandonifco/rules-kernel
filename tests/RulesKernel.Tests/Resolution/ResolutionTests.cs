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

    /// <summary>
    /// Positional matching is what the cases are public for. Making the constructors internal
    /// meant rewriting them from positional records to bodied ones, which silently removed
    /// the compiler-generated deconstructor -- a source break for callers doing exactly what
    /// the documentation tells them to do. Deconstruct is now explicit, and pinned here.
    /// </summary>
    [Fact]
    public void Both_cases_support_positional_matching()
    {
        Assert.Equal(7, Resolution<int>.FromValue(7) switch
        {
            Resolution<int>.Resolved(var value) => value,
            Resolution<int>.Unresolved(var _) => -1,
            _ => -2,
        });

        Assert.Equal(
            UnresolvedReason.UnsupportedRule,
            Resolution<int>.FromUnresolved(Gap()) switch
            {
                Resolution<int>.Unresolved(var gap) => gap.Reason,
                _ => throw new InvalidOperationException("expected the unresolved case"),
            });
    }

    /// <summary>
    /// The factories are the only way in. While the nested cases had public constructors,
    /// new Resolution&lt;int&gt;.Unresolved(null!) bypassed the null check FromUnresolved
    /// performs -- an unguarded second door that callers would eventually find.
    /// </summary>
    [Theory]
    [InlineData(typeof(Resolution<int>.Resolved))]
    [InlineData(typeof(Resolution<int>.Unresolved))]
    public void The_cases_cannot_be_constructed_from_outside_the_assembly(Type caseType)
    {
        Assert.Empty(caseType.GetConstructors(System.Reflection.BindingFlags.Public
                                              | System.Reflection.BindingFlags.Instance));
    }

    [Fact]
    public void FromUnresolved_rejects_a_null_result()
    {
        Assert.Throws<ArgumentNullException>(() => Resolution<int>.FromUnresolved(null!));
    }
}
