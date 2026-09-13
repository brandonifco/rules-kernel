namespace RulesKernel.Resolution;

/// <summary>
/// The outcome of an engine operation: either a resolved value or an explicit
/// <see cref="UnresolvedResult"/>.
///
/// <para>
/// <b>Totality determines the union, not universality.</b> An operation <em>may</em>
/// return its value type directly when, for every valid input in its domain, the corpus
/// determines exactly one answer -- no unsupported branch, no out-of-scope branch, no
/// ambiguity awaiting a decision. An operation <em>must</em> return this union when any
/// input in its domain can reach a rule that is unsupported, out of scope, or ambiguous.
/// The burden sits on the operation returning a bare value: totality is the claim that
/// must be justified, and the union is the default. An implementer who cannot state why an
/// operation is total uses the union (docs/decisions/0004).
/// </para>
///
/// <para>
/// The hierarchy is closed -- the private constructor means <see cref="Resolved"/> and
/// <see cref="Unresolved"/> are the only cases that can ever exist, so
/// <see cref="Match{TResult}"/> and a <c>switch</c> over it are exhaustive by
/// construction.
/// </para>
/// </summary>
/// <typeparam name="T">The resolved value's type.</typeparam>
public abstract record Resolution<T>
{
    private Resolution()
    {
    }

    /// <summary>True when this is a <see cref="Resolved"/> outcome.</summary>
    public bool IsResolved => this is Resolved;

    /// <summary>
    /// A resolved outcome carrying the engine's answer. Constructed through
    /// <see cref="FromValue"/>; the case is public so callers can match on it.
    /// </summary>
    public sealed record Resolved : Resolution<T>
    {
        internal Resolved(T value) => Value = value;

        /// <summary>The answer.</summary>
        public T Value { get; }
    }

    /// <summary>
    /// An outcome the engine could not resolve, and why. Constructed through
    /// <see cref="FromUnresolved"/> -- which is what guarantees <see cref="Result"/> is never
    /// null. A public constructor here would be an unguarded second way in, and callers would
    /// find it.
    /// </summary>
    public sealed record Unresolved : Resolution<T>
    {
        internal Unresolved(UnresolvedResult result) => Result = result;

        /// <summary>The reason, what was attempted, and where the rule lives.</summary>
        public UnresolvedResult Result { get; }
    }

    /// <summary>Wraps a resolved value.</summary>
    public static Resolution<T> FromValue(T value) => new Resolved(value);

    /// <summary>Wraps an unresolved result.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="result"/> is null.</exception>
    public static Resolution<T> FromUnresolved(UnresolvedResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new Unresolved(result);
    }

    /// <summary>
    /// Exhaustively handles both cases. Forcing a caller to supply both is the point: it is
    /// what stops an unresolved outcome from being silently dropped at the call site, which
    /// would reintroduce the guess this type exists to prevent.
    /// </summary>
    /// <exception cref="ArgumentNullException">Either delegate is null.</exception>
    public TResult Match<TResult>(
        Func<T, TResult> onResolved,
        Func<UnresolvedResult, TResult> onUnresolved)
    {
        ArgumentNullException.ThrowIfNull(onResolved);
        ArgumentNullException.ThrowIfNull(onUnresolved);

        return this switch
        {
            Resolved resolved => onResolved(resolved.Value),
            Unresolved unresolved => onUnresolved(unresolved.Result),
            _ => throw new InvalidOperationException("unreachable: Resolution<T> is a closed hierarchy"),
        };
    }
}
