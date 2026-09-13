namespace RulesKernel.Identity;

/// <summary>
/// Identifies which ruleset -- and which revision of its implemented mechanics -- was in
/// force when a sequence of decisions was recorded. Distinct from
/// <see cref="SourceBaselineId"/>: the source baseline identifies the pinned corpus, while
/// this identifies the implemented revision, which changes independently as mechanics are
/// added, corrected, or reinterpreted.
///
/// <para>
/// <c>default(RulesetVersion)</c> bypasses the constructor and yields a
/// <see langword="null"/> <see cref="Id"/> paired with a valid-looking <c>Version</c> of
/// <c>0</c>. <see cref="IsValid"/> distinguishes a constructed value from that default;
/// <see cref="ReplayCompatibilityIdentity"/> rejects the default rather than comparing it.
/// </para>
/// </summary>
public readonly record struct RulesetVersion
{
    /// <summary>
    /// Which ruleset this is. Kept distinct from <see cref="Version"/> so different
    /// rulesets can be told apart from revisions of the same ruleset.
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// This ruleset's implementation revision. Bumping it is routine engineering, expected
    /// whenever a change could alter how a recorded decision resolves.
    /// </summary>
    public int Version { get; }

    /// <exception cref="ArgumentException"><paramref name="id"/> is null, empty, or whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="version"/> is negative.</exception>
    public RulesetVersion(string id, int version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentOutOfRangeException.ThrowIfNegative(version);
        Id = id;
        Version = version;
    }

    /// <summary>False for <c>default(RulesetVersion)</c>, which bypasses the constructor.</summary>
    public bool IsValid => !string.IsNullOrWhiteSpace(Id);
}
