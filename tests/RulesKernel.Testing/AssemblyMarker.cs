using System.Reflection;

namespace RulesKernel.Testing;

/// <summary>Assembly marker for <c>RulesKernel.Testing</c>; see the architecture tests.</summary>
public static class AssemblyMarker
{
    /// <summary>The <c>RulesKernel.Testing</c> assembly.</summary>
    public static Assembly Assembly => typeof(AssemblyMarker).Assembly;
}
