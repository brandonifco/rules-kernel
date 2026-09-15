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
    [InlineData("var x = System.Globalization.CultureInfo.CurrentCulture.Name;", "RK0003")]
    [InlineData("var x = System.Globalization.CultureInfo.CurrentUICulture.Name;", "RK0003")]
    [InlineData("var x = System.TimeZoneInfo.Local;", "RK0003")]
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

    // RK0001 says two different things, and which one it says is the change docs/decisions/
    // 0015 held the tag for. The four below pin both sentences to the shapes they are true
    // of, because an id assertion alone cannot tell a true reason from a false one -- that is
    // exactly how the seeded case passed 358 tests and then embarrassed itself on the first
    // real codebase it met.

    [Fact]
    public void A_seeded_random_source_is_told_its_algorithm_is_unpinned_not_that_it_is_ambient()
    {
        // SeededRandomSource from SRD_Combat, near enough to copy. It is the disciplined
        // shape: the seam takes a seed and the interface's own contract forbids
        // Random.Shared. Telling this author they "draw from ambient entropy" is false, and
        // the finding survives only because the reason it now gives is true.
        var seeded = """
            public interface IRandomSource
            {
                int Roll(int sides);
            }

            public sealed class SeededRandomSource : IRandomSource
            {
                private readonly System.Random _random;

                public SeededRandomSource(int seed) => _random = new System.Random(seed);

                public int Roll(int sides) => _random.Next(1, sides + 1);
            }
            """;

        Assert.Equal(
            new[]
            {
                "RK0001: 'Random..ctor' is System.Random, whose algorithm is not guaranteed stable "
                    + "across runtime versions; a seed makes it repeatable within one runtime, not "
                    + "replayable across them, so take a source with a pinned algorithm as an "
                    + "argument instead",
                "RK0001: 'Random.Next' is System.Random, whose algorithm is not guaranteed stable "
                    + "across runtime versions; a seed makes it repeatable within one runtime, not "
                    + "replayable across them, so take a source with a pinned algorithm as an "
                    + "argument instead",
            },
            AnalyzerHarness.Describe(seeded));
    }

    [Fact]
    public void Random_Shared_still_reports_ambient_entropy()
    {
        // The half of RK0001 that was always accurate. Asserted beside the seeded case so
        // that narrowing the sentence there is visibly a split rather than a replacement.
        Assert.Equal(
            new[] { "RK0001: 'Random.Shared' draws from ambient entropy; take a seeded source as an argument instead" },
            AnalyzerHarness.Describe(Wrap("var x = System.Random.Shared.Next();")));
    }

    [Fact]
    public void A_parameterless_Random_constructor_still_reports_ambient_entropy()
    {
        // `new Random()` seeds itself from the operating system, so it is ambient in the
        // plain sense and the seeded reason would be the wrong one here. This is the line
        // between the two messages, and it is one constructor argument wide.
        Assert.Equal(
            new[] { "RK0001: 'Random..ctor' draws from ambient entropy; take a seeded source as an argument instead" },
            AnalyzerHarness.Describe(Wrap("var x = new System.Random().Next();")));
    }

    [Fact]
    public void Guid_NewGuid_still_reports_ambient_entropy()
    {
        // Nothing outside System.Random changed reason, and a rule that quietly gave every
        // member the new sentence would still pass the id assertions above.
        Assert.Equal(
            new[] { "RK0001: 'Guid.NewGuid' draws from ambient entropy; take a seeded source as an argument instead" },
            AnalyzerHarness.Describe(Wrap("var x = System.Guid.NewGuid();")));
    }

    [Fact]
    public void Environment_TickCount_reports_as_a_clock_not_as_machine_state()
    {
        // Environment was banned as a whole type when this was written, and the point was
        // that the member rule is checked first so the diagnostic names the actual problem.
        // RK0003 no longer bans the type, and the assertion is kept unchanged because the
        // answer must not depend on which table TickCount happened to be caught by.
        Assert.Equal(new[] { "RK0002" }, AnalyzerHarness.Diagnose(Wrap("var x = System.Environment.TickCount;")));
    }

    // RK0003 was four-fifths noise on the first real codebase it met: eight of ten findings
    // were Environment.NewLine in console output and one was Environment.Exit, reported as a
    // read it does not perform (docs/decisions/0015). The pairs below are what narrowing it
    // has to mean -- each silence is asserted beside a near-identical fixture that still
    // fires, because "reports nothing" is otherwise indistinguishable from a rule that
    // stopped running.

    [Fact]
    public void Environment_NewLine_in_console_output_reports_nothing()
    {
        // Display.cs from SRD_Combat, reduced. A line separator concatenated into terminal
        // output is not a rules result, and the old message told this author it was.
        var display = """
            public static class Display
            {
                public static void WriteLine(string line) =>
                    System.Console.Write(line + System.Environment.NewLine);
            }
            """;

        Assert.Empty(AnalyzerHarness.Diagnose(display));
    }

    [Fact]
    public void A_machine_fact_in_the_same_console_output_is_still_reported()
    {
        // Character for character the fixture above, with the one member swapped. What makes
        // a result machine-dependent is the value, not where it is written.
        var display = """
            public static class Display
            {
                public static void WriteLine(string line) =>
                    System.Console.Write(line + System.Environment.MachineName);
            }
            """;

        Assert.Equal(new[] { "RK0003" }, AnalyzerHarness.Diagnose(display));
    }

    [Fact]
    public void Environment_Exit_reports_nothing()
    {
        // Process control. It reads nothing, so a rule about reading ambient machine state
        // had nothing true to say about it.
        var handler = """
            public static class Probe
            {
                public static void Fail() => System.Environment.Exit(1);
            }
            """;

        Assert.Empty(AnalyzerHarness.Diagnose(handler));
    }

    [Fact]
    public void A_fault_handler_that_also_reads_the_environment_reports_once()
    {
        // The same exit path, one line longer. Exactly one diagnostic, on the read, which is
        // what proves the silence above is about Exit rather than about fault handlers.
        var handler = """
            public static class Probe
            {
                public static void Fail()
                {
                    System.Console.Error.Write(System.Environment.CurrentDirectory);
                    System.Environment.Exit(1);
                }
            }
            """;

        Assert.Equal(new[] { "RK0003" }, AnalyzerHarness.Diagnose(handler));
    }

    [Theory]
    [InlineData("var x = System.Environment.ProcessorCount;", "Environment.ProcessorCount")]
    [InlineData("var x = System.Environment.GetEnvironmentVariable(\"HOME\");", "Environment.GetEnvironmentVariable")]
    [InlineData("var x = System.Environment.ProcessPath;", "Environment.ProcessPath")]
    [InlineData("var x = System.Environment.CurrentDirectory;", "Environment.CurrentDirectory")]
    [InlineData(
        "var x = System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData);",
        "Environment.GetFolderPath")]
    public void The_members_that_make_a_result_machine_dependent_still_report(string statement, string member)
    {
        // The half of RK0003 that was always worth having, including the one finding in the
        // calibration that was not noise (GetFolderPath). The message is asserted too: the
        // clause it used to end with claimed the consumer's expression was a rules result.
        Assert.Equal(
            new[]
            {
                "RK0003: '" + member + "' reads ambient machine state; a result derived from it "
                    + "differs by machine, so pass the value in as an argument instead",
            },
            AnalyzerHarness.Describe(Wrap(statement)));
    }

    [Fact]
    public void The_enum_that_names_a_folder_is_not_itself_a_read()
    {
        // Environment.SpecialFolder.ApplicationData is a constant on a nested type. Under
        // the whole-type ban it matched; it is named here because the fixture above passes
        // it as an argument, and one diagnostic there rather than two is the evidence.
        Assert.Empty(
            AnalyzerHarness.Diagnose(Wrap("var x = System.Environment.SpecialFolder.ApplicationData;")));
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

    // Ambient culture and time zone were RK0006 until docs/decisions/0016 retired that id
    // and folded them into RK0003. The three below are the tests that already existed,
    // asserting the new id -- the expectation change is the fold itself, and it is meant to
    // be the conspicuous part of this diff rather than an incidental edit.

    [Fact]
    public void A_using_alias_does_not_hide_ambient_culture()
    {
        // Ambient culture's half of what docs/decisions/0009 says a pattern blacklist
        // cannot close.
        var aliased = """
            using Culture = System.Globalization.CultureInfo;

            public static class Consumer
            {
                public static object Resolve() => Culture.CurrentCulture;
            }
            """;

        Assert.Equal(new[] { "RK0003" }, AnalyzerHarness.Diagnose(aliased));
    }

    [Fact]
    public void Reading_a_member_of_the_ambient_culture_reports_once_not_twice()
    {
        // `CurrentCulture.Name` is the property reference on the banned member wrapped in
        // another property reference. The outer one yields, as it does for entropy.
        Assert.Equal(
            new[] { "RK0003" },
            AnalyzerHarness.Diagnose(Wrap("var x = System.Globalization.CultureInfo.CurrentCulture.Name;")));
    }

    [Fact]
    public void An_explicitly_passed_culture_reports_nothing()
    {
        // The fix this asks for has to be silent, or the rule teaches nothing.
        var passed = Wrap(
            "var x = value.ToString(culture);",
            parameters: "int value, System.Globalization.CultureInfo culture");

        Assert.Empty(AnalyzerHarness.Diagnose(passed));
    }

    [Theory]
    [InlineData("var x = System.Globalization.CultureInfo.CurrentCulture;", "CultureInfo.CurrentCulture")]
    [InlineData("var x = System.Globalization.CultureInfo.CurrentUICulture;", "CultureInfo.CurrentUICulture")]
    [InlineData("var x = System.TimeZoneInfo.Local;", "TimeZoneInfo.Local")]
    public void Ambient_culture_and_time_zone_report_under_RK0003_with_a_message_true_of_them(
        string statement, string member)
    {
        // Retiring an id is only free if the coverage survives it, and an id assertion alone
        // would not catch a fold that kept the finding while leaving it a sentence written
        // for System.Environment. Every member of the merged rule has to make one message
        // true, so the message is asserted on the folded-in members specifically.
        Assert.Equal(
            new[]
            {
                "RK0003: '" + member + "' reads ambient machine state; a result derived from it "
                    + "differs by machine, so pass the value in as an argument instead",
            },
            AnalyzerHarness.Describe(Wrap(statement)));
    }

    [Fact]
    public void The_invariant_culture_reports_nothing()
    {
        // InvariantCulture is the fix, one member along from CurrentCulture on the same
        // type. The near-identical positive above is what makes this silence evidence that
        // the rule is running and discriminating, rather than evidence that folding RK0006
        // in dropped CultureInfo on the floor.
        var passed = Wrap(
            "var x = value.ToString(System.Globalization.CultureInfo.InvariantCulture);",
            parameters: "int value");

        Assert.Empty(AnalyzerHarness.Diagnose(passed));
    }

    [Fact]
    public void A_fixed_time_zone_is_silent_in_the_same_fixture_where_the_local_one_reports()
    {
        // TimeZoneInfo.Utc is not the machine's zone and TimeZoneInfo.Local is, and after
        // the fold they are two members of one type that is not banned as a whole. Both
        // spellings in one fixture, so the single diagnostic is evidence about which member
        // matched rather than a count that two separate tests could each satisfy wrongly.
        Assert.Equal(
            new[] { "RK0003" },
            AnalyzerHarness.Diagnose(Wrap("""
                var utc = System.TimeZoneInfo.Utc;
                var here = System.TimeZoneInfo.Local;
                """)));
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
