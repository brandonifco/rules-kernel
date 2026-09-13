namespace RulesKernel.Resolution;

/// <summary>
/// The closed vocabulary for a result the engine cannot resolve.
///
/// <para>
/// Closed by design: adding a sixth value means superseding the decision that fixed this
/// list (docs/decisions/0004), not extending this enum quietly. The five cover the reasons
/// a faithful engine fails to answer, and they are deliberately about the <em>engine's</em>
/// relationship to its corpus rather than about any particular subject matter -- an
/// unimplemented tabletop manoeuvre and an unimplemented tax election are both
/// <see cref="UnsupportedRule"/>.
/// </para>
/// </summary>
public enum UnresolvedReason
{
    /// <summary>The corpus defines this; the engine has not implemented it yet.</summary>
    UnsupportedRule,

    /// <summary>The corpus is genuinely ambiguous here; a recorded decision is needed before this can resolve.</summary>
    RequiresInterpretation,

    /// <summary>Defined in a supplement, another edition, or another jurisdiction; deliberately not implemented.</summary>
    OutsideCurrentScope,

    /// <summary>Both rules exist, but their combination is not resolved.</summary>
    UnsupportedInteraction,

    /// <summary>The algorithm exists; the structured data it needs is absent.</summary>
    MissingRulesData,
}
