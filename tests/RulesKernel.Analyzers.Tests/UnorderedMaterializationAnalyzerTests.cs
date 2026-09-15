namespace RulesKernel.Analyzers.Tests;

/// <summary>
/// RK0007 warns when an unordered collection is materialized into an ordered one without an
/// explicit sort. Every silence asserted below is paired with a near-identical fixture that
/// fires, because "reports nothing" is otherwise indistinguishable from a rule that never
/// ran — the standard the RK0005 assertions in
/// <see cref="AmbientNonDeterminismAnalyzerTests"/> set.
/// </summary>
public sealed class UnorderedMaterializationAnalyzerTests
{
    // --- The shape the issue reproduced empirically and got nothing for. ---

    [Theory]
    [InlineData("foreach (var kv in map) { ordered.Add(kv.Value); }")]
    [InlineData("foreach (var key in map.Keys) { ordered.Add(key.Length); }")]
    [InlineData("foreach (var value in map.Values) { ordered.Add(value); }")]
    [InlineData("foreach (var item in set) { ordered.Add(item); }")]
    [InlineData("foreach (var kv in map) { ordered.Insert(0, kv.Value); }")]
    [InlineData("foreach (var kv in map.Where(e => e.Value > 0)) { ordered.Add(kv.Value); }")]
    public void An_unordered_source_feeding_an_ordered_accumulator_is_reported(string body)
    {
        Assert.Equal(new[] { "RK0007" }, AnalyzerHarness.Diagnose(Wrap(body)));
    }

    [Theory]
    [InlineData("var x = map.Select(kv => kv.Value).ToList();")]
    [InlineData("var x = map.ToArray();")]
    [InlineData("var x = set.Select(v => v).ToArray();")]
    [InlineData("var x = map.Keys.ToList();")]
    [InlineData("var x = string.Join(\",\", map.Values);")]
    public void Materializing_an_unordered_source_through_LINQ_is_reported(string body)
    {
        Assert.Equal(new[] { "RK0007" }, AnalyzerHarness.Diagnose(Wrap(body)));
    }

    [Fact]
    public void A_StringBuilder_is_an_ordered_accumulator()
    {
        // The separator and the position of every entry are observable in the result, so a
        // dictionary's iteration order ends up in a string a consumer may well hash.
        var appending = Wrap("""
            var text = new System.Text.StringBuilder();
            foreach (var kv in map)
            {
                text.AppendLine(kv.Value.ToString());
            }
            """);

        Assert.Equal(new[] { "RK0007" }, AnalyzerHarness.Diagnose(appending));
    }

    [Fact]
    public void An_ImmutableArray_builder_is_an_ordered_accumulator()
    {
        var building = Wrap("""
            var builder = System.Collections.Immutable.ImmutableArray.CreateBuilder<int>();
            foreach (var kv in map)
            {
                builder.Add(kv.Value);
            }
            """);

        Assert.Equal(new[] { "RK0007" }, AnalyzerHarness.Diagnose(building));
    }

    [Fact]
    public void An_array_write_is_an_ordered_accumulator_and_an_array_read_is_not()
    {
        // An array write is the same sink with no method to name. The paired read is what
        // shows the rule is looking at the assignment target rather than at the type.
        var written = Wrap("""
            var slots = new int[8];
            var next = 0;
            foreach (var kv in map)
            {
                slots[next++] = kv.Value;
            }
            """);

        var read = Wrap("""
            var slots = new int[8];
            var sum = 0;
            foreach (var kv in map)
            {
                sum += slots[kv.Value % 8];
            }
            sum.ToString();
            """);

        Assert.Equal(new[] { "RK0007" }, AnalyzerHarness.Diagnose(written));
        Assert.Empty(AnalyzerHarness.Diagnose(read));
    }

