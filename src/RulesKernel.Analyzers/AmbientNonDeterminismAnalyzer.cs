using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace RulesKernel.Analyzers;

/// <summary>
/// Reports ambient non-determinism — entropy, clock, machine state (including culture and
/// time zone), concurrency and replay-unstable hashing — in a project that references this
/// package.
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

    /// <summary>
    /// RK0001 — a drawn value that cannot be replayed, either because its source is ambient
    /// or because its algorithm is not pinned.
    /// </summary>
    public static readonly DiagnosticDescriptor AmbientEntropy = Rule(
        "RK0001",
        "Ambient or unpinned entropy is not replayable",

        // The reason is a message argument rather than fixed text because this one rule
        // covers two distinct failures, and docs/decisions/0015 records what happened when
        // it claimed only the first. SRD_Combat's SeededRandomSource -- the disciplined
        // shape the kernel asks an engine to build, whose own doc comment forbids
        // Random.Shared -- was told it "draws from ambient entropy". It does not: it is
        // seeded. A right finding with a false reason is read as a wrong finding, and the
        // consumer suppresses the rule and stops reading the rest of them, which
        // docs/decisions/0011 names as the expensive direction of error.
        "'{0}' {1}");

    // Genuinely ambient: the value depends on something outside the program's arguments, and
    // no amount of care at the call site makes the next run agree with this one.
    private const string DrawsFromAmbientEntropy =
        "draws from ambient entropy; take a seeded source as an argument instead";

    // Seeded, deterministic today, and still not a replay contract. docs/decisions/0005 is
    // the whole argument: System.Random's seeded behaviour has been stable in practice and
    // was deliberately preserved when .NET 6 changed the seedless path, but it has never
    // been contractual and does not extend across runtimes or reimplementations. That is
    // precisely why PCG32 was ported and pinned by reference vectors rather than trusting
    // the framework, and why RandomAlgorithmId records which generator produced a sequence.
    // A seeded System.Random is repeatable, which is not the same claim as replayable.
    private const string UsesAnUnpinnedAlgorithm =
        "is System.Random, whose algorithm is not guaranteed stable across runtime versions; "
        + "a seed makes it repeatable within one runtime, not replayable across them, so take "
        + "a source with a pinned algorithm as an argument instead";

    /// <summary>RK0002 — a result that reads the clock differs on every run.</summary>
    public static readonly DiagnosticDescriptor AmbientClock = Rule(
        "RK0002",
        "Ambient clock is not replayable",
        "'{0}' reads an ambient clock; pass the instant in as an argument instead");

    /// <summary>
    /// RK0003 — a result that reads ambient machine state, including the environment, the
    /// current culture and the local time zone, is machine-dependent.
    /// </summary>
    public static readonly DiagnosticDescriptor AmbientEnvironment = Rule(
        "RK0003",

        // "Ambient environment" was the title while this rule meant System.Environment.
        // docs/decisions/0016 folded ambient culture and time zone in from RK0006, so the
        // title names what every member below actually has in common.
        "Ambient machine state makes a result machine-dependent",

        // The old second clause was "a rules result must resolve from its arguments", and
        // docs/decisions/0015 records what it did to eight of this rule's ten findings: it
        // asserted that a line separator being concatenated into console output was a rules
        // result, which it was not. The clause below claims only what the narrowed symbol
        // set supports -- that a result derived from this value differs by machine -- and
        // names the fix rather than restating the kernel's own contract at a consumer. It
        // has to be true of the folded-in culture and time-zone members too, and it is:
        // CultureInfo.CurrentCulture is read from the machine, a comparison or a format
        // derived from it differs by machine, and the fix is to pass the culture in.
        "'{0}' reads ambient machine state; a result derived from it differs by machine, so pass the value in as an argument instead");

    /// <summary>RK0004 — concurrency makes resolution order non-deterministic.</summary>
    public static readonly DiagnosticDescriptor AmbientConcurrency = Rule(
        "RK0004",
        "Concurrency makes resolution order non-deterministic",
        "'{0}' introduces concurrency; an ordered history is the evidence a run was deterministic");

    /// <summary>RK0005 — a runtime hash code is not stable enough to be replay-visible.</summary>
    public static readonly DiagnosticDescriptor ReplayUnstableHashing = Rule(
        "RK0005",
        "A runtime hash code is not replay-stable",
        "'{0}' is not stable across processes or releases; derive a replay-visible value with a pinned algorithm instead");

    // RK0006 is a gap, and it stays one. It existed here for ambient culture and time zone,
    // never shipped, and was folded into RK0003 in docs/decisions/0016 -- the two rationale
    // sentences were near-identical, one concept should need one suppression, and the
    // calibration gave it zero findings against RK0003's ten. An id that was never published
    // was never a contract, so it is removed rather than marked retired and leaves no trace
    // beyond this number.
    //
    // Do not close the gap by renumbering RK0007. Numbering here is sequential and permanent
    // for the reason docs/decisions/README.md gives for decision records: a number is how
    // something is referred to from outside, and moving RK0007 down would change the
    // identity of a rule whose meaning did not change. The gap costs nothing; tidying it
    // costs the one guarantee these ids exist to give.

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(
            AmbientEntropy,
            AmbientClock,
            AmbientEnvironment,
            AmbientConcurrency,
            ReplayUnstableHashing);

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

        // System.Environment is NOT here, and the asymmetry with tools/repo-checks.py is
        // deliberate rather than an oversight, so it is written down where the next reader
        // will look for it.
        //
        // That file bans `Environment.` as a whole-type prefix and is right to: every line
        // of this repository's packaged code is a rules result, the kernel may not read the
        // environment at all, and the rule before it named three members and let
        // ProcessPath, ProcessorCount and NewLine through. That reasoning was carried into
        // a consumer-facing rule without being re-derived, and docs/decisions/0015 measured
        // the result -- ten findings in SRD_Combat, eight of them Environment.NewLine
        // concatenated into console output and one of them Environment.Exit in a fault
        // handler, reported as a *read* of machine state it does not perform.
        //
        // A consumer's application is not the kernel. It has a console UI, a file loader
        // and an exit path, and none of those is a rules result. The lesson is the general
        // one: a rule about what may appear anywhere in this repository does not transfer
        // to a rule about what may appear anywhere in someone else's, and the members that
        // survive below are the ones that make a *result* machine-dependent. A rule that is
        // four-fifths noise on the first real codebase it meets gets suppressed project-
        // wide, after which it is dead while still appearing to be on, which
        // docs/decisions/0011 names as the expensive direction of error.
        ("System.Threading.Thread", AmbientConcurrency),
        ("System.Threading.Tasks.Parallel", AmbientConcurrency),
        ("System.Threading.Tasks.TaskFactory", AmbientConcurrency),
        ("System.Linq.ParallelEnumerable", AmbientConcurrency),

        // Every member of System.HashCode feeds one accumulator whose seed is randomised per
        // process, so there is no subset of it that is replay-stable. docs/decisions/0005
        // records that a predecessor engine hand-rolled a SplitMix64 finaliser specifically
        // to avoid this type: a value derived from it agrees with itself all afternoon and
        // disagrees with yesterday's transcript -- reproducible within a process, different
        // across runs, which is the worst failure mode available here.
        ("System.HashCode", ReplayUnstableHashing),
    };

    // Individual members of types that are otherwise entirely legitimate. Still checked
    // before the whole-type table: no entry below currently sits on a type that table also
    // bans, but the two lists are independent, and a whole-type ban added later must not
    // silently outrank the more specific rule a member names.
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

        // The System.Environment members that make a *result* machine-dependent, replacing
        // the whole-type ban this rule used to carry; see the note in BannedTypes for why.
        // The line is "would two machines running the same rules with the same arguments
        // disagree because of this value", which admits the machine facts below and rejects
        // NewLine (output formatting, and a caller who wants a platform separator in a
        // transcript has a real reason for it), Exit and FailFast (process control, which
        // reads nothing), and ExitCode.
        ("System.Environment", "ProcessorCount", AmbientEnvironment),
        ("System.Environment", "MachineName", AmbientEnvironment),
        ("System.Environment", "UserName", AmbientEnvironment),
        ("System.Environment", "UserDomainName", AmbientEnvironment),
        ("System.Environment", "UserInteractive", AmbientEnvironment),
        ("System.Environment", "OSVersion", AmbientEnvironment),
        ("System.Environment", "Version", AmbientEnvironment),
        ("System.Environment", "Is64BitOperatingSystem", AmbientEnvironment),
        ("System.Environment", "Is64BitProcess", AmbientEnvironment),
        ("System.Environment", "ProcessId", AmbientEnvironment),
        ("System.Environment", "ProcessPath", AmbientEnvironment),
        ("System.Environment", "CurrentDirectory", AmbientEnvironment),
        ("System.Environment", "SystemDirectory", AmbientEnvironment),
        ("System.Environment", "SystemPageSize", AmbientEnvironment),
        ("System.Environment", "WorkingSet", AmbientEnvironment),
        ("System.Environment", "StackTrace", AmbientEnvironment),
        ("System.Environment", "CommandLine", AmbientEnvironment),
        ("System.Environment", "GetCommandLineArgs", AmbientEnvironment),
        ("System.Environment", "GetEnvironmentVariable", AmbientEnvironment),
        ("System.Environment", "GetEnvironmentVariables", AmbientEnvironment),
        ("System.Environment", "ExpandEnvironmentVariables", AmbientEnvironment),
        ("System.Environment", "GetFolderPath", AmbientEnvironment),
        ("System.Environment", "GetLogicalDrives", AmbientEnvironment),

        // These three carried RK0006 until docs/decisions/0016 retired that id and folded
        // them here. The coverage did not change; only the number a consumer suppresses did.
        //
        // They belong under a rule about reads that make a result machine-dependent, and the
        // case against -- recorded in 0016 rather than lost -- is that ambient culture does
        // more than ProcessorCount does: it changes what comparing, sorting, parsing and
        // formatting *mean*, at call sites that name nothing ambient at all, so a consumer
        // might reasonably have wanted to enforce culture-invariance while allowing machine
        // reads. One suppression for one concept won, because the two rationale sentences
        // were near-identical and the calibration gave RK0006 zero findings against
        // RK0003's ten. This repository sets InvariantGlobalization, but that is this build,
        // not a consumer's.
        ("System.Globalization.CultureInfo", "CurrentCulture", AmbientEnvironment),
        ("System.Globalization.CultureInfo", "CurrentUICulture", AmbientEnvironment),
        ("System.TimeZoneInfo", "Local", AmbientEnvironment),
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

        // Object.GetHashCode cannot live in either table above: it is not a member of a
        // banned type, it is a member of every type there is. It is matched by walking the
        // override chain instead, which is what makes a call on a string, on an int and on a
        // consumer's own type one rule rather than an open-ended list of receivers -- the
        // difference docs/decisions/0009 says only symbol resolution can make.
        var objectGetHashCode = context.Compilation
            .GetSpecialType(SpecialType.System_Object)
            .GetMembers("GetHashCode")
            .OfType<IMethodSymbol>()
            .FirstOrDefault(method => method.Parameters.Length == 0);

        // Needed to recognise the second legitimate home for a runtime hash; see below.
        var equalityComparers = new List<INamedTypeSymbol>();
        foreach (var metadataName in new[]
                 {
                     "System.Collections.Generic.IEqualityComparer`1",
                     "System.Collections.IEqualityComparer",
                 })
        {
            if (context.Compilation.GetTypeByMetadataName(metadataName) is { } comparer)
            {
                equalityComparers.Add(comparer);
            }
        }

        if (types.Count == 0 && members.Count == 0 && objectGetHashCode is null)
        {
            return;
        }

        // System.Random is the one banned type whose members do not all fail for the same
        // reason, so the reason RK0001 prints depends on which member matched; see
        // Lookup.EntropyReason.
        var random = context.Compilation.GetTypeByMetadataName("System.Random");

        var lookup = new Lookup(types, members, objectGetHashCode, equalityComparers, random);

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
        private readonly IMethodSymbol? _objectGetHashCode;
        private readonly List<INamedTypeSymbol> _equalityComparers;
        private readonly INamedTypeSymbol? _random;

        internal Lookup(
            Dictionary<INamedTypeSymbol, DiagnosticDescriptor> types,
            Dictionary<ISymbol, DiagnosticDescriptor> members,
            IMethodSymbol? objectGetHashCode,
            List<INamedTypeSymbol> equalityComparers,
            INamedTypeSymbol? random)
        {
            _types = types;
            _members = members;
            _objectGetHashCode = objectGetHashCode;
            _equalityComparers = equalityComparers;
            _random = random;
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

            // RK0005 is the one rule with a legitimate home, and that home is why it is a
            // rule about *where* a hash is used rather than a ban on an API.
            // ReplayCompatibilityIdentity.GetHashCode in this repository's own kernel builds
            // its hash with System.HashCode, correctly: a hashed collection wants a
            // per-process bucket, not a fingerprint. Reporting there would be a false
            // positive in the single most idiomatic shape in .NET, and docs/decisions/0011
            // records that a false positive costs a consumer far more than a false negative
            // -- they cannot fix it, only suppress it or drop the package.
            if (ReferenceEquals(rule, ReplayUnstableHashing)
                && IsWithinHashCodeImplementation(context.ContainingSymbol))
            {
                return;
            }

            var name = definition.ContainingType is null
                ? definition.Name
                : definition.ContainingType.Name + "." + definition.Name;

            // Every rule but RK0001 states one reason, because every member it matches fails
            // for that reason. RK0001 takes a second argument instead of a second diagnostic
            // id: docs/decisions/0015 considered moving seeded System.Random to RK0005 and
            // declined, because an id is a permanent contract a consumer suppresses against
            // and spending one to correct a sentence is the wrong trade.
            var arguments = ReferenceEquals(rule, AmbientEntropy)
                ? new object[] { name, EntropyReason(definition) }
                : new object[] { name };

            context.ReportDiagnostic(
                Diagnostic.Create(rule, operation.Syntax.GetLocation(), arguments));
        }

        // Which of RK0001's two reasons is true of the member that matched.
        //
        // Random.Shared and the parameterless constructor are seeded from the operating
        // system, so they are ambient in the plain sense. Everything else on System.Random
        // -- a seeded constructor, and any draw off an instance whose provenance the call
        // site cannot see -- is the docs/decisions/0005 problem instead: deterministic
        // within a runtime, uncontracted across runtimes.
        //
        // The unseen-provenance case is why a draw reports the algorithm reason rather than
        // no reason at all. `_random.Next(sides)` inside SeededRandomSource is a call on a
        // field, and the field's symbol belongs to the consuming type, so nothing here can
        // tell a seeded instance from Random.Shared assigned into a field. The algorithm
        // reason is true of both, and being true of both is the property that matters.
        private string EntropyReason(ISymbol definition)
        {
            if (_random is null
                || definition.ContainingType is not { } containing
                || !SymbolEqualityComparer.Default.Equals(containing.OriginalDefinition, _random))
            {
                // Guid.NewGuid, RandomNumberGenerator, and anything else added to RK0001
                // that is not System.Random: ambient by construction.
                return DrawsFromAmbientEntropy;
            }

            var seededFromTheOperatingSystem =
                definition is IPropertySymbol { Name: "Shared" }
                || definition is IMethodSymbol { MethodKind: MethodKind.Constructor, Parameters.Length: 0 };

            return seededFromTheOperatingSystem ? DrawsFromAmbientEntropy : UsesAnUnpinnedAlgorithm;
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

            // Members are checked before whole types, so a member that names its own rule
            // always wins over a ban on its containing type. Nothing exercises that ordering
            // today -- Environment.TickCount did, until RK0003 stopped banning the whole
            // type -- and it is kept because the tables are edited independently.
            if (_members.TryGetValue(definition, out var rule)
                || (definition.ContainingType is { } containing
                    && _types.TryGetValue(containing.OriginalDefinition, out rule)))
            {
                return (rule, definition);
            }

            // A string's is the case that matters most: .NET randomises string hashing per
            // process, so a seed or a "stable" id derived from it is reproducible all
            // afternoon and different tomorrow.
            if (definition is IMethodSymbol method && OverridesObjectGetHashCode(method))
            {
                return (ReplayUnstableHashing, definition);
            }

            return null;
        }

        // Whether a symbol is Object.GetHashCode or any override of it, at any depth.
        private bool OverridesObjectGetHashCode(IMethodSymbol method)
        {
            if (_objectGetHashCode is null || method.Parameters.Length != 0)
            {
                return false;
            }

            for (IMethodSymbol? current = method.OriginalDefinition;
                 current is not null;
                 current = current.OverriddenMethod?.OriginalDefinition)
            {
                if (SymbolEqualityComparer.Default.Equals(current, _objectGetHashCode))
                {
                    return true;
                }
            }

            return false;
        }

        // Whether the operation sits inside a member whose entire job is producing a hash
        // code for a hashed collection.
        //
        // The walk goes up ContainingSymbol rather than inspecting only the immediately
        // enclosing symbol, because a lambda or a local function inside GetHashCode is its
        // own IMethodSymbol. Combining hashes inside a lambda in a GetHashCode body is the
        // same legitimate use one level down, and reporting it would be exactly the false
        // positive this suppression exists to prevent.
        //
        // The line is drawn lexically, and deliberately. A private helper *called from*
        // GetHashCode is not suppressed: following the call would need a call graph, which
        // an operation action does not have, and the whole-compilation approximation of one
        // cuts the wrong way -- it would suppress any hashing reachable from a GetHashCode
        // anywhere in the compilation, including the seed derivation docs/decisions/0005
        // says an engine will be tempted to write. Under-reporting is the preference
        // docs/decisions/0011 states, and this is the one place the surgical choice
        // over-reports instead, so it is named here rather than left to be discovered: a
        // consumer with a shared hash helper suppresses RK0005 on that helper, and the
        // suppression then reads as the deliberate act it is.
        //
        // A record's compiler-generated equality needs no branch here. Its GetHashCode has
        // no source body, so no operation action ever runs over it -- asserted in
        // tests/RulesKernel.Analyzers.Tests rather than assumed, because "it reports
        // nothing" is indistinguishable from a rule that silently stopped working.
        private bool IsWithinHashCodeImplementation(ISymbol? symbol)
        {
            for (; symbol is not null; symbol = symbol.ContainingSymbol)
            {
                if (symbol is INamedTypeSymbol or INamespaceSymbol)
                {
                    // A type boundary ends the walk. Stopping here means the rule can never
                    // suppress on the strength of an enclosing *type* looking hash-related,
                    // which is the loosest thing this could accidentally become.
                    return false;
                }

                if (symbol is IMethodSymbol method
                    && (OverridesObjectGetHashCode(method) || ImplementsComparerGetHashCode(method)))
                {
                    return true;
                }
            }

            return false;
        }

        // Whether a method is this type's implementation of the GetHashCode declared by
        // IEqualityComparer, generic or not. A comparer is the second idiomatic home for a
        // runtime hash, and a consumer writing one cannot restructure their way out of a
        // diagnostic on it -- the signature is the interface's, not theirs.
        private bool ImplementsComparerGetHashCode(IMethodSymbol method)
        {
            if (method.ContainingType is not { } containingType)
            {
                return false;
            }

            foreach (var comparer in _equalityComparers)
            {
                foreach (var implemented in containingType.AllInterfaces)
                {
                    if (!SymbolEqualityComparer.Default.Equals(implemented.OriginalDefinition, comparer))
                    {
                        continue;
                    }

                    foreach (var member in implemented.GetMembers("GetHashCode"))
                    {
                        // FindImplementationForInterfaceMember answers for the explicit and
                        // the implicit form alike, so neither spelling needs its own branch.
                        var implementation = containingType.FindImplementationForInterfaceMember(member);
                        if (implementation is not null
                            && SymbolEqualityComparer.Default.Equals(
                                implementation.OriginalDefinition, method.OriginalDefinition))
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
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
