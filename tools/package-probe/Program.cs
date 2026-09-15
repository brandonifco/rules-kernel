// Program.cs -- the body of the smoke consumer that tools/package-probe/check.sh builds
// and runs against the packed packages, on every framework docs/decisions/0008 commits to.
//
// This file is not part of any project in RulesKernel.slnx and is not compiled by the
// gate's build. check.sh copies it into a temporary project whose only references are
// PackageReferences to the .nupkg files it just produced. That is the entire point: every
// type named below is reached through a restored package, exactly as an engine reaches
// them, rather than through a ProjectReference that would prove nothing about packaging.
//
// What it asserts, and why these and not others:
//
//   Identity     -- the types an engine has to construct before it can record anything,
//                   including the two things that are easy to lose in a repackage and
//                   silent when lost: ordered, not set-like, source baselines, and the
//                   argument validation that rejects a struct default. If a trimmed or
//                   rewritten assembly dropped those guards, every assertion that the
//                   types merely EXIST would still pass.
//   Resolution   -- the union an engine returns, matched exhaustively, including the
//                   non-ASCII citation shape a non-tabletop corpus uses.
//   Randomness   -- the canonical PCG32 seed-42/stream-54 sequence from the upstream
//                   reference implementation. These numbers are not this repository's own
//                   output recorded and played back: they are in upstream's own
//                   check-pcg32.out, and tests/RulesKernel.Randomness.Tests/Pcg32ReferenceVectors.cs
//                   carries the same row. A package that restores, compiles and runs while
//                   producing different draws has broken the replay contract for every
//                   seed anyone has ever stored, and nothing about its shape would say so.
//   Draw counts  -- how many values a bounded draw consumes is part of that contract
//                   (docs/decisions/0006), so the rejection path is driven deliberately
//                   rather than left to chance.
//   Provenance   -- the running assemblies report the version check.sh just packed, and
//                   the process is on the runtime the target framework names. Without
//                   these two, a consumer silently binding to some other copy of the
//                   kernel, or rolling forward onto a different runtime, would pass
//                   everything above and prove none of it about the packages under test.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using RulesKernel.Identity;
using RulesKernel.Provenance;
using RulesKernel.Randomness;
using RulesKernel.Resolution;
using RulesKernel.Testing;

internal static class Program
{
    // The canonical PCG32 (pcg_setseq_64_xsh_rr_32) sequence for seed 42, stream 54.
    private const uint Canonical0 = 0xA15C02B7U;
    private const uint Canonical1 = 0x7B47F409U;
    private const uint Canonical2 = 0xBA1D3330U;
    private const uint Canonical3 = 0x83D2F293U;

    // Deliberately synthetic, and uppercase on input so the constructor's normalisation is
    // observable in ToString rather than merely asserted to have happened.
    private const string PrimaryHashAsGiven =
        "ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789";
    private const string PrimaryHashNormalised =
        "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";
    private const string SupplementHash =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static int _passed;
    private static int _failed;

    private static void Assert(bool condition, string what, string? actual = null)
    {
        if (condition)
        {
            _passed++;
            return;
        }

        _failed++;
        Console.WriteLine(
            actual is null
                ? "ASSERTION FAILED: " + what
                : "ASSERTION FAILED: " + what + " -- actual: " + actual);
    }

