using System.Collections.Generic;
using System.Linq;

namespace RulesKernel.Identity;

/// <summary>
/// The complete replay compatibility identity. The kernel's invariant is "same ruleset
/// version + same corpus baselines + same initial state + same ordered decisions (+ the
/// same random algorithm and state, where randomness is consumed) = the same outcomes and
/// the same ordered history". This type makes the ruleset/corpus/replay portion of that
/// explicitly comparable.
///
/// <para>
/// Two identities are equal only when every component matches. Each component changes for
/// a different, independent reason, so a mismatch in any single one is a genuine
/// incompatibility -- not something to average away.
/// </para>
///
/// <para>
/// <see cref="SourceBaselines"/> is an ordered collection, not a single value: an engine
/// routinely draws on more than one corpus -- a core book plus supplements, a statute plus
/// its implementing regulations. Order is significant, and equality is sequential: the
/// same baselines listed in a different order are a different identity. That is the
/// stricter and safer default for a compatibility check (docs/decisions/0003).
/// </para>
///
/// <para>
/// <see cref="RandomAlgorithm"/> is optional. An engine that resolves no random outcome --
/// a regulation, a statute, a deterministic board game -- carries <see langword="null"/>
/// here and takes no dependency on <c>RulesKernel.Randomness</c> at all
/// (docs/decisions/0002).
/// </para>
///
/// <para>
/// This type has no serialization, persistence, or replay-execution behaviour by design.
/// It exists only to represent and compare compatibility identity.
/// </para>
/// </summary>
public sealed class ReplayCompatibilityIdentity : IEquatable<ReplayCompatibilityIdentity>
{
    private readonly SourceBaselineId[] _sourceBaselines;

    /// <summary>The implemented ruleset revision in force.</summary>
    public RulesetVersion Ruleset { get; }

    /// <summary>The shape of the recorded replay.</summary>
    public ReplaySchemaVersion ReplaySchema { get; }

    /// <summary>The pinned corpora, in declaration order. Never empty.</summary>
    public IReadOnlyList<SourceBaselineId> SourceBaselines => _sourceBaselines;

    /// <summary>
    /// The pseudorandom algorithm this engine consumes, or <see langword="null"/> when it
    /// consumes none. See the type documentation.
    /// </summary>
    public RandomAlgorithmId? RandomAlgorithm { get; }

    /// <exception cref="ArgumentException">
    /// <paramref name="ruleset"/> is the struct default, <paramref name="sourceBaselines"/>
    /// is empty or contains a default entry, or <paramref name="randomAlgorithm"/> is
    /// present but is the struct default.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="sourceBaselines"/> is null.</exception>
    public ReplayCompatibilityIdentity(
        RulesetVersion ruleset,
        ReplaySchemaVersion replaySchema,
        IEnumerable<SourceBaselineId> sourceBaselines,
        RandomAlgorithmId? randomAlgorithm = null)
    {
        ArgumentNullException.ThrowIfNull(sourceBaselines);

        if (!ruleset.IsValid)
        {
            throw new ArgumentException(
                "ruleset is default(RulesetVersion), which bypasses its constructor and carries "
                + "no id.",
                nameof(ruleset));
        }

        var baselines = sourceBaselines.ToArray();
        if (baselines.Length == 0)
        {
            throw new ArgumentException(
                "an engine must pin at least one corpus; an identity with no source baseline "
                + "cannot establish what the rules were.",
                nameof(sourceBaselines));
        }

        for (int i = 0; i < baselines.Length; i++)
        {
            if (!baselines[i].IsValid)
            {
                throw new ArgumentException(
                    $"sourceBaselines[{i}] is default(SourceBaselineId), which bypasses its "
                    + "constructor.",
                    nameof(sourceBaselines));
            }
        }

        if (randomAlgorithm is { } algorithm && !algorithm.IsValid)
        {
            throw new ArgumentException(
                "randomAlgorithm is default(RandomAlgorithmId); pass null to declare that this "
                + "engine consumes no randomness, rather than an unnamed algorithm.",
                nameof(randomAlgorithm));
        }

        Ruleset = ruleset;
        ReplaySchema = replaySchema;
        _sourceBaselines = baselines;
        RandomAlgorithm = randomAlgorithm;
    }

    /// <summary>True when this engine resolves outcomes without consuming any randomness.</summary>
    public bool IsDeterministicWithoutRandomness => RandomAlgorithm is null;

    /// <inheritdoc/>
    public bool Equals(ReplayCompatibilityIdentity? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return Ruleset == other.Ruleset
            && ReplaySchema == other.ReplaySchema
            && Nullable.Equals(RandomAlgorithm, other.RandomAlgorithm)
            && _sourceBaselines.AsSpan().SequenceEqual(other._sourceBaselines);
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as ReplayCompatibilityIdentity);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Ruleset);
        hash.Add(ReplaySchema);
        hash.Add(RandomAlgorithm);
        foreach (var baseline in _sourceBaselines)
        {
            hash.Add(baseline);
        }

        return hash.ToHashCode();
    }

    /// <summary>Equality by value; see <see cref="Equals(ReplayCompatibilityIdentity?)"/>.</summary>
    public static bool operator ==(ReplayCompatibilityIdentity? left, ReplayCompatibilityIdentity? right) =>
        left is null ? right is null : left.Equals(right);

    /// <summary>Inequality by value.</summary>
    public static bool operator !=(ReplayCompatibilityIdentity? left, ReplayCompatibilityIdentity? right) =>
        !(left == right);
}
