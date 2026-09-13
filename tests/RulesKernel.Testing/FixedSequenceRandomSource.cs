using System.Collections.Generic;
using System.Linq;
using RulesKernel.Randomness;

namespace RulesKernel.Testing;

/// <summary>
/// A scripted <see cref="IRandomSource"/> that replays a fixed sequence of values in
/// order, for tests that need to control exactly what a mechanic draws.
///
/// <para>
/// Lives in <c>RulesKernel.Testing</c>, under <c>tests/</c> rather than in a <c>src/</c>
/// assembly: it is test-support, not a layer of the engine, and
/// <c>tools/repo-checks.py --only layering</c> forbids any <c>src/</c> project from
/// referencing it. It is packaged all the same, because an engine built on this kernel
/// needs the same scripted source in its own tests (docs/decisions/0001).
/// </para>
///
/// <para>
/// This shape -- a scripted source that throws on exhaustion rather than falling back to
/// real randomness -- is the one piece of this kernel that was arrived at twice
/// independently, in two separate engines, essentially unchanged. A test that draws more
/// values than it accounted for is a test whose premise has changed, and silently
/// continuing would hide that.
/// </para>
/// </summary>
public sealed class FixedSequenceRandomSource : IRandomSource
{
    private readonly IReadOnlyList<uint> _values;
    private int _consumed;

    /// <summary>An empty sequence is allowed to construct; it fails on the first draw, like any other exhaustion.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="values"/> is null.</exception>
    public FixedSequenceRandomSource(IEnumerable<uint> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        _values = values.ToArray();
    }

    /// <summary>
    /// How many values have been drawn so far. Exposed because the number of draws a
    /// mechanic makes is part of its observable contract under docs/decisions/0005 -- a test needs
    /// to be able to assert on it, not just on the values themselves.
    /// </summary>
    public int Consumed => _consumed;

    /// <exception cref="InvalidOperationException">
    /// The supplied sequence is already exhausted. Never wraps, repeats the last value,
    /// or returns zero -- a caller drawing more values than a test scripted is a
    /// programmer error, and any of those fallbacks would silently hide it instead.
    /// </exception>
    public uint NextUInt32()
    {
        if (_consumed >= _values.Count)
        {
            throw new InvalidOperationException(
                $"FixedSequenceRandomSource was given {_values.Count} value(s) and was asked for one more.");
        }

        return _values[_consumed++];
    }
}
