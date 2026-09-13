using System;
using RulesKernel.Identity;

namespace RulesKernel.Tests.Identity;

public sealed class VersionAndAlgorithmTests
{
    [Fact]
    public void RulesetVersion_requires_an_id_and_a_non_negative_version()
    {
        Assert.Throws<ArgumentException>(() => new RulesetVersion(" ", 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RulesetVersion("example", -1));
        Assert.False(default(RulesetVersion).IsValid);
        Assert.True(new RulesetVersion("example", 0).IsValid);
    }

    [Fact]
    public void ReplaySchemaVersion_default_equals_version_zero()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReplaySchemaVersion(-1));
        Assert.Equal(new ReplaySchemaVersion(0), default(ReplaySchemaVersion));
    }

    [Fact]
    public void RandomAlgorithmId_names_the_algorithm_the_randomness_package_provides()
    {
        Assert.Equal("pcg_setseq_64_xsh_rr_32", RandomAlgorithmId.Pcg32SetSeq64XshRr32.Name);
        Assert.Throws<ArgumentException>(() => new RandomAlgorithmId(" "));
        Assert.False(default(RandomAlgorithmId).IsValid);
    }
}
