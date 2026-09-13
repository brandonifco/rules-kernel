using System;
using System.Linq;

namespace RulesKernel.Tests;

/// <summary>
/// Mechanical enforcement of the layering in docs/architecture.md. <c>RulesKernel</c> is
/// the floor: it may reference no other assembly in this repository at all.
///
/// <para>
/// This assertion is one-directional on purpose. The C# compiler omits assembly references
/// a compilation does not actually use, so "RulesKernel references X" cannot be asserted
/// until code here consumes X. What can always be asserted -- and what matters -- is that
/// nothing leaks upward. The declared <c>ProjectReference</c> graph is checked separately
/// and exactly by <c>tools/repo-checks.py --only layering</c>, which reads the csproj files
/// rather than compiled output, and that check is the load-bearing one.
/// </para>
/// </summary>
public sealed class ArchitectureTests
{
    [Fact]
    public void Kernel_references_no_other_RulesKernel_assembly()
    {
        string[] referenced = AssemblyMarker.Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(n => n.StartsWith("RulesKernel", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(referenced);
    }
}
