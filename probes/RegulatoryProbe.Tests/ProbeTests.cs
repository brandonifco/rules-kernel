using System;
using System.Collections.Generic;
using System.Linq;
using RulesKernel.Identity;
using RulesKernel.Provenance;
using RulesKernel.Resolution;

namespace RegulatoryProbe;

/// <summary>
/// What this probe is actually testing is the kernel, not the engine above it. Every
/// assertion here is of the form "a non-tabletop consumer can express this", and each one
/// covers a kernel property that neither real engine exercises.
/// </summary>
public sealed class ProbeTests
{
    private const string StatuteHash = "a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1";
    private const string AdjustHash2019 = "b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2";
    private const string AdjustHash2024 = "c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3";

    private static ReplayCompatibilityIdentity IdentityAsOf(DateOnly asOf, string adjustmentHash) =>
        new(
            ruleset: new RulesetVersion("deferral-limit", 1),
            replaySchema: new ReplaySchemaVersion(1),
            sourceBaselines:
            [
                new SourceBaselineId(DeferralLimitEngine.StatuteCorpus, StatuteHash, "ecfr-xml", asOf),
                new SourceBaselineId(DeferralLimitEngine.AdjustmentCorpus, adjustmentHash, "notice-text", asOf),
            ]);

    private static DeferralLimitEngine Engine(DateOnly asOf, string hash, Dictionary<int, int> data) =>
        new(IdentityAsOf(asOf, hash), data);

    private static DeferralLimitEngine Engine2019() =>
        Engine(new DateOnly(2019, 3, 14), AdjustHash2019, new() { [2019] = 100 });

    // --------------------------------------------------------------- the no-randomness path

    [Fact]
    public void An_engine_over_a_regulation_declares_that_it_consumes_no_randomness()
    {
        Assert.True(Engine2019().Identity.IsDeterministicWithoutRandomness);
        Assert.Null(Engine2019().Identity.RandomAlgorithm);
    }

    /// <summary>
    /// The load-bearing assertion of this entire probe. If this assembly ever acquires a
    /// dependency on the randomness package, "randomness is optional" has stopped being
    /// true of the kernel as shipped, whatever the documentation says.
    /// </summary>
    [Fact]
    public void This_assembly_references_the_kernel_and_never_the_randomness_package()
    {
        string[] referenced = typeof(DeferralLimitEngine).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(n => n.StartsWith("RulesKernel", StringComparison.Ordinal))
            .ToArray();

        Assert.Contains("RulesKernel", referenced);
        Assert.DoesNotContain("RulesKernel.Randomness", referenced);
    }

    // ------------------------------------------------------------------ the temporal axis

    [Fact]
    public void The_same_corpus_pinned_at_two_moments_is_two_different_engines()
    {
        var asRead2019 = IdentityAsOf(new DateOnly(2019, 3, 14), AdjustHash2019);
        var asRead2024 = IdentityAsOf(new DateOnly(2024, 1, 1), AdjustHash2024);

        Assert.NotEqual(asRead2019, asRead2024);
    }

    /// <summary>
    /// The case a content hash alone cannot express: identical bytes, different moments.
    /// A regulation reprinted unchanged is still a different baseline if the question is
    /// "what was in force on this date".
    /// </summary>
    [Fact]
    public void Identical_content_pinned_at_different_moments_is_still_two_baselines()
    {
        Assert.NotEqual(
            IdentityAsOf(new DateOnly(2019, 3, 14), AdjustHash2019),
            IdentityAsOf(new DateOnly(2020, 3, 14), AdjustHash2019));
    }

    [Fact]
    public void A_run_is_reproducible_when_every_baseline_matches()
    {
        Assert.Equal(
            IdentityAsOf(new DateOnly(2019, 3, 14), AdjustHash2019),
            IdentityAsOf(new DateOnly(2019, 3, 14), AdjustHash2019));
    }

    // ---------------------------------------------------------------- several corpora at once

