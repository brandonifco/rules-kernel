using System.Globalization;

namespace RulesKernel.Identity;

/// <summary>
/// Pins one authoritative corpus into a replay identity: which corpus, what exact content,
/// and -- when the corpus is one that changes over time -- which moment of it.
///
/// <para>
/// <b>Why the content hash is not enough.</b> A hash proves two people read identical
/// bytes. It does not say what those bytes <em>were</em>, and for a continuously revised
/// corpus that is the question an engine actually answers: "what did this rule say on this
/// date." A regulation, a statute, an errata'd rulebook and a reprinted board game all
/// share that shape. <see cref="AsOf"/> records the moment requested; the hash records what
/// was received. Both are needed, and neither substitutes for the other
/// (docs/decisions/0003).
/// </para>
///
/// <para>
/// <see cref="AsOf"/> is optional because a corpus can be genuinely timeless -- a single
/// printing of a book that will never be revised. Absent means "this corpus has no
/// temporal dimension", not "unknown": a caller that does not know the date of a revisable
/// corpus has an unpinned baseline, which is a provenance failure, not a null.
/// </para>
/// </summary>
public readonly record struct SourceBaselineId
{
    /// <summary>The corpus's stable logical identifier, as declared in its manifest.</summary>
    public string SourceId { get; }

    /// <summary>
    /// The lowercase hex SHA-256 of the pinned corpus content. Normalized on construction,
    /// so two baselines naming the same content in different letter case compare equal.
    /// </summary>
    public string ContentHash { get; }

    /// <summary>
    /// What the hash was computed over, in the grammar the corpus's adapter declares --
    /// <c>"pdf-bytes"</c>, <c>"extracted-json"</c>, <c>"id-roster"</c>.
    ///
    /// <para>
    /// Required, because a hash on its own does not say what it is a hash of, and this type
    /// was documented as though it did. A real engine demonstrates the gap: SRD_Combat
    /// fingerprints its corpus by hashing the sorted roster of content ids and says so in
    /// its own comment -- "by id alone, not by the numbers behind them. Two loads with the
    /// exact same roster of ids fingerprint identically even if a description changed
    /// underneath." That is a legitimate, deliberately coarse hash, and it is the value that
    /// engine would naturally put here.
    /// </para>
    ///
    /// <para>
    /// Without this field, two engines could both publish <c>srd-5.2.1#&lt;64 hex&gt;</c>
    /// having hashed the PDF bytes, the extracted JSON, and the id roster respectively --
    /// comparing unequal for identical corpora, or, worse, an engine believing its baseline
    /// pins content it does not pin. With it, the mismatch is visible as data, which is the
    /// same reason <see cref="RandomAlgorithmId"/> exists rather than trusting that two
    /// generators agreeing today will keep agreeing.
    /// </para>
    ///
    /// <para>
    /// The kernel does not interpret this string, exactly as it does not interpret a
    /// citation: only the adapter that produced the hash can define what it covers.
    /// </para>
    ///
    /// <para>
    /// It does fix the string's <em>form</em>: one or more runs of lowercase ASCII letters and
    /// digits, joined by single hyphens or dots -- <c>ecfr-versioner-xml</c>,
    /// <c>srd-5.2.1-pdftotext-24.02.0-page-marked</c>. The field is compared ordinally, so
    /// <c>eCFR-versioner-XML</c> or a trailing space would make two engines pinning the same
    /// bytes by the same method compare unequal. A Part 107 probe built against the same corpus
    /// as a real engine found exactly that, and the vocabulary itself is still the adapter's
    /// (docs/decisions/0019).
    /// </para>
    /// </summary>
    public string HashDerivation { get; }

    /// <summary>
    /// The moment of the corpus this baseline pins, for a corpus that is revised over time;
    /// <see langword="null"/> for one that is not. See the type documentation.
    /// </summary>
    public DateOnly? AsOf { get; }

    /// <exception cref="ArgumentException">
    /// <paramref name="sourceId"/>, <paramref name="contentHash"/> or
    /// <paramref name="hashDerivation"/> is null, empty, or whitespace,
    /// <paramref name="contentHash"/> is not 64 hexadecimal characters, or
    /// <paramref name="hashDerivation"/> is not lowercase ASCII letters and digits joined by
    /// single hyphens or dots.
    /// </exception>
    public SourceBaselineId(
        string sourceId, string contentHash, string hashDerivation, DateOnly? asOf = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(hashDerivation);
        Provenance.SourceLocator.ThrowIfNotACanonicalSourceId(sourceId, nameof(sourceId));

        if (!IsSha256Hex(contentHash))
        {
            throw new ArgumentException(
                "contentHash must be 64 hexadecimal characters (a SHA-256 digest); got "
                + $"'{contentHash}'.",
                nameof(contentHash));
        }

        if (!IsCanonicalDerivation(hashDerivation))
        {
            throw new ArgumentException(
                "hashDerivation must be lowercase ASCII letters and digits joined by single "
                + $"hyphens or dots, such as 'ecfr-versioner-xml'; got '{hashDerivation}'. It is "
                + "compared ordinally, so any other spelling of the same derivation would compare "
                + "unequal (docs/decisions/0019).",
                nameof(hashDerivation));
        }

        SourceId = sourceId;
        ContentHash = contentHash.ToLowerInvariant();
        HashDerivation = hashDerivation;
        AsOf = asOf;
    }

    private static bool IsSha256Hex(string value)
    {
        if (value.Length != 64)
        {
            return false;
        }

        foreach (char c in value)
        {
            bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!hex)
            {
                return false;
            }
        }

        return true;
    }

    // Runs of [a-z0-9] joined by single '-' or '.'; no leading, trailing or doubled separator.
    // Written out rather than as a Regex so the floor stays allocation-free and obvious.
    private static bool IsCanonicalDerivation(string value)
    {
        bool previousWasSeparator = true;
        foreach (char c in value)
        {
            bool alphanumeric = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9');
            if (alphanumeric)
            {
                previousWasSeparator = false;
            }
            else if ((c == '-' || c == '.') && !previousWasSeparator)
            {
                previousWasSeparator = true;
            }
            else
            {
                return false;
            }
        }

        return !previousWasSeparator;
    }

    /// <summary>False for <c>default(SourceBaselineId)</c>, which bypasses the constructor.</summary>
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(SourceId)
        && ContentHash is not null
        && !string.IsNullOrWhiteSpace(HashDerivation);

    /// <inheritdoc/>
    public override string ToString() =>
        AsOf is { } date
            ? string.Create(CultureInfo.InvariantCulture, $"{SourceId}@{date:yyyy-MM-dd}#{HashDerivation}:{ContentHash}")
            : $"{SourceId}#{HashDerivation}:{ContentHash}";
}
