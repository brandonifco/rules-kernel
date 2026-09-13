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
        SourceId = sourceId;
        Citation = citation;
    }

    /// <summary>False for <c>default(SourceLocator)</c>, which bypasses the constructor.</summary>
    public bool IsValid => !string.IsNullOrWhiteSpace(SourceId) && !string.IsNullOrWhiteSpace(Citation);

    /// <inheritdoc/>
    public override string ToString() => $"{SourceId} / {Citation}";
}