    [Fact]
    public void An_engine_pins_every_corpus_it_draws_on()
    {
        var baselines = Engine2019().Identity.SourceBaselines;

        Assert.Equal(2, baselines.Length);
        Assert.Equal(DeferralLimitEngine.StatuteCorpus, baselines[0].SourceId);
        Assert.Equal(DeferralLimitEngine.AdjustmentCorpus, baselines[1].SourceId);
        Assert.All(baselines, b => Assert.NotNull(b.AsOf));
    }

    [Fact]
    public void Dropping_a_corpus_is_an_incompatibility()
    {
        var both = IdentityAsOf(new DateOnly(2019, 3, 14), AdjustHash2019);
        var statuteOnly = new ReplayCompatibilityIdentity(
            new RulesetVersion("deferral-limit", 1),
            new ReplaySchemaVersion(1),
            [new SourceBaselineId(DeferralLimitEngine.StatuteCorpus, StatuteHash, "ecfr-xml", new DateOnly(2019, 3, 14))]);

        Assert.NotEqual(both, statuteOnly);
    }

    // ------------------------------------------------------------------- resolution and citations

    [Fact]
    public void A_covered_year_resolves_to_a_value()
    {
        var outcome = Engine2019().ResolveLimit(2019);

        Assert.True(outcome.IsResolved);
        Assert.Equal(100, outcome.Match(value => value, _ => -1));
    }

    [Theory]
    [MemberData(nameof(UnresolvedCases))]
    public void Every_unresolved_reason_is_reachable_and_cites_a_designation(
        UnresolvedReason expected, Func<DeferralLimitEngine, Resolution<int>> call)
    {
        var outcome = call(Engine2019());
        var gap = outcome.Match<UnresolvedResult?>(_ => null, g => g);

        Assert.NotNull(gap);
        Assert.Equal(expected, gap!.Reason);
        Assert.Contains("§", gap.Locator.Citation, StringComparison.Ordinal);
        Assert.False(outcome.IsResolved);
    }

    public static TheoryData<UnresolvedReason, Func<DeferralLimitEngine, Resolution<int>>> UnresolvedCases() => new()
    {
        { UnresolvedReason.OutsideCurrentScope, e => e.ResolveLimit(1998) },
        { UnresolvedReason.RequiresInterpretation, e => e.ResolveLimit(2019, isShortPlanYear: true) },
        { UnresolvedReason.UnsupportedRule, e => e.ResolveCatchUpLimit(2019) },
        { UnresolvedReason.UnsupportedInteraction, e => e.ResolveCombinedLimit(2019) },
    };

    /// <summary>
    /// MissingRulesData cites the adjustment corpus rather than the statute: the algorithm
    /// is fine, the data for that year was never loaded, and the citation has to point at
    /// the thing that is actually absent for the gap to be actionable.
    /// </summary>
    [Fact]
    public void A_year_with_no_loaded_adjustment_cites_the_adjustment_corpus()
    {
        var gap = Engine2019().ResolveLimit(2024).Match<UnresolvedResult?>(_ => null, g => g);

        Assert.NotNull(gap);
        Assert.Equal(UnresolvedReason.MissingRulesData, gap!.Reason);
        Assert.Equal(DeferralLimitEngine.AdjustmentCorpus, gap.Locator.SourceId);
    }

    /// <summary>
    /// Citations here are designations, not pages. A kernel that had privileged page
    /// numbers would have made this engine unrepresentable.
    /// </summary>
    [Fact]
    public void Citations_are_designations_rather_than_pages()
    {
        var gap = Engine2019().ResolveCatchUpLimit(2019).Match<UnresolvedResult?>(_ => null, g => g);

        Assert.NotNull(gap);
        Assert.Equal("§ 414(v)", gap!.Locator.Citation);
        Assert.DoesNotContain("p.", gap.Locator.Citation, StringComparison.OrdinalIgnoreCase);
    }
}