    private static void AssertThrows<TException>(Action action, string what)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            Assert(true, what);
            return;
        }
        catch (Exception unexpected)
        {
            Assert(false, what, unexpected.GetType().Name);
            return;
        }

        Assert(false, what, "nothing was thrown");
    }

    private static int Main(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Contains("--negative-control", StringComparer.Ordinal))
        {
            // The counterpart run. Everything below this point in a normal run is only
            // evidence if the assertion machinery actually bites, and the only way to show
            // that is to make a real draw and hold it against an expectation that is known
            // to be false. If this run exits 0, check.sh fails the whole probe.
            var control = Pcg32.FromSeed(42UL, 54UL);
            uint drawn = control.NextUInt32();
            Assert(
                drawn == 0xDEADBEEFU,
                "negative control: the canonical first draw is deliberately expected to be "
                + "0xDEADBEEF, which it is not",
                "0x" + drawn.ToString("X8", System.Globalization.CultureInfo.InvariantCulture));
            return _failed == 0 ? 0 : 1;
        }

        Identity();
        Resolutions();
        Randomness();
        Provenance();

        if (_failed > 0)
        {
            Console.WriteLine(
                "package-probe: " + _failed.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " assertion(s) failed");
            return 1;
        }

        Console.WriteLine(
            "package-probe: " + _passed.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + " assertion(s) passed on " + RuntimeInformation.FrameworkDescription
            + " (" + Expected.TargetFramework + ")");
        return 0;
    }

    private static ReplayCompatibilityIdentity BuildIdentity(
        IEnumerable<SourceBaselineId> baselines, int rulesetRevision, RandomAlgorithmId? algorithm) =>
        new(
            new RulesetVersion("smoke-consumer", rulesetRevision),
            new ReplaySchemaVersion(2),
            baselines,
            algorithm);

    private static void Identity()
    {
        // A corpus pinned to a date and a corpus with no temporal axis, in that order --
        // the shape a non-tabletop engine actually has, and the one probes/README.md says
        // the kernel's own two engines cannot produce.
        var primary = new SourceBaselineId(
            "corpus-primary", PrimaryHashAsGiven, "extracted-json", new DateOnly(2026, 1, 1));
        var supplement = new SourceBaselineId("corpus-supplement", SupplementHash, "id-roster");

        var identity = BuildIdentity(
            new[] { primary, supplement }, 3, RandomAlgorithmId.Pcg32SetSeq64XshRr32);

        Assert(identity.SourceBaselines.Length == 2, "an identity carries both source baselines");
        Assert(
            identity.SourceBaselines[0].ToString()
                == "corpus-primary@2026-01-01#extracted-json:" + PrimaryHashNormalised,
            "a dated baseline renders its corpus, date, derivation and lowercased hash",
            identity.SourceBaselines[0].ToString());
        Assert(
            identity.SourceBaselines[1].ToString()
                == "corpus-supplement#id-roster:" + SupplementHash,
            "an undated baseline renders without a date",
            identity.SourceBaselines[1].ToString());

        Assert(
            identity == BuildIdentity(
                new[] { primary, supplement }, 3, RandomAlgorithmId.Pcg32SetSeq64XshRr32),
            "two identities built from the same components compare equal");
        // Order is the evidence, so the reversed list is a different identity rather than
        // the same one seen differently.
        Assert(
            identity != BuildIdentity(
                new[] { supplement, primary }, 3, RandomAlgorithmId.Pcg32SetSeq64XshRr32),
            "the same baselines in the other order are a different identity");
        Assert(
            identity != BuildIdentity(
                new[] { primary, supplement }, 4, RandomAlgorithmId.Pcg32SetSeq64XshRr32),
            "a bumped ruleset revision is a different identity");

        Assert(
            identity.RandomAlgorithm is { } algorithm
                && algorithm.Name == "pcg_setseq_64_xsh_rr_32",
            "the named random algorithm survives the round trip through the package",
            identity.RandomAlgorithm?.Name);
        Assert(
            BuildIdentity(new[] { primary }, 3, null).IsDeterministicWithoutRandomness,
            "an engine that consumes no randomness can say so");

        // Struct defaults bypass every constructor, so the gates that reject them are
        // behaviour, not shape. An assembly that still exposes these types but no longer
        // enforces this would pass every assertion above.
        AssertThrows<ArgumentException>(
            () => BuildIdentity(
                new[] { default(SourceBaselineId) }, 3, null),
            "a default(SourceBaselineId) is rejected rather than compared");
        AssertThrows<ArgumentException>(
            () => new ReplayCompatibilityIdentity(
                default, new ReplaySchemaVersion(2), new[] { primary }),
            "a default(RulesetVersion) is rejected rather than compared");
        AssertThrows<ArgumentException>(
            () => BuildIdentity(new[] { primary, primary }, 3, null),
            "two baselines naming one corpus are rejected as ambiguous");
    }

    private static void Resolutions()
    {
        // A designation-style citation, non-ASCII on purpose: a statute or regulation is
        // located this way and a page number cannot express it.
        var locator = new SourceLocator("corpus-primary", "§ 4.2.1(b)");

        Resolution<int> resolved = Resolution<int>.FromValue(23_000);
        Resolution<int> unresolved = Resolution<int>.FromUnresolved(new UnresolvedResult(
            UnresolvedReason.MissingRulesData, "quota for period 2031", locator));

        Assert(resolved.IsResolved, "a resolved outcome reports itself resolved");
        Assert(!unresolved.IsResolved, "an unresolved outcome reports itself unresolved");
        Assert(
            resolved.Match(v => "resolved:" + v.ToString(System.Globalization.CultureInfo.InvariantCulture),
                           u => "unresolved:" + u.Reason) == "resolved:23000",
            "Match routes a resolved outcome to the resolved branch");
        Assert(
            unresolved.Match(v => "resolved:" + v.ToString(System.Globalization.CultureInfo.InvariantCulture),
                             u => "unresolved:" + u.Reason) == "unresolved:MissingRulesData",
            "Match routes an unresolved outcome to the unresolved branch");

        // Positional matching is documented, so it is part of the surface a consumer binds
        // against -- and a deconstructor is exactly the kind of member that disappears in a
        // refactor without a source break anywhere in this repository.
        string matched = unresolved switch
        {
            Resolution<int>.Resolved(var value) =>
                value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Resolution<int>.Unresolved(var result) => result.ToString(),
            _ => "unreachable",
        };
        Assert(
            matched == "MissingRulesData: quota for period 2031 [corpus-primary / § 4.2.1(b)]",
            "an unresolved result deconstructs and renders its reason, attempt and citation",
            matched);

        AssertThrows<ArgumentException>(
            () => new UnresolvedResult(
                UnresolvedReason.UnsupportedRule, "a gap with no citation", default),
            "an unresolved result without a citation is rejected");
    }

    private static void Randomness()
    {
        var rng = Pcg32.FromSeed(42UL, 54UL);
        uint[] first = { rng.NextUInt32(), rng.NextUInt32(), rng.NextUInt32() };
        Assert(
            first[0] == Canonical0 && first[1] == Canonical1 && first[2] == Canonical2,
            "the packaged generator reproduces the canonical seed-42/stream-54 sequence",
            string.Join(",", first.Select(v => "0x" + v.ToString(
                "X8", System.Globalization.CultureInfo.InvariantCulture))));

        // Resuming mid-sequence is the whole reason Pcg32State is public: a replay that can
        // only restart from a fresh seed cannot resume a recorded session.
        Pcg32State captured = rng.GetState();
        var resumed = Pcg32.FromState(captured);
        Assert(resumed.GetState() == captured, "a captured state round-trips unchanged");
        Assert(
            resumed.NextUInt32() == Canonical3,
            "a generator resumed from a captured state continues the same sequence");

        var bounded = Pcg32.FromSeed(42UL, 54UL);
        uint[] below = { UniformInt.Below(bounded, 6), UniformInt.Below(bounded, 6),
                         UniformInt.Below(bounded, 6), UniformInt.Below(bounded, 6) };
        Assert(
            below[0] == 3 && below[1] == 3 && below[2] == 2 && below[3] == 1,
            "bounded draws map the canonical sequence to the documented values",
            string.Join(",", below.Select(v => v.ToString(
                System.Globalization.CultureInfo.InvariantCulture))));

        Assert(
            UniformInt.InRange(Pcg32.FromSeed(42UL, 54UL), 1, 20) == 4,
            "a ranged draw offsets into its window");

        // A one-value window is already determined, and consuming a draw for it would make
        // the draw count depend on a window size that cannot affect the result. An empty
        // scripted source proves no draw was taken: it throws on the first one.
        var noDraws = new FixedSequenceRandomSource(Array.Empty<uint>());
        Assert(
            UniformInt.InRange(noDraws, 7, 7) == 7 && noDraws.Consumed == 0,
            "a single-value window consumes no draw");

        // Draw count is part of the observable contract, so the rejection path is driven
        // deliberately: 4294967293 is at or above the acceptance limit for a bound of six
        // (2^32 - 4), so it is rejected and costs an extra draw.
        var scripted = new FixedSequenceRandomSource(new uint[] { 5U, 4294967293U, 2U });
        Assert(
            UniformInt.Below(scripted, 6) == 5 && scripted.Consumed == 1,
            "an accepted raw value costs exactly one draw",
            scripted.Consumed.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert(
            UniformInt.Below(scripted, 6) == 2 && scripted.Consumed == 3,
            "a rejected raw value costs an extra draw",
            scripted.Consumed.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AssertThrows<InvalidOperationException>(
            () => scripted.NextUInt32(),
            "an exhausted scripted source fails loudly rather than wrapping");

        AssertThrows<ArgumentOutOfRangeException>(
            () => UniformInt.Below(new FixedSequenceRandomSource(new uint[] { 1U }), 0),
            "a zero bound is rejected");
        AssertThrows<ArgumentException>(
            () => Pcg32.FromState(default),
            "a default(Pcg32State) -- an even increment -- is rejected by FromState");
    }

    private static void Provenance()
    {
        // Which bytes actually ran. Every assertion above is about behaviour, and behaviour
        // proves nothing about PACKAGING unless the assemblies that produced it are the
        // ones check.sh just packed. An informational version carrying the probe's
        // timestamped prerelease suffix can only have come from this run's pack.
        AssertPackagedAssembly(typeof(SourceLocator).Assembly, "RulesKernel");
        AssertPackagedAssembly(typeof(Pcg32).Assembly, "RulesKernel.Randomness");
        AssertPackagedAssembly(typeof(FixedSequenceRandomSource).Assembly, "RulesKernel.Testing");

        // The framework the process is really on. A net8.0 consumer that rolled forward
        // onto a .NET 10 runtime would satisfy everything else while testing the same
        // thing twice, and docs/decisions/0008 is a commitment about net8.0 specifically.
        Assert(
            RuntimeInformation.FrameworkDescription.StartsWith(
                Expected.RuntimePrefix, StringComparison.Ordinal),
            "a " + Expected.TargetFramework + " consumer ran on " + Expected.RuntimePrefix,
            RuntimeInformation.FrameworkDescription);
    }

    private static void AssertPackagedAssembly(Assembly assembly, string expectedName)
    {
        string? informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        Assert(
            assembly.GetName().Name == expectedName
                && informational is not null
                && informational.StartsWith(Expected.PackageVersion, StringComparison.Ordinal),
            expectedName + " was bound from the package this run produced",
            assembly.GetName().Name + " " + (informational ?? "(no informational version)"));
    }
}
