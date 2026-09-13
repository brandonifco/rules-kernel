using RulesKernel.Provenance;

namespace RulesKernel.Resolution;

/// <summary>
/// An explicit "this engine cannot resolve this" outcome: never nothing, never a default,
/// never a guess, and never an exception for this case. A caller receiving one knows
/// exactly what was attempted, why it was not resolved, and where in the corpus the real
/// rule lives.
///
/// <para>
/// This is the second half of the determinism contract. The first half -- same inputs,
/// same outputs -- is worthless if the engine reaches an unimplemented rule and invents an
/// answer: it would then be reproducibly wrong. An engine honest about what it does not
/// cover is useful; one that guesses at the rest is not.
/// </para>
///
/// <para>
/// An exception is the wrong shape here because being unable to resolve a rule is an
/// ordinary, expected, <em>enumerable</em> outcome of a partial engine, not a defect. It
/// belongs in the return type where a caller must acknowledge it.
/// </para>
/// </summary>
public sealed record UnresolvedResult
{
    /// <summary>Which of the closed reasons applies.</summary>
    public UnresolvedReason Reason { get; }

    /// <summary>What the caller asked the engine to resolve, in plain language.</summary>
    public string Attempted { get; }

    /// <summary>Where the underlying rule lives, so the gap is actionable.</summary>
    public SourceLocator Locator { get; }

    /// <exception cref="ArgumentException">
    /// <paramref name="attempted"/> is null, empty, or whitespace, or
    /// <paramref name="locator"/> is the struct default.
    /// </exception>
    public UnresolvedResult(UnresolvedReason reason, string attempted, SourceLocator locator)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(attempted);

        if (!locator.IsValid)
        {
            throw new ArgumentException(
                "locator is default(SourceLocator); an unresolved result must still cite where "
                + "the rule lives -- that citation is what makes the gap actionable.",
                nameof(locator));
        }

        Reason = reason;
        Attempted = attempted;
        Locator = locator;
    }

    /// <inheritdoc/>
    public override string ToString() => $"{Reason}: {Attempted} [{Locator}]";
}
