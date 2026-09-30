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
/// <b>What this type enforces, and what it does not.</b> It cannot make an engine
/// non-guessing: whether an operation returns this union or a bare value is the engine's
/// claim, and a bare value for an unimplemented rule is invisible here. What it enforces
/// begins once an operation has declared itself potentially non-total by returning it:
/// no member yields the value without the caller naming the unresolved case. A handler that
/// names it and returns a default anyway is still possible; the difference is that the
/// guess is now written at a call site rather than implied by a return type
/// (docs/decisions/0018).
/// </para>
///
/// <para>
/// <b>A resolved value carries no citation here.</b> An unresolved result must cite where the
/// rule lives; a resolved one cites through its own value. An engine that cites its answers
/// puts <see cref="Provenance.SourceLocator"/>s in <typeparamref name="T"/>, in whatever shape
/// its rules need -- one authority, an ordered list of passages applied, a map entry. Real
/// engines use all three, which is why the shape is theirs (docs/decisions/0022).
/// </para>
///
/// <para>
/// The hierarchy is closed -- the private constructor and a guarded copy constructor mean
/// <see cref="Resolved"/> and <see cref="Unresolved"/> are the only cases that can ever
/// exist, so <see cref="Match{TResult}"/> and a <c>switch</c> over it are exhaustive by
/// construction. A third case can be declared but not constructed
/// (docs/decisions/0023).
/// </para>
/// </summary>
/// <typeparam name="T">The resolved value's type.</typeparam>
public abstract record Resolution<T>
{
    private Resolution()
    {
    }

    /// <summary>
    /// Declared so the compiler does not generate an unguarded one: a record that is not
    /// sealed gets a protected copy constructor, and a derived record could chain to it,
    /// making a third case that <see cref="Match{TResult}"/> reports as unreachable. The
    /// only legitimate callers are <see cref="Resolved"/> and <see cref="Unresolved"/>,
    /// through <c>with</c>.
    /// </summary>
    /// <param name="original">The instance being copied.</param>
    /// <exception cref="ArgumentNullException"><paramref name="original"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// The instance being constructed is neither <see cref="Resolved"/> nor
    /// <see cref="Unresolved"/>.
    /// </exception>
    protected Resolution(Resolution<T> original)
    {
        ArgumentNullException.ThrowIfNull(original);

        if (this is not (Resolved or Unresolved))
        {
            throw new InvalidOperationException(
                "Resolution<T> is a closed hierarchy whose only cases are Resolved and Unresolved "
                + "(docs/decisions/0023).");
        }
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

        /// <summary>
        /// Supports positional matching -- <c>case Resolution&lt;T&gt;.Resolved(var value)</c>.
        /// Restored explicitly: rewriting this from a positional record to a bodied one to
        /// make the constructor internal silently removed the compiler-generated
        /// deconstructor, which would have been a source break for callers doing exactly what
        /// this type's documentation tells them to do.
        /// </summary>
        public void Deconstruct(out T value) => value = Value;
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

        /// <summary>Supports positional matching; see <see cref="Resolved.Deconstruct"/>.</summary>
        public void Deconstruct(out UnresolvedResult result) => result = Result;
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
    /// Exhaustively handles both cases. Forcing a caller to supply both is the point: an
    /// unresolved outcome cannot be dropped without a handler that says so. It can still be
    /// dropped deliberately -- <c>_ =&gt; 0</c> -- and nothing here prevents that; it only
    /// makes the choice visible where it is made (docs/decisions/0018).
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
