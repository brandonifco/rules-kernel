using System;
using System.Linq;

namespace RulesKernel.Randomness.Tests;

/// <summary>
/// <c>RulesKernel.Randomness</c> sits directly on the kernel and nothing else. See the
/// note in the kernel's own architecture tests on why this direction only.
/// </summary>
public sealed class ArchitectureTests
{
    private static readonly string[] Allowed = ["RulesKernel"];

    [Fact]
    public void Randomness_references_only_the_kernel()
    {
        string[] referenced = AssemblyMarker.Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(n => n.StartsWith("RulesKernel", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(referenced.Except(Allowed, StringComparer.Ordinal));
    }
}
