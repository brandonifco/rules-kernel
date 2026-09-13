using System.Collections.Generic;
using RulesKernel.Identity;
using RulesKernel.Provenance;
using RulesKernel.Resolution;

namespace RegulatoryProbe;

/// <summary>
/// A deliberately tiny engine over a regulatory corpus, shaped like a real one and
/// implementing nothing real.
///
/// THE NUMBERS BELOW ARE FABRICATED. They are not tax figures, they have not been checked
/// against any source, and nothing here is advice. Inventing them is the honest choice for
/// a probe: the kernel's whole provenance discipline says a value without a verified
/// citation is worthless, and a probe has no verified citation to offer. What is being
/// tested is shape, not substance.
///
/// The shape is the interesting part, because it is the shape a tabletop engine cannot
/// produce: two corpora rather than one, each pinned to a date rather than to a printing,
/// citations by designation rather than by page, and not a single random value drawn from
/// end to end.
/// </summary>
public sealed class DeferralLimitEngine
{
    /// <summary>The statute establishing the limit and its adjustment mechanism.</summary>
    public const string StatuteCorpus = "usc-26-402g";

    /// <summary>The annual adjustment notices that set each year's figure.</summary>
    public const string AdjustmentCorpus = "irs-adjustments";

    /// <summary>The first plan year this engine covers at all.</summary>
    public const int FirstCoveredYear = 2015;

    private readonly IReadOnlyDictionary<int, int> _adjustments;

    /// <summary>The replay identity of this engine over the corpora it was built from.</summary>
    public ReplayCompatibilityIdentity Identity { get; }

    public DeferralLimitEngine(
        ReplayCompatibilityIdentity identity,
        IReadOnlyDictionary<int, int> adjustmentsByYear)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(adjustmentsByYear);
        Identity = identity;
        _adjustments = adjustmentsByYear;
    }

    private static SourceLocator Statute(string designation) => new(StatuteCorpus, designation);

    private static SourceLocator Adjustment(string designation) => new(AdjustmentCorpus, designation);

    /// <summary>
    /// Resolves the elective deferral limit for a plan year, in whole currency units.
    ///
    /// Returns the union rather than a bare value because the domain genuinely reaches
    /// unresolved branches: a year before the engine's coverage, a year with no adjustment
    /// loaded, and a case the corpus does not settle. That is the totality test from
    /// docs/decisions/0004 applied honestly rather than assumed away.
    /// </summary>
    public Resolution<int> ResolveLimit(int planYear, bool isShortPlanYear = false)
    {
        if (planYear < FirstCoveredYear)
        {
            return Resolution<int>.FromUnresolved(new UnresolvedResult(
                UnresolvedReason.OutsideCurrentScope,
                $"elective deferral limit for plan year {planYear}",
                Statute("§ 402(g)(1)(B)")));
        }

        if (isShortPlanYear)
        {
            // The corpus does not settle whether the limit prorates for a short plan year.
            // An engine that picked one reading here would be guessing, and a caller would
            // have no way to tell that from a settled answer.
            return Resolution<int>.FromUnresolved(new UnresolvedResult(
                UnresolvedReason.RequiresInterpretation,
                $"elective deferral limit for a short plan year ending in {planYear}",
                Statute("§ 402(g)(1)(A)")));
        }

        if (!_adjustments.TryGetValue(planYear, out int limit))
        {
            // The algorithm exists; the adjustment notice for this year was never loaded.
            return Resolution<int>.FromUnresolved(new UnresolvedResult(
                UnresolvedReason.MissingRulesData,
                $"elective deferral limit for plan year {planYear}",
                Adjustment($"annual adjustment for {planYear}")));
        }

        return Resolution<int>.FromValue(limit);
    }

    /// <summary>
    /// Resolves the catch-up contribution limit, which this probe deliberately does not
    /// implement -- the distinction between "not built yet" and "the corpus is silent"
    /// matters, and both need to be expressible.
    /// </summary>
    public Resolution<int> ResolveCatchUpLimit(int planYear) =>
        Resolution<int>.FromUnresolved(new UnresolvedResult(
            UnresolvedReason.UnsupportedRule,
            $"catch-up contribution limit for plan year {planYear}",
            Statute("§ 414(v)")));

    /// <summary>
    /// Resolves the combined limit where a participant is covered by two plans, which
    /// depends on an interaction between rules this probe implements separately and has
    /// not resolved together.
    /// </summary>
    public Resolution<int> ResolveCombinedLimit(int planYear) =>
        Resolution<int>.FromUnresolved(new UnresolvedResult(
            UnresolvedReason.UnsupportedInteraction,
            $"combined limit across two plans for plan year {planYear}",
            Statute("§ 402(g)(1) with § 414(v)")));
}
