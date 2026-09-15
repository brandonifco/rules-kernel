namespace RulesKernel.Analyzers.Tests;

public sealed class AmbientNonDeterminismAnalyzerTests
{
    [Theory]
    [InlineData("var x = System.Random.Shared.Next();", "RK0001")]
    [InlineData("var x = new System.Random().Next();", "RK0001")]
    [InlineData("var x = System.Guid.NewGuid();", "RK0001")]
    [InlineData("var x = System.DateTime.UtcNow;", "RK0002")]
    [InlineData("var x = System.DateTimeOffset.Now;", "RK0002")]
    [InlineData("var x = System.Diagnostics.Stopwatch.StartNew();", "RK0002")]
    [InlineData("var x = System.Environment.ProcessorCount;", "RK0003")]
    [InlineData("var x = System.Threading.Tasks.Task.Run(() => 1);", "RK0004")]
    [InlineData("var x = System.Threading.Tasks.Parallel.For(0, 1, _ => { });", "RK0004")]
    [InlineData("var x = \"x\".GetHashCode();", "RK0005")]
    [InlineData("var x = System.HashCode.Combine(1, 2);", "RK0005")]
    [InlineData("var x = System.Globalization.CultureInfo.CurrentCulture.Name;", "RK0006")]
    [InlineData("var x = System.Globalization.CultureInfo.CurrentUICulture.Name;", "RK0006")]
    [InlineData("var x = System.TimeZoneInfo.Local;", "RK0006")]
    public void Reports_the_rule_for_each_kind_of_ambient_input(string statement, string expected)
    {
        Assert.Contains(expected, AnalyzerHarness.Diagnose(Wrap(statement)));
    }

    [Fact]
    public void Resolving_from_arguments_alone_reports_nothing()
    {
        var clean = Wrap("var x = seed + count;", parameters: "int seed, int count");

        Assert.Empty(AnalyzerHarness.Diagnose(clean));
    }

    // The four below are the cases docs/decisions/0009 records as walking straight past
    // tools/repo-checks.py. They are the reason this package exists rather than a wider
    // blacklist, so they are asserted directly.

    [Fact]
    public void A_using_alias_does_not_hide_the_clock()
    {
        var aliased = """
            using Clock = System.DateTime;

            public static class Consumer
            {
                public static object Resolve() => Clock.UtcNow;
            }
            """;

        Assert.Equal(new[] { "RK0002" }, AnalyzerHarness.Diagnose(aliased));
    }

    [Fact]
    public void An_extension_method_does_not_hide_its_receiver()
    {
        // The call site reads `1.Entropy()`, which names nothing banned. The analyzer sees
        // through to System.Random inside the extension, where a text match sees nothing.
        var hidden = """
            public static class Extensions
            {
                public static int Entropy(this int bound) => System.Random.Shared.Next(bound);
            }
            """;

        Assert.Equal(new[] { "RK0001" }, AnalyzerHarness.Diagnose(hidden));
    }

    [Fact]
    public void A_fully_qualified_name_split_across_a_using_is_still_found()
    {
        var split = """
            using System;

            public static class Consumer
            {
                public static object Resolve() => DateTime.Now;
            }
            """;

        Assert.Equal(new[] { "RK0002" }, AnalyzerHarness.Diagnose(split));
    }

    [Fact]
    public void A_local_function_wrapping_the_call_is_still_found()
    {
        var wrapped = """
            public static class Consumer
            {
                public static object Resolve()
                {
                    static object Inner() => System.Guid.NewGuid();
                    return Inner();
                }
            }
            """;

        Assert.Equal(new[] { "RK0001" }, AnalyzerHarness.Diagnose(wrapped));
    }

