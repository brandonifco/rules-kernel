using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace RulesKernel.Tests;

/// <summary>
/// Mechanical enforcement of the layering in docs/architecture.md, read from the build's own
/// dependency graph. <c>RulesKernel</c> is the floor: it may depend on no other assembly in
/// this repository at all.
///
/// <para>
/// These tests used to ask the compiled assembly for its referenced assemblies, and passed
/// vacuously: the C# compiler omits a reference the code does not use, so a
/// <c>ProjectReference</c> nobody had called yet was invisible (issue #54). The
/// <c>.deps.json</c> the SDK writes beside this test assembly records every project in the
/// graph and what each declares, used or not. This is a second net under
/// <c>tools/repo-checks.py --only layering</c>, not a copy of it: that check reads project and
/// build files as text, and this one reads what MSBuild actually resolved, so a reference
/// either one misreads the other still sees.
/// </para>
/// </summary>
public sealed class ArchitectureTests
{
    [Fact]
    public void Kernel_depends_on_no_other_RulesKernel_project()
    {
        var graph = ProjectGraph.OfThisTestRun();

        Assert.True(graph.ContainsKey("RulesKernel"), "the kernel is not in this test run's dependency graph");
        Assert.Empty(graph["RulesKernel"]);
    }
}

/// <summary>The RulesKernel* projects in a test run's <c>.deps.json</c>, and what each depends on.</summary>
internal static class ProjectGraph
{
    public static IReadOnlyDictionary<string, string[]> OfThisTestRun()
    {
        string path = Path.Combine(AppContext.BaseDirectory, typeof(ProjectGraph).Assembly.GetName().Name + ".deps.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement target = document.RootElement.GetProperty("targets").EnumerateObject().Single().Value;

        var graph = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (JsonProperty library in target.EnumerateObject())
        {
            string name = library.Name.Split('/')[0];
            if (!name.StartsWith("RulesKernel", StringComparison.Ordinal))
            {
                continue;
            }

            graph[name] = library.Value.TryGetProperty("dependencies", out JsonElement dependencies)
                ? dependencies.EnumerateObject()
                    .Select(dependency => dependency.Name)
                    .Where(dependency => dependency.StartsWith("RulesKernel", StringComparison.Ordinal))
                    .ToArray()
                : [];
        }

        return graph;
    }
}
