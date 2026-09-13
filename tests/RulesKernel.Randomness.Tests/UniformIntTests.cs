using System;
using System.Collections.Generic;
using RulesKernel.Randomness;
using RulesKernel.Testing;

namespace RulesKernel.Randomness.Tests;

public sealed class UniformIntTests
{
    [Fact]
    public void Below_rejects_a_zero_bound()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => UniformInt.Below(new FixedSequenceRandomSource([0u]), 0));
    }

    [Fact]
    public void Below_rejects_a_null_source()
    {
        Assert.Throws<ArgumentNullException>(() => UniformInt.Below(null!, 6));
        Assert.Throws<ArgumentNullException>(() => UniformInt.InRange(null!, 1, 6));
    }

    [Fact]
    public void Below_maps_an_accepted_draw_with_a_single_consumption()
    {
        var source = new FixedSequenceRandomSource([13u, 99u]);

        Assert.Equal(1u, UniformInt.Below(source, 6));
        Assert.Equal(1, source.Consumed);
    }

    /// <summary>
    /// The four raw values at or above the acceptance limit for a bound of six are exactly
    /// the ones rejection sampling exists to discard. Scripting one proves the redraw
    /// happens and that the extra consumption is observable -- draw count is part of the
    /// deterministic contract, not an implementation detail.
    /// </summary>
    [Fact]
    public void Below_redraws_past_a_rejected_value_and_the_extra_draw_is_observable()
    {
        const uint AcceptanceLimit = uint.MaxValue - (uint.MaxValue % 6);
        var source = new FixedSequenceRandomSource([AcceptanceLimit, AcceptanceLimit + 1, 4u]);

        Assert.Equal(4u, UniformInt.Below(source, 6));
        Assert.Equal(3, source.Consumed);
    }

    [Fact]
    public void Below_accepts_the_value_immediately_under_the_acceptance_limit()
    {
        const uint AcceptanceLimit = uint.MaxValue - (uint.MaxValue % 6);
        var source = new FixedSequenceRandomSource([AcceptanceLimit - 1]);

        Assert.Equal((AcceptanceLimit - 1) % 6, UniformInt.Below(source, 6));
        Assert.Equal(1, source.Consumed);
    }

    /// <summary>
    /// A source that only ever yields rejected values is a broken source, not a failed
    /// draw. It must surface as the scripted source running dry rather than as a hang --
    /// which is precisely why no retry cap is imposed.
    /// </summary>
    [Fact]
    public void Below_fails_loudly_rather_than_looping_on_a_source_that_only_rejects()
    {
        const uint AcceptanceLimit = uint.MaxValue - (uint.MaxValue % 6);
        var source = new FixedSequenceRandomSource([AcceptanceLimit, AcceptanceLimit + 2]);

        Assert.Throws<InvalidOperationException>(() => UniformInt.Below(source, 6));
    }

    /// <summary>
    /// The expectation is derived from the definition of the acceptance limit, never from
    /// the expression the implementation uses. The defect docs/decisions/0006 corrects
    /// survived because the tests recomputed the implementation's own formula, which can
    /// only ever confirm that the code does what it does.
    /// </summary>
    private static ulong AcceptanceLimit(uint bound)
    {
        const ulong DrawSpace = 1UL << 32;
        return DrawSpace - (DrawSpace % bound);
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(6u)]
    [InlineData(20u)]
    [InlineData(1u << 16)]
    [InlineData(1u << 31)]
    [InlineData(uint.MaxValue)]
    public void The_highest_acceptable_raw_value_is_accepted_in_a_single_draw(uint bound)
    {
        uint highestAccepted = (uint)(AcceptanceLimit(bound) - 1);
        var source = new FixedSequenceRandomSource([highestAccepted]);

        Assert.Equal(highestAccepted % bound, UniformInt.Below(source, bound));
        Assert.Equal(1, source.Consumed);
    }

    /// <summary>
    /// A bound that divides 2^32 maps the entire draw space perfectly, so nothing can be
    /// rejected. The superseded formula rejected half of every draw at 2^31 and one value
    /// in every 2^32 at a bound of 1 -- uniform output, wrong draw count.
    /// </summary>
    [Theory]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(1u << 16)]
    [InlineData(1u << 31)]
    public void A_bound_dividing_the_draw_space_never_rejects_anything(uint bound)
    {
        Assert.Equal(1UL << 32, AcceptanceLimit(bound));

        var source = new FixedSequenceRandomSource([uint.MaxValue]);

        Assert.Equal(uint.MaxValue % bound, UniformInt.Below(source, bound));
        Assert.Equal(1, source.Consumed);
    }

    [Theory]
    [InlineData(6u)]
    [InlineData(20u)]
    [InlineData(uint.MaxValue)]
    public void The_lowest_unacceptable_raw_value_is_rejected_and_redrawn(uint bound)
    {
        ulong limit = AcceptanceLimit(bound);
        Assert.True(limit < (1UL << 32), "this case only applies to a bound that leaves a remainder");

        var source = new FixedSequenceRandomSource([(uint)limit, 0u]);

        Assert.Equal(0u, UniformInt.Below(source, bound));
        Assert.Equal(2, source.Consumed);
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(6u)]
    [InlineData(1u << 31)]
    [InlineData(uint.MaxValue)]
    public void Every_outcome_receives_the_same_number_of_raw_values(uint bound)
    {
        Assert.Equal(0UL, AcceptanceLimit(bound) % bound);
    }

    [Fact]
    public void Below_with_a_bound_of_one_always_yields_zero()
    {
        var source = new FixedSequenceRandomSource([0u, uint.MaxValue - 1, 12345u]);

        Assert.Equal(0u, UniformInt.Below(source, 1));
        Assert.Equal(0u, UniformInt.Below(source, 1));
        Assert.Equal(0u, UniformInt.Below(source, 1));
    }

    [Fact]
    public void InRange_offsets_into_the_requested_window()
    {
        var source = new FixedSequenceRandomSource([0u, 5u]);

        Assert.Equal(1, UniformInt.InRange(source, 1, 6));
        Assert.Equal(6, UniformInt.InRange(source, 1, 6));
    }

    [Fact]
    public void InRange_with_a_single_value_window_consumes_nothing()
    {
        var source = new FixedSequenceRandomSource([]);

        Assert.Equal(4, UniformInt.InRange(source, 4, 4));
        Assert.Equal(0, source.Consumed);
    }

    [Fact]
    public void InRange_rejects_an_inverted_window()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => UniformInt.InRange(new FixedSequenceRandomSource([0u]), 6, 1));
    }

    [Fact]
    public void InRange_spans_the_whole_int_range_without_overflowing()
    {
        var source = new FixedSequenceRandomSource([0u]);

        Assert.Equal(int.MinValue, UniformInt.InRange(source, int.MinValue, int.MaxValue));
    }

    [Fact]
    public void InRange_handles_negative_windows()
    {
        var source = new FixedSequenceRandomSource([0u, 4u]);

        Assert.Equal(-5, UniformInt.InRange(source, -5, -1));
        Assert.Equal(-1, UniformInt.InRange(source, -5, -1));
    }

    /// <summary>
    /// Draw order is preserved: the nth value the source yields determines the nth result.
    /// Every ordered result this kernel produces is ordered by construction, never sorted
    /// afterwards.
    /// </summary>
    [Fact]
    public void Successive_draws_follow_the_sources_own_order()
    {
        var source = new FixedSequenceRandomSource([0u, 1u, 2u, 3u]);
        var drawn = new List<uint>();

        for (int i = 0; i < 4; i++)
        {
            drawn.Add(UniformInt.Below(source, 6));
        }

        Assert.Equal(new uint[] { 0, 1, 2, 3 }, drawn);
    }

    [Fact]
    public void A_replay_from_the_same_state_reproduces_the_same_bounded_sequence()
    {
        var first = Pcg32.FromSeed(42, 54);
        var captured = first.GetState();

        uint[] original = [.. Draw(first, 8)];
        uint[] replayed = [.. Draw(Pcg32.FromState(captured), 8)];

        Assert.Equal(original, replayed);

        static IEnumerable<uint> Draw(Pcg32 source, int count)
        {
            for (int i = 0; i < count; i++)
            {
                yield return UniformInt.Below(source, 20);
            }
        }
    }
}