    [Fact]
    public void A_banned_member_captured_as_a_delegate_is_still_found()
    {
        // Not called, so there is no invocation to see. The analyzer registered four
        // operation kinds and MethodReference was not among them, so this reported nothing
        // while the same member called one line later reported RK0001.
        var captured = Wrap("System.Func<System.Guid> f = System.Guid.NewGuid;");

        Assert.Equal(new[] { "RK0001" }, AnalyzerHarness.Diagnose(captured));
    }

    [Fact]
    public void A_method_group_on_a_banned_receiver_reports_once_not_twice()
    {
        // The receiver already carries the finding. Two overlapping squiggles on one
        // expression make a suppression ambiguous, which is why Inspect yields to the
        // instance — a method group has to take that path rather than go around it.
        // `System.Random.Shared.Next` is the method-group form of the control case: the
        // property reference and the method reference are both over System.Random.
        var onReceiver = Wrap("System.Func<int> f = System.Random.Shared.Next;");

        Assert.Equal(new[] { "RK0001" }, AnalyzerHarness.Diagnose(onReceiver));
    }

    [Fact]
    public void Environment_TickCount_reports_as_a_clock_not_as_machine_state()
    {
        // Environment is banned as a whole type, but the member rule is checked first, so the
        // diagnostic a reader gets names the actual problem.
        Assert.Equal(new[] { "RK0002" }, AnalyzerHarness.Diagnose(Wrap("var x = System.Environment.TickCount;")));
    }

    // RK0005 is the one rule whose subject has a legitimate home. The assertions below are
    // what stop it from being a rule nobody can keep: ReplayCompatibilityIdentity in this
    // repository's own kernel writes exactly the first shape, and docs/decisions/0011 records
    // that a false positive costs a consumer more than a false negative, because they cannot
    // fix it -- only suppress it or drop the package.

    [Fact]
    public void HashCode_inside_a_GetHashCode_override_reports_nothing()
    {
        // This is ReplayCompatibilityIdentity.GetHashCode, near enough to copy. It is
        // correct .NET: a hashed collection wants a per-process bucket, not a fingerprint.
        var idiomatic = """
            public sealed class Identity
            {
                private readonly int _left;
                private readonly string _right;

                public Identity(int left, string right)
                {
                    _left = left;
                    _right = right;
                }

                public override int GetHashCode()
                {
                    var hash = new System.HashCode();
                    hash.Add(_left);
                    hash.Add(_right);
                    return hash.ToHashCode();
                }
            }
            """;

        Assert.Empty(AnalyzerHarness.Diagnose(idiomatic));
    }

    [Fact]
    public void Combine_and_a_member_hash_inside_a_GetHashCode_override_report_nothing()
    {
        var idiomatic = """
            public sealed class Identity
            {
                private readonly string _name;

                public Identity(string name) => _name = name;

                public override int GetHashCode() =>
                    System.HashCode.Combine(_name.GetHashCode(), 7);
            }
            """;

        Assert.Empty(AnalyzerHarness.Diagnose(idiomatic));
    }

    [Fact]
    public void A_lambda_inside_a_GetHashCode_override_reports_nothing()
    {
        // A lambda is its own IMethodSymbol, so a suppression that looked only at the
        // immediately enclosing symbol would report here -- on the same legitimate use, one
        // level down. The walk goes up ContainingSymbol for exactly this.
        var nested = """
            using System.Linq;

            public sealed class Identity
            {
                private readonly int[] _parts = new int[0];

                public override int GetHashCode() =>
                    _parts.Aggregate(0, (running, part) => System.HashCode.Combine(running, part));
            }
            """;

        Assert.Empty(AnalyzerHarness.Diagnose(nested));
    }

    [Fact]
    public void An_equality_comparer_implementation_reports_nothing()
    {
        // The signature is IEqualityComparer's, not the consumer's; there is nothing they
        // could restructure to avoid a diagnostic here. Both spellings are covered because
        // FindImplementationForInterfaceMember answers for the implicit form too.
        var comparer = """
            public sealed class OrdinalComparer : System.Collections.Generic.IEqualityComparer<string>
            {
                public bool Equals(string? left, string? right) => left == right;

                public int GetHashCode(string value) => System.HashCode.Combine(value.GetHashCode());
            }
            """;

        Assert.Empty(AnalyzerHarness.Diagnose(comparer));
    }

