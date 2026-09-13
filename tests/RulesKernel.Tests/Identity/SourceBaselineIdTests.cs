using System;
using RulesKernel.Identity;
using RulesKernel.Provenance;

namespace RulesKernel.Tests.Identity;

public sealed class SourceBaselineIdTests
{
    private const string Derivation = "pdf-bytes";
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void Rejects_a_hash_that_is_not_sixty_four_hex_characters()
    {
        Assert.Throws<ArgumentException>(() => new SourceBaselineId("core", "abc", Derivation));
        Assert.Throws<ArgumentException>(() => new SourceBaselineId("core", new string('z', 64), Derivation));
    }

    [Fact]
    public void Normalizes_hash_case_so_the_same_content_compares_equal()
    {
        var lower = new SourceBaselineId("core", Hash, Derivation);
        var upper = new SourceBaselineId("core", Hash.ToUpperInvariant(), Derivation);

        Assert.Equal(lower, upper);
        Assert.Equal(lower.GetHashCode(), upper.GetHashCode());
        Assert.Equal(Hash, upper.ContentHash);
    }

    [Fact]
    public void A_corpus_pinned_at_two_different_moments_is_two_baselines()
    {
        var earlier = new SourceBaselineId("cfr-26", Hash, Derivation, new DateOnly(2019, 3, 14));
        var later = new SourceBaselineId("cfr-26", Hash, Derivation, new DateOnly(2024, 1, 1));

        Assert.NotEqual(earlier, later);
    }

    [Fact]
    public void A_timeless_corpus_is_distinct_from_the_same_content_pinned_to_a_date()
    {
        Assert.NotEqual(
            new SourceBaselineId("core", Hash, Derivation),
            new SourceBaselineId("core", Hash, Derivation, new DateOnly(2024, 1, 1)));
    }

    /// <summary>
    /// A stray space makes two references to the same corpus compare unequal, which would
    /// silently defeat the duplicate-baseline check whose entire rationale is that an id
    /// identifies a corpus. Case is deliberately left alone -- see the guard's own doc.
    /// </summary>
    [Theory]
    [InlineData("core ")]
    [InlineData(" core")]
    [InlineData("core\t")]
    public void A_source_id_padded_with_whitespace_is_rejected(string padded)
    {
        Assert.Throws<ArgumentException>(() => new SourceBaselineId(padded, Hash, Derivation));
        Assert.Throws<ArgumentException>(() => new SourceLocator(padded, "p. 1"));
    }

    /// <summary>
    /// The gap this field closes. SRD_Combat hashes the sorted roster of content ids and
    /// says so in its own comment; another engine over the same corpus would hash the PDF
    /// bytes. Both are legitimate. Without a derivation both publish the same shape of
    /// identity while pinning different things, and the identity cannot tell you.
    /// </summary>
    [Fact]
    public void The_same_corpus_hashed_over_different_things_is_two_baselines()
    {
        Assert.NotEqual(
            new SourceBaselineId("srd-5.2.1", Hash, "pdf-bytes"),
            new SourceBaselineId("srd-5.2.1", Hash, "id-roster"));
    }

    [Fact]
    public void A_baseline_must_say_what_its_hash_was_computed_over()
    {
        Assert.Throws<ArgumentException>(() => new SourceBaselineId("core", Hash, " "));
        Assert.False(default(SourceBaselineId).IsValid);
    }

    [Fact]
    public void Corpus_ids_differing_only_in_case_remain_distinct()
    {
        // Stated, not softened: the kernel has no basis for deciding whether a corpus
        // scheme is case-sensitive, so it does not guess.
        Assert.NotEqual(new SourceBaselineId("Core", Hash, Derivation), new SourceBaselineId("core", Hash, Derivation));
    }

    [Fact]
    public void Default_is_reported_invalid_rather_than_throwing()
    {
        Assert.False(default(SourceBaselineId).IsValid);
        Assert.True(new SourceBaselineId("core", Hash, Derivation).IsValid);
    }

    [Fact]
    public void ToString_shows_the_date_only_when_one_is_pinned()
    {
        Assert.Equal(
            $"core#{Derivation}:{Hash}",
            new SourceBaselineId("core", Hash, Derivation).ToString());
        Assert.Equal(
            $"core@2019-03-14#{Derivation}:{Hash}",
            new SourceBaselineId("core", Hash, Derivation, new DateOnly(2019, 3, 14)).ToString());
    }
}
