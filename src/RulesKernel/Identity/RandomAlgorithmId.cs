namespace RulesKernel.Identity;

/// <summary>
/// Names the pseudorandom algorithm a sequence of draws was produced by -- not its seed,
/// not its captured state, its algorithm. Two random sources that happen to produce
/// identical output today are still a replay-compatibility break the moment either is
/// replaced, because nothing about a sequence of values proves which generator produced
/// it.
///
/// <para>
/// This type lives in the kernel rather than in <c>RulesKernel.Randomness</c> on purpose:
/// it is part of replay <em>identity</em>, and an engine that draws no random values at
/// all still has to be able to say so. <see cref="ReplayCompatibilityIdentity"/> carries
/// it as an optional component, absent meaning "this engine consumes no randomness"
/// (docs/decisions/0002).
/// </para>
///
/// <para>
/// <c>default(RandomAlgorithmId)</c> bypasses the constructor and yields a
/// <see langword="null"/> <see cref="Name"/>. <see cref="IsValid"/> distinguishes it.
/// </para>
/// </summary>
public readonly record struct RandomAlgorithmId
{
    /// <summary>The algorithm's canonical name, exactly as its reference implementation names it.</summary>
    public string Name { get; }

    /// <exception cref="ArgumentException"><paramref name="name"/> is null, empty, or whitespace.</exception>
    public RandomAlgorithmId(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    /// <summary>False for <c>default(RandomAlgorithmId)</c>, which bypasses the constructor.</summary>
    public bool IsValid => !string.IsNullOrWhiteSpace(Name);

    /// <summary>
    /// The algorithm provided by <c>RulesKernel.Randomness</c>: PCG32, variant
    /// <c>pcg_setseq_64_xsh_rr_32</c>. Named here, in the identity layer, so a replay
    /// identity can reference it without the kernel depending on the implementation.
    /// </summary>
    public static readonly RandomAlgorithmId Pcg32SetSeq64XshRr32 = new("pcg_setseq_64_xsh_rr_32");

    /// <inheritdoc/>
    public override string ToString() => Name ?? "(invalid)";
}
