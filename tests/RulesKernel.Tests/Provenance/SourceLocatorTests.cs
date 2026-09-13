using System;
using RulesKernel.Provenance;

namespace RulesKernel.Tests.Provenance;

public sealed class SourceLocatorTests
{
    [Theory]
    [InlineData("core-rules", "printed p. 45 / PDF p. 57")]
    [InlineData("cfr-26", "§ 1.401(k)-1(b)(4)(ii)")]
    [InlineData("boardgame", "rule 4.2.1")]
    [InlineData("usc-title-11", "11 U.S.C. § 362(a)(3)")]
    public void Accepts_any_citation_grammar_its_corpus_declares(string sourceId, string citation)
    {
        var locator = new SourceLocator(sourceId, citation);

        Assert.Equal(sourceId, locator.SourceId);
        Assert.Equal(citation, locator.Citation);
        Assert.True(locator.IsValid);
    }

    [Fact]
    public void Requires_both_a_corpus_and_a_citation()
    {
        Assert.Throws<ArgumentException>(() => new SourceLocator(" ", "p. 1"));
        Assert.Throws<ArgumentException>(() => new SourceLocator("core", " "));
    }

    [Fact]
    public void Default_is_reported_invalid_rather_than_throwing()
    {
        Assert.False(default(SourceLocator).IsValid);
    }
}
