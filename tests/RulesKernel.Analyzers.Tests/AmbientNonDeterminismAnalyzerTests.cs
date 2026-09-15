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
