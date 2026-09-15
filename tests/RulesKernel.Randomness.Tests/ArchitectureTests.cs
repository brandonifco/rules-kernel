using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace RulesKernel.Randomness.Tests;

/// <summary>
/// <c>RulesKernel.Randomness</c> sits directly on the kernel and nothing else, and
/// <c>RulesKernel.Testing</c> directly on <c>RulesKernel.Randomness</c>. Read from this test
/// run's <c>.deps.json</c>, which records declared project dependencies whether or not the code
/// uses them yet; see the kernel's own <c>ArchitectureTests</c> for why (issue #54).
/// </summary>
public sealed class ArchitectureTests
{
    [Theory]
    [InlineData("RulesKernel", new string[0])]
    [InlineData("RulesKernel.Randomness", new[] { "RulesKernel" })]
    [InlineData("RulesKernel.Testing", new[] { "RulesKernel.Randomness" })]
    public void Each_layer_depends_only_on_the_layer_below(string project, string[] allowed)
    {
        var graph = OfThisTestRun();

        Assert.True(graph.ContainsKey(project), $"{project} is not in this test run's dependency graph");
        Assert.Empty(graph[project].Except(allowed, StringComparer.Ordinal));
    }

    private static Dictionary<string, string[]> OfThisTestRun()
    {
        string path = Path.Combine(AppContext.BaseDirectory, typeof(ArchitectureTests).Assembly.GetName().Name + ".deps.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement target = document.RootElement.GetProperty("targets").EnumerateObject().Single().Value;

        var graph = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (JsonProperty library in target.EnumerateObject())
        {
            string name = library.Name.Split('/')[0];
            if (name.StartsWith("RulesKernel", StringComparison.Ordinal))
            {
                graph[name] = library.Value.TryGetProperty("dependencies", out JsonElement dependencies)
                    ? dependencies.EnumerateObject()
                        .Select(dependency => dependency.Name)
                        .Where(dependency => dependency.StartsWith("RulesKernel", StringComparison.Ordinal))
                        .ToArray()
                    : [];
            }
        }

        return graph;
    }
}
