using System.Reflection;

namespace RulesKernel;

/// <summary>
/// Assembly marker for <c>RulesKernel</c>, the floor of the dependency graph. Exists so
/// the architecture tests can reflect over this assembly and assert its dependency
/// direction. It carries no behaviour.
/// </summary>
public static class AssemblyMarker
{
    /// <summary>The <c>RulesKernel</c> assembly.</summary>
    public static Assembly Assembly => typeof(AssemblyMarker).Assembly;
}
