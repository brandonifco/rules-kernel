using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace RulesKernel.Analyzers;

/// <summary>
/// Reports an unordered collection being materialized into an ordered one without an
/// explicit sort — the determinism hole
/// <c>docs/decisions/0009-what-the-source-blacklists-do-not-prove.md</c> records as left to
/// review.
/// </summary>
/// <remarks>
/// <para>
/// That one sentence is the whole scope, deliberately.
/// <c>docs/decisions/0014-warning-where-unordered-becomes-ordered.md</c> records the two
/// scopes rejected to arrive at it, and why a narrower or a wider rule was worse.
/// </para>
/// <para>
/// The rule fires only on a *positive* recognition of both halves: a source whose type
/// documents no iteration order, and a sink whose whole purpose is to preserve the order
/// things arrived in. Anything it cannot recognise — a method it cannot see into, a
/// sequence whose provenance is an <c>IEnumerable&lt;T&gt;</c> parameter, an accumulation
/// into another hashed collection — is silent, and silent is the correct answer there
/// rather than a missing feature. <c>docs/decisions/0011</c> records why: a false positive
/// costs a consumer more than a false negative, because they cannot fix it, only suppress
/// it or drop the package.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UnorderedMaterializationAnalyzer : DiagnosticAnalyzer
{
    private const string Category = "Determinism";

    private const string HelpUri =
        "https://github.com/brandonifco/rules-kernel/blob/main/docs/decisions/0014-warning-where-unordered-becomes-ordered.md";

    /// <summary>RK0007 — an unordered collection became an ordered result with no sort in between.</summary>
    public static readonly DiagnosticDescriptor UnorderedMaterialization = new DiagnosticDescriptor(
        "RK0007",
        "An unordered collection is materialized into an ordered result",
        "'{0}' has no defined iteration order, and this materializes it into an ordered result; sort it explicitly with OrderBy first",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: null,
        helpLinkUri: HelpUri);

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(UnorderedMaterialization);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    // Types whose documented iteration order is "unspecified". Every one of these is a
    // concrete type or a nested collection of one, never an interface: IDictionary<,> and
    // ISet<> are implemented by SortedDictionary<,> and SortedSet<>, so a parameter typed as
    // the interface may well be ordered and flagging it would be exactly the false positive
    // docs/decisions/0011 says a consumer cannot fix. A type this list does not name is
    // unknown, and unknown is silent.
    private static readonly string[] UnorderedTypes =
    {
        "System.Collections.Generic.Dictionary`2",
        "System.Collections.Generic.HashSet`1",
        "System.Collections.Concurrent.ConcurrentDictionary`2",
        "System.Collections.Concurrent.ConcurrentBag`1",
        "System.Collections.Immutable.ImmutableDictionary`2",
        "System.Collections.Immutable.ImmutableHashSet`1",
        "System.Collections.Frozen.FrozenDictionary`2",
        "System.Collections.Frozen.FrozenSet`1",
        "System.Collections.Hashtable",
    };

    // The sinks. Each is a type whose entire contract is "the order things went in is the
    // order they come out", so reaching one from an unordered source is the moment an
    // implementation detail of the runtime becomes part of a result.
    //
    // ICollection<T>.Add is deliberately not here, even though it would cover all of these
    // in one line: HashSet<T> implements it too, and `foreach (var kv in dictionary)
    // { otherSet.Add(kv.Key); }` cannot depend on visit order. Naming the ordered types
    // individually is the difference between this rule and one that fires on every
    // accumulation.
    private static readonly (string MetadataName, string[] Members)[] OrderedAccumulators =
    {
        ("System.Collections.Generic.List`1", new[] { "Add", "AddRange", "Insert", "InsertRange" }),
        ("System.Collections.ObjectModel.Collection`1", new[] { "Add", "Insert" }),
        ("System.Collections.Generic.Queue`1", new[] { "Enqueue" }),
        ("System.Collections.Generic.Stack`1", new[] { "Push" }),
        ("System.Text.StringBuilder", new[] { "Append", "AppendLine", "AppendFormat", "AppendJoin" }),
        ("System.Collections.Immutable.ImmutableArray`1+Builder", new[] { "Add", "AddRange", "Insert" }),
        ("System.Collections.Immutable.ImmutableList`1+Builder", new[] { "Add", "AddRange", "Insert" }),
    };

    // Operators that carry the source sequence's order through unchanged. A chain made only
    // of these is still the source's order, so walking back through them reaches the
    // question that matters: what was at the start.
    private static readonly string[] PassThroughOperators =
    {
        "Select", "Where", "SelectMany", "Take", "Skip", "TakeWhile", "SkipWhile",
        "Cast", "OfType", "Distinct", "DistinctBy", "Reverse", "Append", "Prepend",
        "DefaultIfEmpty", "AsEnumerable",
    };

    // The documented fix. Reaching one of these ends the walk with no diagnostic, which is
    // what makes the message's advice true rather than decorative.
    private static readonly string[] OrderingOperators =
    {
        "OrderBy", "OrderByDescending", "Order", "OrderDescending", "ThenBy", "ThenByDescending",
    };

    // Calls that turn a sequence into something whose order is now part of the value.
    // ToDictionary, ToHashSet, ToLookup, Count, Any, All, Sum and their relatives are
    // absent on purpose: their result cannot depend on the order the source was visited in,
    // which is the other half of the scope decision.
    private static readonly (string MetadataName, string[] Members)[] Materializers =
    {
        ("System.Linq.Enumerable", new[] { "ToList", "ToArray" }),
        ("System.Collections.Immutable.ImmutableArray", new[] { "ToImmutableArray" }),
        ("System.Collections.Immutable.ImmutableList", new[] { "ToImmutableList" }),

        // string.Join over a dictionary's values is the same mistake wearing a different
        // coat: the separator makes the position of every element observable.
        ("System.String", new[] { "Join", "Concat" }),
    };

    private static void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        var unordered = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        foreach (var metadataName in UnorderedTypes)
        {
            // A type absent from this compilation's references is not a finding.
            // FrozenDictionary does not exist before net8.0, and demanding it would be an
            // assertion about the consumer's framework rather than about their code.
            if (context.Compilation.GetTypeByMetadataName(metadataName) is { } type)
            {
                unordered.Add(type);
            }
        }

        if (unordered.Count == 0)
        {
            return;
        }

        var accumulators = MembersOf(context.Compilation, OrderedAccumulators);
        var materializers = MembersOf(context.Compilation, Materializers);

        var passThrough = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        var ordering = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        foreach (var queryable in new[] { "System.Linq.Enumerable", "System.Linq.Queryable" })
        {
            if (context.Compilation.GetTypeByMetadataName(queryable) is not { } type)
            {
                continue;
            }

            foreach (var name in PassThroughOperators)
            {
                foreach (var member in type.GetMembers(name))
                {
                    passThrough.Add(member.OriginalDefinition);
                }
            }

            foreach (var name in OrderingOperators)
            {
                foreach (var member in type.GetMembers(name))
                {
                    ordering.Add(member.OriginalDefinition);
                }
            }
        }

        var walker = new Walker(unordered, accumulators, materializers, passThrough, ordering);

        context.RegisterOperationAction(walker.InspectLoop, OperationKind.Loop);
        context.RegisterOperationAction(walker.InspectInvocation, OperationKind.Invocation);
    }

    private static HashSet<ISymbol> MembersOf(
        Compilation compilation,
        (string MetadataName, string[] Members)[] table)
    {
        var symbols = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        foreach (var (metadataName, members) in table)
        {
            if (compilation.GetTypeByMetadataName(metadataName) is not { } type)
            {
                continue;
            }

            foreach (var member in members)
            {
                foreach (var symbol in type.GetMembers(member))
                {
                    symbols.Add(symbol.OriginalDefinition);
                }
            }
        }

        return symbols;
    }

    private sealed class Walker
    {
        private readonly HashSet<INamedTypeSymbol> _unordered;
        private readonly HashSet<ISymbol> _accumulators;
        private readonly HashSet<ISymbol> _materializers;
        private readonly HashSet<ISymbol> _passThrough;
        private readonly HashSet<ISymbol> _ordering;

        internal Walker(
            HashSet<INamedTypeSymbol> unordered,
            HashSet<ISymbol> accumulators,
            HashSet<ISymbol> materializers,
            HashSet<ISymbol> passThrough,
            HashSet<ISymbol> ordering)
        {
            _unordered = unordered;
            _accumulators = accumulators;
            _materializers = materializers;
            _passThrough = passThrough;
            _ordering = ordering;
        }

        // `foreach (var kv in dictionary) { ordered.Add(kv.Value); }` -- the shape the issue
        // that asked for this rule reproduced empirically and got nothing for.
        internal void InspectLoop(OperationAnalysisContext context)
        {
            if (context.Operation is not IForEachLoopOperation loop)
            {
                return;
            }

            if (Source(loop.Collection) is not { } source)
            {
                return;
            }

            if (!AppendsToAnOrderedAccumulator(loop))
            {
                return;
            }

            Report(context, source, loop.Collection);
        }

        internal void InspectInvocation(OperationAnalysisContext context)
        {
            var invocation = (IInvocationOperation)context.Operation;

            if (!_materializers.Contains(invocation.TargetMethod.OriginalDefinition))
            {
                return;
            }

            // Every argument is examined rather than only the first, because the sequence is
            // not always in the same place: `dictionary.Select(f).ToList()` passes it as the
            // reduced extension receiver, `string.Join(",", dictionary.Values)` as the
            // second argument.
            foreach (var argument in Candidates(invocation))
            {
                if (Source(argument) is { } source)
                {
                    Report(context, source, argument);
                    return;
                }
            }
        }

        private static IEnumerable<IOperation> Candidates(IInvocationOperation invocation)
        {
            if (invocation.Instance is { } instance)
            {
                yield return instance;
            }

            foreach (var argument in invocation.Arguments)
            {
                yield return argument.Value;
            }
        }

        private static void Report(OperationAnalysisContext context, INamedTypeSymbol source, IOperation at)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                UnorderedMaterialization,
                at.Syntax.GetLocation(),
                source.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
        }

        /// <summary>
        /// Walks back to whatever produced this sequence and answers with the unordered type
        /// at the root, or <c>null</c> for every other outcome — ordered, ordinary, or
        /// unknown.
        /// </summary>
        private INamedTypeSymbol? Source(IOperation? operation)
        {
            // A depth bound rather than a while(true): an operation tree is finite, but a
            // rule that can hang a consumer's compiler is worse than a rule that gives up.
            for (var step = 0; step < 64; step++)
            {
                operation = Unwrap(operation);

                if (operation is null)
                {
                    return null;
                }

                if (operation is IInvocationOperation call)
                {
                    var definition = call.TargetMethod.OriginalDefinition;

                    // The documented fix. An explicit sort ends the walk, silently, and that
                    // is asserted as a test rather than left to follow from this comment.
                    if (_ordering.Contains(definition))
                    {
                        return null;
                    }

                    if (_passThrough.Contains(definition))
                    {
                        operation = call.Instance ?? (call.Arguments.Length > 0 ? call.Arguments[0].Value : null);
                        continue;
                    }

                    // Some other call produced this sequence. If its *return type* is one of
                    // the unordered types then the answer is not in doubt -- a method
                    // returning Dictionary<,> hides nothing, which is the difference between
                    // resolving symbols and matching source text that docs/decisions/0009
                    // says is the whole point of doing this in an analyzer.
                    //
                    // Otherwise this is where the walk gives up: GroupBy, ToLookup, a
                    // consumer's own extension, an interface method with many
                    // implementations all land here typed as IEnumerable<T>, and all of them
                    // are silent. That is the trade docs/decisions/0011 states, applied at
                    // the one place in this rule where it is decided.
                }

                return IsUnordered(operation.Type);
            }

            return null;
        }

        private INamedTypeSymbol? IsUnordered(ITypeSymbol? type)
        {
            if (type is not INamedTypeSymbol named)
            {
                return null;
            }

            var definition = named.OriginalDefinition;

            if (_unordered.Contains(definition))
            {
                return named;
            }

            // Dictionary<K,V>.KeyCollection and its relatives are types in their own right,
            // and enumerating one is the same question as enumerating the dictionary --
            // `foreach (var key in dictionary.Keys)` is the spelling people actually reach
            // for. Matching through the containing type covers every nested collection a
            // banned type declares without naming each one, including ones added later.
            if (definition.ContainingType is { } containing && _unordered.Contains(containing.OriginalDefinition))
            {
                return named;
            }

            return null;
        }

        // Whether anything in the loop body appends to an accumulator that outlives the
        // iteration. The whole body is scanned, nested loops included, because an append two
        // levels down is still the dictionary's order reaching an ordered result.
        private bool AppendsToAnOrderedAccumulator(IForEachLoopOperation loop)
        {
            foreach (var operation in loop.Body.DescendantsAndSelf())
            {
                switch (operation)
                {
                    case IInvocationOperation call
                        when _accumulators.Contains(call.TargetMethod.OriginalDefinition)
                             && !IsDeclaredInside(call.Instance, loop):
                        return true;

                    // An array write is the same sink without a method to name: `slots[i++] =
                    // kv.Value` puts visit order into an indexed result exactly as List.Add
                    // does. Only the assignment target counts -- reading an array inside the
                    // loop says nothing about order.
                    case ISimpleAssignmentOperation { Target: IArrayElementReferenceOperation element }
                        when !IsDeclaredInside(element.ArrayReference, loop):
                        return true;
                }
            }

            return false;
        }

        // Whether the accumulator is a local declared inside the loop body, in which case it
        // dies with the iteration and its order cannot reach a result. This is the one piece
        // of flow reasoning the rule does, and it is here because the alternative is a false
        // positive on a perfectly ordinary shape: building a per-entry list inside the loop
        // and handing it to something order-insensitive.
        //
        // Deliberately shallow. It asks where the local was declared, not where it goes
        // afterwards, so a local declared above the loop is treated as escaping even when it
        // does not. That direction is the safe one: it over-reports only on code that is
        // already writing visit order into an ordered accumulator.
        private static bool IsDeclaredInside(IOperation? receiver, IForEachLoopOperation loop)
        {
            if (Unwrap(receiver) is not ILocalReferenceOperation local)
            {
                return false;
            }

            foreach (var reference in local.Local.DeclaringSyntaxReferences)
            {
                if (loop.Body.Syntax.Span.Contains(reference.Span))
                {
                    return true;
                }
            }

            return false;
        }

        // An implicit conversion to IEnumerable<T> sits between a foreach and its collection,
        // and between a dictionary and the LINQ operator taking it. Unwrapping keeps the walk
        // looking at what the consumer wrote rather than at what the compiler inserted.
        private static IOperation? Unwrap(IOperation? operation)
        {
            while (operation is IConversionOperation conversion)
            {
                operation = conversion.Operand;
            }

            return operation is IParenthesizedOperation parenthesized ? Unwrap(parenthesized.Operand) : operation;
        }
    }
}
