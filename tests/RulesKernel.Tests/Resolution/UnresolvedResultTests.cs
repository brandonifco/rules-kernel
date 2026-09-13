using System;
using RulesKernel.Provenance;
using RulesKernel.Resolution;

namespace RulesKernel.Tests.Resolution;

public sealed class UnresolvedResultTests
{
    private static readonly SourceLocator Locator = new("cfr-26", "§ 1.401(k)-1(b)(4)(ii)");

    [Fact]
    public void Requires_a_description_of_what_was_attempted()
    {
        Assert.Throws<ArgumentException>(
            () => new UnresolvedResult(UnresolvedReason.UnsupportedRule, " ", Locator));
    }

    [Fact]
    public void Requires_a_locator_because_an_uncited_gap_is_not_actionable()
    {
        Assert.Throws<ArgumentException>(
            () => new UnresolvedResult(UnresolvedReason.UnsupportedRule, "compute the limit", default));
    }

    /// <summary>
    /// The vocabulary is closed by docs/decisions/0004, but a C# enum accepts any value of
    /// its underlying type -- so "closed" is only true if something checks.
    /// </summary>
    [Fact]
    public void An_undefined_reason_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new UnresolvedResult((UnresolvedReason)999, "compute the limit", Locator));
    }

    [Theory]
    [InlineData(UnresolvedReason.UnsupportedRule)]
    [InlineData(UnresolvedReason.RequiresInterpretation)]
    [InlineData(UnresolvedReason.OutsideCurrentScope)]
    [InlineData(UnresolvedReason.UnsupportedInteraction)]
    [InlineData(UnresolvedReason.MissingRulesData)]
    public void Every_declared_reason_is_accepted(UnresolvedReason reason)
    {
        Assert.Equal(reason, new UnresolvedResult(reason, "x", Locator).Reason);
    }

    [Fact]
    public void Carries_all_three_facts_a_caller_needs()
    {
        var result = new UnresolvedResult(
            UnresolvedReason.MissingRulesData, "compute the elective deferral limit", Locator);

        Assert.Equal(UnresolvedReason.MissingRulesData, result.Reason);
        Assert.Equal("compute the elective deferral limit", result.Attempted);
        Assert.Equal(Locator, result.Locator);
        Assert.Contains("cfr-26", result.ToString(), StringComparison.Ordinal);
    }
}
