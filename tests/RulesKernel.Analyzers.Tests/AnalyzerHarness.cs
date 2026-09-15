using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RulesKernel.Analyzers.Tests;

/// Compiles a snippet against the running framework's reference set and returns the ids the
/// analyzer reported, ordered by source position so a test can assert on order.
internal static class AnalyzerHarness
{
    private static readonly ImmutableArray<MetadataReference> References = BuildReferences();

    internal static ImmutableArray<string> Diagnose(string source) =>
        Report(source).Select(d => d.Id).ToImmutableArray();

    /// Same, but each entry is "ID: rendered message". A diagnostic's *reason* is the part a
    /// consumer reads and acts on, and docs/decisions/0015 records a rule whose ids were all
    /// correct while one of its sentences was false, so the sentences are asserted too.
    internal static ImmutableArray<string> Describe(string source) =>
        Report(source)
            .Select(d => d.Id + ": " + d.GetMessage(CultureInfo.InvariantCulture))
            .ToImmutableArray();

    private static ImmutableArray<Diagnostic> Report(string source)
    {
        var compilation = CSharpCompilation.Create(
            "Consumer",
            new[] { CSharpSyntaxTree.ParseText(source) },
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        // A fixture that does not compile would make the analyzer's silence meaningless:
        // no operations means no operation actions means no diagnostics, and the test would
        // pass for the wrong reason.
        var compileErrors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToImmutableArray();
        Assert.True(
            compileErrors.IsEmpty,
            "fixture does not compile: " + string.Join("; ", compileErrors.Select(d => d.ToString())));

        var reported = compilation
            // Every analyzer in the package runs over every fixture, rather than each test
            // choosing its own. A rule that fires on another rule's negative fixture is a
            // false positive on code this repository has already asserted is clean, and
            // running them together is what makes that show up as a failing test.
            .WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(
                new AmbientNonDeterminismAnalyzer(),
                new UnorderedMaterializationAnalyzer()))
            .GetAnalyzerDiagnosticsAsync()
            .GetAwaiter()
            .GetResult();

        return reported
            .OrderBy(d => d.Location.SourceSpan.Start)
            .ToImmutableArray();
    }

    private static ImmutableArray<MetadataReference> BuildReferences()
    {
        var trusted = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
        return trusted
            .Split(Path.PathSeparator)
            .Where(path => path.EndsWith(".dll", StringComparison.Ordinal))
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToImmutableArray();
    }
}
