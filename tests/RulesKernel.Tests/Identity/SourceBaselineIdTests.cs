using System;
using RulesKernel.Identity;

namespace RulesKernel.Tests.Identity;

public sealed class SourceBaselineIdTests
{
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void Rejects_a_hash_that_is_not_sixty_four_hex_characters()
    {
        Assert.Throws<ArgumentException>(() => new SourceBaselineId("core", "abc"));
        Assert.Throws<ArgumentException>(() => new SourceBaselineId("core", new string('z', 64)));
    }

    [Fact]
    public void Normalizes_hash_case_so_the_same_content_compares_equal()
    {
        var lower = new SourceBaselineId("core", Hash);
        var upper = new SourceBaselineId("core", Hash.ToUpperInvariant());

        Assert.Equal(lower, upper);
        Assert.Equal(lower.GetHashCode(), upper.GetHashCode());
        Assert.Equal(Hash, upper.ContentHash);
    }

    [Fact]
    public void A_corpus_pinned_at_two_different_moments_is_two_baselines()
    {
        var earlier = new SourceBaselineId("cfr-26", Hash, new DateOnly(2019, 3, 14));
        var later = new SourceBaselineId("cfr-26", Hash, new DateOnly(2024, 1, 1));

        Assert.NotEqual(earlier, later);
    }

    [Fact]
    public void A_timeless_corpus_is_distinct_from_the_same_content_pinned_to_a_date()
    {
        Assert.NotEqual(
            new SourceBaselineId("core", Hash),
            new SourceBaselineId("core", Hash, new DateOnly(2024, 1, 1)));
    }

    [Fact]
    public void Default_is_reported_invalid_rather_than_throwing()
    {
        Assert.False(default(SourceBaselineId).IsValid);
        Assert.True(new SourceBaselineId("core", Hash).IsValid);
    }

    [Fact]
    public void ToString_shows_the_date_only_when_one_is_pinned()
    {
        Assert.Equal($"core#{Hash}", new SourceBaselineId("core", Hash).ToString());
        Assert.Equal(
            $"core@2019-03-14#{Hash}",
            new SourceBaselineId("core", Hash, new DateOnly(2019, 3, 14)).ToString());
    }
}
