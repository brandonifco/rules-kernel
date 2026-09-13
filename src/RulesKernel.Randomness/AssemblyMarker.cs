using System.Reflection;

namespace RulesKernel.Randomness;

/// <summary>
/// Assembly marker for <c>RulesKernel.Randomness</c>. Exists so the architecture tests
/// can assert this assembly references <c>RulesKernel</c> and nothing above it.
/// </summary>
public static class AssemblyMarker
{
    /// <summary>The <c>RulesKernel.Randomness</c> assembly.</summary>
    public static Assembly Assembly => typeof(AssemblyMarker).Assembly;
}
