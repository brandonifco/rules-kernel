using System;
using RulesKernel.Identity;
using RulesKernel.Provenance;
using RulesKernel.Resolution;

namespace RulesKernel.Documentation.Tests;

/// <summary>
/// The C# blocks in README.md, each between <c>// sample: NAME</c> and <c>// end sample</c>.
/// A region is copied into the document verbatim, after removing its common indentation;
/// tools/repo-checks.py --only doc-samples compares the two. The assertions are what the
/// prose around each block says about it, so a sample that compiles and means something
/// else fails too.
/// </summary>
public sealed class ReadmeSamples
{
    // Fabricated digests. The samples name them and never show them.
    private const string regulationHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string noticeHash = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";

    [Fact]
    public void Identity()
    {
        // sample: identity
        var identity = new ReplayCompatibilityIdentity(
            ruleset: new RulesetVersion("cfr-26-401k", 3),
            replaySchema: new ReplaySchemaVersion(1),
            sourceBaselines:
            [
                new SourceBaselineId("cfr-26", regulationHash, "extracted-xml", asOf: new DateOnly(2019, 3, 14)),
                new SourceBaselineId("rev-proc-2019-20", noticeHash, "pdf-bytes", asOf: new DateOnly(2019, 5, 1)),
            ]);

        bool noGenerator = identity.IsDeterministicWithoutRandomness;  // true -- no generator involved
        // end sample

        Assert.True(noGenerator);
        Assert.Equal(2, identity.SourceBaselines.Length);
        Assert.Equal("extracted-xml", identity.SourceBaselines[0].HashDerivation);
        Assert.Equal(new DateOnly(2019, 5, 1), identity.SourceBaselines[1].AsOf);
    }

    [Fact]
    public void Provenance()
    {
        // sample: provenance
        var page = new SourceLocator("core-rules", "printed p. 45 / PDF p. 57");
        var designation = new SourceLocator("cfr-26", "§ 1.401(k)-1(b)(4)(ii)");
        var numberedRule = new SourceLocator("boardgame", "rule 4.2.1");
        // end sample

        // Verbatim: the kernel neither parses nor normalizes a citation.
        Assert.Equal("printed p. 45 / PDF p. 57", page.Citation);
        Assert.Equal("cfr-26 / § 1.401(k)-1(b)(4)(ii)", designation.ToString());
        Assert.Equal("boardgame", numberedRule.SourceId);
    }

    [Fact]
    public void Resolution()
    {
        var resolution = ElectiveDeferralLimitForAMidYearAmendment();

        Assert.False(resolution.IsResolved);
        var reason = resolution.Match(_ => (UnresolvedReason?)null, gap => gap.Reason);
        Assert.Equal(UnresolvedReason.RequiresInterpretation, reason);
    }

    private static Resolution<int> ElectiveDeferralLimitForAMidYearAmendment()
    {
        // sample: resolution
        return Resolution<int>.FromUnresolved(new UnresolvedResult(
            UnresolvedReason.RequiresInterpretation,
            "resolve the elective deferral limit for a mid-year plan amendment",
            new SourceLocator("cfr-26", "§ 1.401(k)-1(b)(4)(ii)")));
        // end sample
    }
}
