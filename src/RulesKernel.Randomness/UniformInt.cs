namespace RulesKernel.Randomness;

/// <summary>
/// Exactly uniform bounded draws over an <see cref="IRandomSource"/>, by rejection
/// sampling rather than modulo.
///
/// <para>
/// <b>Why not modulo.</b> 2^32 is not a multiple of most bounds -- for a bound of six,
/// 2^32 = 4,294,967,296 = 6 x 715,827,882 + 4 -- so a plain <c>value % bound</c> mapping is
/// not uniform: four of the six outcomes would receive one extra raw value each. For small
/// bounds that skew is around 2.3e-10 and distorts nothing anyone will ever roll. Rejection
/// sampling is used anyway, on the stronger ground that actually holds: it is exactly
/// uniform rather than uniform up to a discrepancy someone has to argue is small enough,
/// and it costs almost nothing. The skew is not small for large bounds, and a kernel cannot
/// know which bounds its consumers will choose.
/// </para>
///
/// <para>
/// This type is deliberately vocabulary-free. It says "uniform integer below a bound", not
/// "die" or "dice pool": those are tabletop words, and an engine over a statute draws
/// nothing at all. Dice vocabulary belongs in a domain pack layered above this
/// (docs/decisions/0002).
/// </para>
/// </summary>
public static class UniformInt
{
    /// <summary>
    /// Draws a uniform value in <c>[0, exclusiveBound)</c>.
    ///
    /// <para>
    /// Draws from <paramref name="source"/> until it yields a value below the largest
    /// multiple of <paramref name="exclusiveBound"/> that fits in a <c>uint</c>, then maps
    /// it down. A call almost always consumes exactly one draw; it consumes more only when
    /// a rejected raw value appears. The exact number of draws is determined entirely by
    /// the source's own sequence, which is what keeps it exactly reproducible under replay
    /// rather than merely usually reproducible -- so draw count is part of an engine's
    /// observable deterministic contract, and worth asserting in tests.
    /// </para>
    ///
    /// <para>
    /// The loop terminates provided <paramref name="source"/> eventually yields an accepted
    /// value -- exactly what <see cref="IRandomSource"/>'s distribution contract entitles a
    /// consumer to assume. No retry cap is imposed deliberately: a source that never yields
    /// an accepted value is a broken source, and a cap would misreport that as a failed
    /// draw instead. A source unable to honour the contract is expected to fail loudly --
    /// <c>FixedSequenceRandomSource</c> (tests/RulesKernel.Testing) throws on exhaustion
    /// rather than looping, so a test scripting only rejected values fails visibly before
    /// this method could hang.
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="exclusiveBound"/> is zero.</exception>
    public static uint Below(IRandomSource source, uint exclusiveBound)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfZero(exclusiveBound);

        // The largest multiple of exclusiveBound not exceeding 2^32, the true size of the
        // draw space. Raw values at or above it are rejected and redrawn.
        //
        // Computed in ulong on purpose. An earlier version worked against uint.MaxValue
        // (2^32 - 1) and justified it with the claim that 2^32 is not a multiple of any
        // bound above 1. That claim is false -- every power of two divides 2^32 -- and the
        // cost was real: at a bound of 2^31 the whole draw space maps perfectly with no
        // rejection at all, yet that formula rejected half of every draw. The output stayed
        // uniform, so no distribution was ever wrong; what was wrong was the number of draws
        // consumed, and draw count is part of the replay contract (docs/decisions/0006).
        //
        // The limit reaches 2^32 exactly when exclusiveBound divides it, and 2^32 does not
        // fit in a uint -- which is precisely why this is not narrowed back down. No uint can
        // then equal or exceed the limit, so nothing is rejected, which is the correct answer.
        //
        // The comparison must be >=, not >, and the subtraction must be of the remainder,
        // not of exclusiveBound itself. Either slip reintroduces exactly the bias this
        // method exists to remove, while looking correct in every ordinary run.
        const ulong DrawSpace = 1UL << 32;
        ulong acceptanceLimit = DrawSpace - (DrawSpace % exclusiveBound);

        uint raw;
        do
        {
            raw = source.NextUInt32();
        }
        while (raw >= acceptanceLimit);

        return raw % exclusiveBound;
    }

    /// <summary>
    /// Draws a uniform value in <c>[minInclusive, maxInclusive]</c>.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="maxInclusive"/> is less than <paramref name="minInclusive"/>.
    /// </exception>
    public static int InRange(IRandomSource source, int minInclusive, int maxInclusive)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxInclusive, minInclusive);

        // Widened to long before subtracting: int.MaxValue - int.MinValue overflows int,
        // and the full-range case is legal input.
        long span = (long)maxInclusive - minInclusive + 1;

        if (span == 1)
        {
            // A single-value window is already determined; consuming a draw for it would
            // make the draw count depend on a window size that cannot affect the result,
            // and draw count is part of the deterministic contract.
            return minInclusive;
        }

        if (span == 1L << 32)
        {
            // The window is the entire 32-bit range, so every raw value maps and nothing
            // can be rejected. It must be special-cased because the bound 2^32 does not
            // fit in a uint: narrowing it would silently produce 0 and be rejected by
            // Below as a zero bound. Unchecked because the offset intentionally wraps
            // through the unsigned range before landing back in int.
            return unchecked((int)(source.NextUInt32() + (uint)minInclusive));
        }

        return (int)(minInclusive + (long)Below(source, (uint)span));
    }
}
