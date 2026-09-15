using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace RulesKernel.Analyzers;

/// <summary>
/// Reports ambient non-determinism — entropy, clock, environment and concurrency — in a
/// project that references this package.
/// </summary>
/// <remarks>
/// <para>
/// This resolves symbols against the compilation rather than matching source text, which is
/// the whole reason it exists. <c>tools/repo-checks.py</c> in the kernel repository is a
/// pattern blacklist: a <c>using</c> alias, an extension method named to hide its receiver,
/// or a generated tree all walk past it, as docs/decisions/0009 records. A symbol comparison
/// closes the class rather than the instance, because every spelling of a banned member
/// binds to the same <see cref="ISymbol"/>.
/// </para>
/// <para>
/// Diagnostics default to <see cref="DiagnosticSeverity.Warning"/>, not error. An analyzer
/// that breaks a consumer's build the moment they take a version bump teaches them to
/// remove the package. A consumer that wants these enforced escalates them — this
/// repository's own gate builds with <c>-warnaserror</c>, and an engine can do the same or
/// raise individual rules in <c>.editorconfig</c>.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class AmbientNonDeterminismAnalyzer : DiagnosticAnalyzer
{
    private const string Category = "Determinism";

    private const string HelpUri =
        "https://github.com/brandonifco/rules-kernel/blob/main/docs/decisions/0011-shipping-a-determinism-analyzer.md";

    /// <summary>RK0001 — a value drawn from ambient entropy cannot be replayed.</summary>
    public static readonly DiagnosticDescriptor AmbientEntropy = Rule(
        "RK0001",
        "Ambient entropy is not replayable",
        "'{0}' draws from ambient entropy; take a seeded source as an argument instead");

    /// <summary>RK0002 — a result that reads the clock differs on every run.</summary>
    public static readonly DiagnosticDescriptor AmbientClock = Rule(
        "RK0002",
        "Ambient clock is not replayable",
        "'{0}' reads an ambient clock; pass the instant in as an argument instead");

    /// <summary>RK0003 — a result that reads the environment is machine-dependent.</summary>
    public static readonly DiagnosticDescriptor AmbientEnvironment = Rule(
        "RK0003",
        "Ambient environment makes a result machine-dependent",
        "'{0}' reads ambient machine state; a rules result must resolve from its arguments");

    /// <summary>RK0004 — concurrency makes resolution order non-deterministic.</summary>
    public static readonly DiagnosticDescriptor AmbientConcurrency = Rule(
        "RK0004",
        "Concurrency makes resolution order non-deterministic",
        "'{0}' introduces concurrency; an ordered history is the evidence a run was deterministic");

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(AmbientEntropy, AmbientClock, AmbientEnvironment, AmbientConcurrency);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    private static DiagnosticDescriptor Rule(string id, string title, string message) =>
        new DiagnosticDescriptor(
            id, title, message, Category, DiagnosticSeverity.Warning,
            isEnabledByDefault: true, description: null, helpLinkUri: HelpUri);

    // Whole types, where every member is a problem and naming members individually would be
    // the treadmill docs/decisions/0009 declines to run.
    private static readonly (string MetadataName, DiagnosticDescriptor Rule)[] BannedTypes =
    {
        ("System.Random", AmbientEntropy),
        ("System.Security.Cryptography.RandomNumberGenerator", AmbientEntropy),
        ("System.TimeProvider", AmbientClock),
        ("System.Diagnostics.Stopwatch", AmbientClock),
        ("System.Environment", AmbientEnvironment),
        ("System.Threading.Thread", AmbientConcurrency),
        ("System.Threading.Tasks.Parallel", AmbientConcurrency),
        ("System.Threading.Tasks.TaskFactory", AmbientConcurrency),
        ("System.Linq.ParallelEnumerable", AmbientConcurrency),
    };

    // Individual members of types that are otherwise entirely legitimate. Checked before the
    // whole-type table, so Environment.TickCount reports as a clock rather than as ambient
    // machine state.
    private static readonly (string MetadataName, string Member, DiagnosticDescriptor Rule)[] BannedMembers =
    {
        ("System.Guid", "NewGuid", AmbientEntropy),
        ("System.Guid", "CreateVersion7", AmbientEntropy),
        ("System.DateTime", "Now", AmbientClock),
        ("System.DateTime", "UtcNow", AmbientClock),
        ("System.DateTime", "Today", AmbientClock),
        ("System.DateTimeOffset", "Now", AmbientClock),
        ("System.DateTimeOffset", "UtcNow", AmbientClock),
        ("System.Environment", "TickCount", AmbientClock),
        ("System.Environment", "TickCount64", AmbientClock),
        ("System.Threading.Tasks.Task", "Run", AmbientConcurrency),
    };

    private static void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        var types = new Dictionary<INamedTypeSymbol, DiagnosticDescriptor>(SymbolEqualityComparer.Default);
        foreach (var (metadataName, rule) in BannedTypes)
        {
            var type = context.Compilation.GetTypeByMetadataName(metadataName);

            // A type absent from this compilation's references is not a finding. TimeProvider
            // does not exist on net6.0, and an analyzer that demanded it would be asserting
            // something about the consumer's framework rather than about their code.
            if (type is not null)
            {
                types[type] = rule;
            }
        }

        var members = new Dictionary<ISymbol, DiagnosticDescriptor>(SymbolEqualityComparer.Default);
        foreach (var (metadataName, member, rule) in BannedMembers)
        {
            var type = context.Compilation.GetTypeByMetadataName(metadataName);
            if (type is null)
            {
                continue;
            }

            foreach (var symbol in type.GetMembers(member))
            {
                members[symbol] = rule;
            }
        }

        if (types.Count == 0 && members.Count == 0)
        {
            return;
        }

        var lookup = new Lookup(types, members);

        // MethodReference is here because a banned member captured as a delegate is never an
        // Invocation: `Func<Guid> f = Guid.NewGuid;` binds the same symbol a call would and
        // ran clean past the first four kinds. That is the semantic indirection
        // docs/decisions/0009 says only symbol resolution closes, so missing it left open an
        // instance of the class this package exists to close.
        context.RegisterOperationAction(
            lookup.Inspect,
            OperationKind.Invocation,
            OperationKind.MethodReference,
            OperationKind.ObjectCreation,
            OperationKind.PropertyReference,
            OperationKind.FieldReference);
    }

    private sealed class Lookup
    {
        private readonly Dictionary<INamedTypeSymbol, DiagnosticDescriptor> _types;
        private readonly Dictionary<ISymbol, DiagnosticDescriptor> _members;

        internal Lookup(
            Dictionary<INamedTypeSymbol, DiagnosticDescriptor> types,
            Dictionary<ISymbol, DiagnosticDescriptor> members)
        {
            _types = types;
            _members = members;
        }

        internal void Inspect(OperationAnalysisContext context)
        {
            var operation = context.Operation;

            if (Match(SymbolOf(operation)) is not { } matched)
            {
                return;
            }

            // `Random.Shared.Next()` is two operations over the same banned type: the
            // property reference and the invocation on it. Reporting both puts two
            // overlapping squiggles on one expression and makes a suppression ambiguous, so
            // the outer operation yields to the receiver that already carries the finding.
            if (Match(SymbolOf(InstanceOf(operation))) is not null)
            {
                return;
            }

            var (rule, definition) = matched;

            var name = definition.ContainingType is null
                ? definition.Name
                : definition.ContainingType.Name + "." + definition.Name;

            context.ReportDiagnostic(
                Diagnostic.Create(rule, operation.Syntax.GetLocation(), name));
        }

        private (DiagnosticDescriptor Rule, ISymbol Definition)? Match(ISymbol? symbol)
        {
            if (symbol is null)
            {
                return null;
            }

            // The constructed form of a generic member is a different symbol from the one
            // GetMembers returned, so compare the definition.
            var definition = symbol.OriginalDefinition;

            // Members are checked before whole types, so Environment.TickCount reports as a
            // clock rather than as ambient machine state.
            if (_members.TryGetValue(definition, out var rule)
                || (definition.ContainingType is { } containing
                    && _types.TryGetValue(containing.OriginalDefinition, out rule)))
            {
                return (rule, definition);
            }

            return null;
        }

        private static ISymbol? SymbolOf(IOperation? operation) => operation switch
        {
            IInvocationOperation invocation => invocation.TargetMethod,
            IMethodReferenceOperation method => method.Method,
            IObjectCreationOperation creation => creation.Constructor,
            IPropertyReferenceOperation property => property.Property,
            IFieldReferenceOperation field => field.Field,
            IConversionOperation conversion => SymbolOf(conversion.Operand),
            _ => null,
        };

        private static IOperation? InstanceOf(IOperation operation) => operation switch
        {
            IInvocationOperation invocation => invocation.Instance,

            // A method group has a receiver just as a call does, so it yields on exactly the
            // terms above: `System.Random.Shared.Next` is the property reference and the
            // method reference over one banned type, and reporting both would put two
            // overlapping squiggles on one expression. A method group on a field of a banned
            // type -- `_random.Next` -- is not that shape and never was: the field's own
            // symbol belongs to the consuming type, so the receiver does not match and the
            // method reference reports once on its own.
            IMethodReferenceOperation method => method.Instance,
            IPropertyReferenceOperation property => property.Instance,
            IFieldReferenceOperation field => field.Instance,
            _ => null,
        };
    }
}
