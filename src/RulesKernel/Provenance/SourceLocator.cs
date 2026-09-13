namespace RulesKernel.Provenance;

/// <summary>
/// Where in an authoritative corpus a rule lives. Every implemented rule cites one; so
/// does every unresolved result, which is what makes a gap actionable rather than
/// mysterious.
///
/// <para>
/// <b>The citation is opaque to the kernel on purpose.</b> Corpora do not share an
/// addressing scheme: a printed rulebook is located by page, a regulation by designation
/// (<c>§ 1.401(k)-1(b)(4)(ii)</c>), a statute by title and section, a board game by
/// numbered rule (<c>4.2.1</c>), a web corpus by anchor. A kernel that hard-coded page
/// numbers would exclude three of those five. The locator therefore names the corpus and
/// carries a citation string whose grammar is owned by that corpus's adapter, which is
/// also the only component able to validate it.
/// </para>
///
/// <para>
/// This keeps the kernel honest about what it can check: that a citation was supplied at
/// all, and which corpus it points into. Whether the citation is well-formed is the
/// adapter's question, and whether it points at the right passage is the reviewer's.
/// </para>
/// </summary>
public readonly record struct SourceLocator
{
    /// <summary>
    /// The corpus this citation points into, matching a
    /// <see cref="Identity.SourceBaselineId.SourceId"/> in the engine's replay identity.
    /// </summary>
    public string SourceId { get; }

    /// <summary>
    /// The location within that corpus, in the grammar its adapter declares. Verbatim; the
    /// kernel neither parses nor normalizes it.
    /// </summary>
    public string Citation { get; }

    /// <exception cref="ArgumentException">
    /// <paramref name="sourceId"/> or <paramref name="citation"/> is null, empty, or
    /// whitespace.
    /// </exception>
    public SourceLocator(string sourceId, string citation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(citation);
        ThrowIfNotACanonicalSourceId(sourceId, nameof(sourceId));

        SourceId = sourceId;
        Citation = citation;
    }


    /// <summary>
    /// Corpus identifiers are compared with ordinal, case-sensitive equality wherever they
    /// are compared at all -- by <c>SourceBaselineId</c>'s own equality, by the duplicate
    /// check in <see cref="Identity.ReplayCompatibilityIdentity"/>, and by any consumer
    /// matching a locator back to the baseline it cites.
    ///
    /// <para>
    /// That contract is stated rather than softened. Case-folding would be a guess about
    /// whether a corpus scheme is case-sensitive, and this type has no basis for guessing.
    /// Surrounding whitespace is different: <c>"core "</c> is never a deliberate identifier,
    /// only a typo, and one that would make two baselines for the same corpus compare
    /// unequal -- silently defeating the duplicate check whose whole rationale is that an id
    /// identifies a corpus. So whitespace is rejected and case is left alone.
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="sourceId"/> has leading or trailing whitespace.</exception>
    internal static void ThrowIfNotACanonicalSourceId(string sourceId, string parameterName)
    {
        if (sourceId.Length != sourceId.Trim().Length)
        {
            throw new ArgumentException(
                $"sourceId '{sourceId}' has leading or trailing whitespace. Identifiers are "
                + "compared ordinally, so a stray space makes two references to the same corpus "
                + "compare unequal.",
                parameterName);
        }
    }

    /// <summary>False for <c>default(SourceLocator)</c>, which bypasses the constructor.</summary>
    public bool IsValid => !string.IsNullOrWhiteSpace(SourceId) && !string.IsNullOrWhiteSpace(Citation);

    /// <inheritdoc/>
    public override string ToString() => $"{SourceId} / {Citation}";
}