    [Fact]
    public void A_method_returning_an_unordered_type_does_not_hide_it()
    {
        // The call site reads `Build().ToList()`, which names no unordered type. Resolving
        // the return type sees through it, which is the difference between this and the
        // pattern blacklist docs/decisions/0009 records the limits of.
        var hidden = """
            using System.Collections.Generic;
            using System.Linq;

            public static class Consumer
            {
                private static Dictionary<string, int> Build() => new Dictionary<string, int>();

                public static object Resolve() => Build().ToList();
            }
            """;

        Assert.Equal(new[] { "RK0007" }, AnalyzerHarness.Diagnose(hidden));
    }

    // --- The documented fix has to be silent, or the message teaches nothing. ---

    [Theory]
    [InlineData("foreach (var kv in map.OrderBy(e => e.Key)) { ordered.Add(kv.Value); }")]
    [InlineData("foreach (var kv in map.OrderByDescending(e => e.Key)) { ordered.Add(kv.Value); }")]
    [InlineData("foreach (var kv in map.OrderBy(e => e.Key).ThenBy(e => e.Value)) { ordered.Add(kv.Value); }")]
    [InlineData("foreach (var kv in map.OrderBy(e => e.Key).Where(e => e.Value > 0)) { ordered.Add(kv.Value); }")]
    [InlineData("var x = map.OrderBy(e => e.Key).Select(e => e.Value).ToList();")]
    [InlineData("var x = map.Keys.OrderBy(k => k).ToArray();")]
    [InlineData("var x = string.Join(\",\", map.Values.OrderBy(v => v));")]
    public void An_explicit_sort_silences_the_rule(string body)
    {
        Assert.Empty(AnalyzerHarness.Diagnose(Wrap(body)));
    }

    // --- Order-insensitive consumption. Each is one word away from a fixture above. ---

    [Theory]
    [InlineData("foreach (var kv in map) { total += kv.Value; }")]
    [InlineData("foreach (var kv in map) { unordered.Add(kv.Key); }")]
    [InlineData("foreach (var kv in map) { other[kv.Key] = kv.Value; }")]
    [InlineData("foreach (var kv in map) { }")]
    [InlineData("var x = map.Any(kv => kv.Value > 0);")]
    [InlineData("var x = map.All(kv => kv.Value > 0);")]
    [InlineData("var x = map.Count();")]
    [InlineData("var x = map.Sum(kv => kv.Value);")]
    [InlineData("var x = map.ToDictionary(kv => kv.Key, kv => kv.Value);")]
    [InlineData("var x = map.Keys.ToHashSet();")]
    public void Order_insensitive_consumption_is_not_reported(string body)
    {
        Assert.Empty(AnalyzerHarness.Diagnose(Wrap(body)));
    }

    [Fact]
    public void An_ordered_source_feeding_an_ordered_accumulator_is_not_reported()
    {
        // The accumulator half of the rule fires on its own in the fixtures above; this is
        // what proves the source half is doing work rather than the rule firing on List.Add
        // wherever it appears.
        var fine = Wrap("foreach (var value in sequence) { ordered.Add(value); }");

        Assert.Empty(AnalyzerHarness.Diagnose(fine));
    }

    [Fact]
    public void An_accumulator_that_dies_with_the_iteration_is_not_reported()
    {
        // Declared inside the body, so nothing outside the loop can observe the order it was
        // filled in. The paired fixture moves one line and fires.
        var inside = Wrap("""
            foreach (var kv in map)
            {
                var perEntry = new List<int>();
                perEntry.Add(kv.Value);
                total += perEntry.Count;
            }
            """);

        var outside = Wrap("""
            var escaping = new List<int>();
            foreach (var kv in map)
            {
                escaping.Add(kv.Value);
            }
            """);

        Assert.Empty(AnalyzerHarness.Diagnose(inside));
        Assert.Equal(new[] { "RK0007" }, AnalyzerHarness.Diagnose(outside));
    }

    // --- Where the line is drawn on ambiguity. These are deliberate false negatives. ---