    [Fact]
    public void A_records_compiler_generated_equality_reports_nothing()
    {
        // The synthesised GetHashCode has no source body, so no operation action runs over
        // it. That is asserted rather than assumed: "reports nothing" is otherwise
        // indistinguishable from a rule that silently stopped working.
        var generated = """
            public sealed record Identity(string Name, int Revision);
            """;

        Assert.Empty(AnalyzerHarness.Diagnose(generated));
    }

    [Fact]
    public void The_same_call_outside_a_GetHashCode_override_is_still_reported()
    {
        // The negative fixtures above are only evidence if the positive one fires on the
        // identical expression. Seeding from a hash is the mistake docs/decisions/0005
        // records a predecessor engine writing a SplitMix64 finaliser to avoid.
        var seeding = Wrap("var seed = System.HashCode.Combine(name.GetHashCode(), 7);", parameters: "string name");

        Assert.Equal(new[] { "RK0005", "RK0005" }, AnalyzerHarness.Diagnose(seeding));
    }

    [Fact]
    public void A_helper_called_from_GetHashCode_is_still_reported()
    {
        // The line is lexical, and this is the edge it leaves over-reporting rather than
        // under-reporting. Following the call would need a call graph an operation action
        // does not have, and approximating one would suppress every hash reachable from any
        // GetHashCode in the compilation -- including the seed derivation above. Pinned as a
        // test so the trade-off is a decision on record rather than a surprise in a
        // consumer's build.
        var helper = """
            public sealed class Identity
            {
                private readonly string _name = "";

                public override int GetHashCode() => Mix(_name);

                private static int Mix(string value) => System.HashCode.Combine(value);
            }
            """;

        Assert.Equal(new[] { "RK0005" }, AnalyzerHarness.Diagnose(helper));
    }

    [Fact]
    public void A_using_alias_does_not_hide_ambient_culture()
    {
        // RK0006's half of what docs/decisions/0009 says a pattern blacklist cannot close.
        var aliased = """
            using Culture = System.Globalization.CultureInfo;

            public static class Consumer
            {
                public static object Resolve() => Culture.CurrentCulture;
            }
            """;

        Assert.Equal(new[] { "RK0006" }, AnalyzerHarness.Diagnose(aliased));
    }

    [Fact]
    public void Reading_a_member_of_the_ambient_culture_reports_once_not_twice()
    {
        // `CurrentCulture.Name` is the property reference on the banned member wrapped in
        // another property reference. The outer one yields, as it does for entropy.
        Assert.Equal(
            new[] { "RK0006" },
            AnalyzerHarness.Diagnose(Wrap("var x = System.Globalization.CultureInfo.CurrentCulture.Name;")));
    }

    [Fact]
    public void An_explicitly_passed_culture_reports_nothing()
    {
        // The fix RK0006 asks for has to be silent, or the rule teaches nothing.
        var passed = Wrap(
            "var x = value.ToString(culture);",
            parameters: "int value, System.Globalization.CultureInfo culture");

        Assert.Empty(AnalyzerHarness.Diagnose(passed));
    }

    [Fact]
    public void Each_occurrence_is_reported_in_source_order()
    {
        var several = Wrap("""
            var a = System.Random.Shared.Next();
            var b = System.DateTime.UtcNow;
            var c = System.Environment.ProcessorCount;
            """);

        Assert.Equal(new[] { "RK0001", "RK0002", "RK0003" }, AnalyzerHarness.Diagnose(several));
    }

    private static string Wrap(string body, string parameters = "") => $$"""
        public static class Consumer
        {
            public static void Resolve({{parameters}})
            {
                {{body}}
            }
        }
        """;
}
