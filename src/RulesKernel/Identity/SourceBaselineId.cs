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
    /// The moment of the corpus this baseline pins, for a corpus that is revised over time;
    /// <see langword="null"/> for one that is not. See the type documentation.
    /// </summary>
    public DateOnly? AsOf { get; }

    /// <exception cref="ArgumentException">
    /// <paramref name="sourceId"/> is null, empty, or whitespace, or
    /// <paramref name="contentHash"/> is not 64 hexadecimal characters.
    /// </exception>
    public SourceBaselineId(string sourceId, string contentHash, DateOnly? asOf = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentHash);

        if (!IsSha256Hex(contentHash))
        {
            throw new ArgumentException(
                "contentHash must be 64 hexadecimal characters (a SHA-256 digest); got "
                + $"'{contentHash}'.",
                nameof(contentHash));
        }

        SourceId = sourceId;
        ContentHash = contentHash.ToLowerInvariant();
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

    /// <summary>False for <c>default(SourceBaselineId)</c>, which bypasses the constructor.</summary>
    public bool IsValid => !string.IsNullOrWhiteSpace(SourceId) && ContentHash is not null;

    /// <inheritdoc/>
    public override string ToString() =>
        AsOf is { } date
            ? string.Create(CultureInfo.InvariantCulture, $"{SourceId}@{date:yyyy-MM-dd}#{ContentHash}")
            : $"{SourceId}#{ContentHash}";
}