    [Fact]
    public void A_call_the_analyzer_cannot_see_into_is_deliberately_not_reported()
    {
        // `Record` may well append to a list one frame down, and this rule will never know:
        // answering would need a call graph, and an operation action does not have one.
        // docs/decisions/0011 records that a false positive costs a consumer more than a
        // false negative, because they cannot fix it -- only suppress it or drop the
        // package. So the unknown case is silent, and it is pinned here as a decision on
        // record rather than left to be discovered in a consumer's build.
        var opaque = """
            using System.Collections.Generic;

            public static class Consumer
            {
                private static readonly List<int> Sink = new List<int>();

                private static void Record(int value) => Sink.Add(value);

                public static void Resolve(Dictionary<string, int> map)
                {
                    foreach (var kv in map)
                    {
                        Record(kv.Value);
                    }
                }
            }
            """;

        var direct = """
            using System.Collections.Generic;

            public static class Consumer
            {
                private static readonly List<int> Sink = new List<int>();

                public static void Resolve(Dictionary<string, int> map)
                {
                    foreach (var kv in map)
                    {
                        Sink.Add(kv.Value);
                    }
                }
            }
            """;

        Assert.Empty(AnalyzerHarness.Diagnose(opaque));
        Assert.Equal(new[] { "RK0007" }, AnalyzerHarness.Diagnose(direct));
    }

    [Fact]
    public void An_interface_typed_source_is_deliberately_not_reported()
    {
        // SortedDictionary<,> implements IDictionary<,>, and IEnumerable<T> says nothing at
        // all about order. Flagging either would fire on code that is already correct, and
        // the consumer could do nothing about it but suppress. The concrete-typed twin is
        // what shows the rule is running at all.
        var abstracted = Wrap(
            "foreach (var kv in opaque) { ordered.Add(kv.Value); }");

        var concrete = Wrap("foreach (var kv in map) { ordered.Add(kv.Value); }");

        Assert.Empty(AnalyzerHarness.Diagnose(abstracted));
        Assert.Equal(new[] { "RK0007" }, AnalyzerHarness.Diagnose(concrete));
    }

    [Fact]
    public void An_operator_the_rule_does_not_model_stops_the_walk()
    {
        // GroupBy re-shapes the sequence in a way this rule does not model, so the walk
        // stops there rather than guessing. Distinct, which it does model, is the twin that
        // fires -- silence has to be a property of GroupBy, not of the chain being long.
        var grouped = Wrap("var x = map.GroupBy(kv => kv.Value).Select(g => g.Key).ToList();");
        var modelled = Wrap("var x = map.Select(kv => kv.Value).Distinct().ToList();");

        Assert.Empty(AnalyzerHarness.Diagnose(grouped));
        Assert.Equal(new[] { "RK0007" }, AnalyzerHarness.Diagnose(modelled));
    }

    [Fact]
    public void Each_occurrence_is_reported_in_source_order()
    {
        var several = Wrap("""
            foreach (var kv in map) { ordered.Add(kv.Value); }
            var a = set.ToList();
            foreach (var kv in map.OrderBy(e => e.Key)) { ordered.Add(kv.Value); }
            var b = map.Values.ToArray();
            """);

        Assert.Equal(new[] { "RK0007", "RK0007", "RK0007" }, AnalyzerHarness.Diagnose(several));
    }

    [Fact]
    public void A_clean_consumer_reports_nothing_from_either_analyzer()
    {
        // Both analyzers run over every fixture in this assembly, so this is the assertion
        // that RK0007 has not started firing on the ordinary code the other rules call
        // clean.
        var clean = Wrap("total = ordered.Count + sequence.Count;");

        Assert.Empty(AnalyzerHarness.Diagnose(clean));
    }

    private static string Wrap(string body) => $$"""
        using System.Collections.Generic;
        using System.Linq;

        public static class Consumer
        {
            public static void Resolve(
                Dictionary<string, int> map,
                HashSet<int> set,
                List<int> ordered,
                List<int> sequence,
                HashSet<string> unordered,
                Dictionary<string, int> other,
                IDictionary<string, int> opaque)
            {
                var total = 0;
                {{body}}
                total.ToString();
            }
        }
        """;
}
