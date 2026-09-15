using System;
using System.Linq;
using System.Reflection;
using RulesKernel.Identity;
using RulesKernel.Provenance;
using RulesKernel.Resolution;

namespace Part107Probe;

/// <summary>
/// The kernel's current behaviour at the points where this engine found it uncomfortable.
/// These tests pass by asserting what the kernel does today. Each one is a finding in
/// FINDINGS.md, and a change to the kernel that resolves a finding should fail its test,
/// so the finding is revisited rather than quietly outlived.
/// </summary>
public sealed class KernelPressureTests
{
    // ------------------------------------------------ finding 1: two engines, one corpus

    [Fact]
    public void The_same_bytes_pinned_by_two_engines_compare_equal_only_if_they_spell_the_derivation_alike()
    {
        // What faa-part-107's generated MapEntries.Baseline constructs, value for value.
        var faaPart107 = new SourceBaselineId(
            "cfr-14-107", "80f6bc4b002df9dcc60a651fec30a2dc3590081cc3e5fd431d9885c69b7ce35e",
            "ecfr-versioner-xml", new DateOnly(2026, 1, 1));

        Assert.Equal(faaPart107, Corpus.Regulation);

        // Identical bytes and an identical method, spelled differently.
        var respelled = new SourceBaselineId(Corpus.RegulationId, Corpus.RegulationHash, "eCFR-versioner-XML", Corpus.RegulationAsOf);
        Assert.NotEqual(faaPart107, respelled);
    }

    [Fact]
    public void A_derivation_with_a_trailing_space_is_accepted_and_compares_unequal()
    {
        // SourceId rejects surrounding whitespace for exactly this reason (SourceLocator's
        // ThrowIfNotACanonicalSourceId); HashDerivation, compared the same way, does not.
        var padded = new SourceBaselineId(Corpus.RegulationId, Corpus.RegulationHash, Corpus.RegulationDerivation + " ", Corpus.RegulationAsOf);

        Assert.NotEqual(Corpus.Regulation, padded);
    }

    // --------------------------------------------- finding 2: one corpus, two moments

    [Fact]
    public void An_identity_cannot_pin_two_snapshots_of_one_regulation()
    {
        // An engine answering for operations on both sides of an amendment needs the text
        // as of each. FABRICATED hash for the earlier snapshot, which this probe does not hold.
        var earlier = new SourceBaselineId(
            Corpus.RegulationId, new string('e', 64), Corpus.RegulationDerivation, new DateOnly(2021, 1, 1));

        Assert.Throws<ArgumentException>(() => new ReplayCompatibilityIdentity(
            new RulesetVersion("part107-probe", 1), new ReplaySchemaVersion(1), [earlier, Corpus.Regulation]));
    }

    [Fact]
    public void Nothing_in_an_identity_records_the_date_range_its_snapshot_is_trusted_for()
    {
        // The snapshot is as of 2026-01-01; the engine answers from 2021-04-21. The second
        // date is an engine assumption, and SourceBaselineId has nowhere to put it.
        Assert.Equal(
            ["AsOf", "ContentHash", "HashDerivation", "IsValid", "SourceId"],
            typeof(SourceBaselineId).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(property => property.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(Corpus.RegulationAsOf, new Part107Engine(false).Identity.SourceBaselines.Single().AsOf);
    }

    // ---------------------------------------------------- finding 3: order and precedence

    [Fact]
    public void Listing_the_same_corpora_in_the_other_order_is_a_different_identity()
    {
        // The engine applies the regulation before its own interpretation because the
        // interpretation reads the regulation, not because of where either sits in the list.
        var declared = new ReplayCompatibilityIdentity(
            new RulesetVersion("part107-probe", 1), new ReplaySchemaVersion(1), [Corpus.Regulation, Corpus.Interpretations]);
        var reversed = new ReplayCompatibilityIdentity(
            new RulesetVersion("part107-probe", 1), new ReplaySchemaVersion(1), [Corpus.Interpretations, Corpus.Regulation]);

        Assert.Equal(new Part107Engine(true).Identity, declared);
        Assert.NotEqual(declared, reversed);
    }

    [Fact]
    public void Adopting_an_interpretation_changes_the_identity_without_changing_the_ruleset_version()
    {
        var strict = new Part107Engine(false).Identity;
        var interpreting = new Part107Engine(true).Identity;

        Assert.Equal(strict.Ruleset, interpreting.Ruleset);
        Assert.NotEqual(strict, interpreting);
    }

    // ------------------------------------------------ finding 4: a resolved answer's source

    [Fact]
    public void A_resolved_outcome_carries_no_citation_and_an_unresolved_one_must()
    {
        static bool Cites(Type type) =>
            type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Any(property => property.PropertyType == typeof(SourceLocator) || property.PropertyType == typeof(UnresolvedResult));

        Assert.False(Cites(typeof(Resolution<bool>.Resolved)));
        Assert.True(Cites(typeof(Resolution<bool>.Unresolved)));
    }

    // ---------------------------------------- finding 5: citing a corpus nobody pinned

    [Fact]
    public void An_unresolved_result_may_cite_a_corpus_absent_from_the_engines_identity()
    {
        var engine = new Part107Engine(false);
        var gap = engine.RecencyCurrent(new KnowledgeRecord(KnowledgeEvent.Part61Training, new DateOnly(2024, 1, 1)), new DateOnly(2024, 7, 1))
            .Match(_ => throw new Xunit.Sdk.XunitException("resolved"), result => result);

        Assert.DoesNotContain(engine.Identity.SourceBaselines, baseline => baseline.SourceId == gap.Locator.SourceId);
    }
}
