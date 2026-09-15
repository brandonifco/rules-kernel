using System;
using System.Linq;
using RulesKernel.Provenance;
using RulesKernel.Resolution;

namespace Part107Probe;

/// <summary>
/// The engine's answers, including every unresolved branch it can reach. Each unresolved
/// case is asserted by reason and by citation, because a gap reported with the wrong reason
/// or pointing at the wrong passage is as unactionable as one not reported at all.
/// </summary>
public sealed class RuleTests
{
    private static readonly DateOnly InScope = new(2024, 7, 10);
    private static readonly Part107Engine Strict = new(adoptsRecordedInterpretations: false);
    private static readonly Part107Engine Interpreting = new(adoptsRecordedInterpretations: true);

    private static readonly KnowledgeRecord Trained2023 = new(KnowledgeEvent.RecurrentTraining, new DateOnly(2023, 2, 1));
    private static readonly KnowledgeRecord Tested2021 = new(KnowledgeEvent.InitialTest, new DateOnly(2021, 1, 1));

    // Sunrise 06:00 and sunset 20:00; civil twilight per § 107.29(c) is 05:30-06:00 and 20:00-20:30.
    private static LightConditions At(int hour, int minute, TimeOnly? eveningAlmanac = null, bool alaska = false) =>
        new(new TimeOnly(hour, minute), new TimeOnly(6, 0), new TimeOnly(20, 0), alaska,
            EveningCivilTwilightEnds: eveningAlmanac);

    private static T Value<T>(Resolution<Finding<T>> resolution) =>
        resolution.Match(finding => finding.Value, gap => throw new Xunit.Sdk.XunitException($"unresolved: {gap}"));

    private static SourceLocator[] Authorities<T>(Resolution<Finding<T>> resolution) =>
        resolution.Match(finding => finding.Authorities.ToArray(), gap => throw new Xunit.Sdk.XunitException($"unresolved: {gap}"));

    private static UnresolvedResult Gap<T>(Resolution<T> resolution) =>
        resolution.Match(value => throw new Xunit.Sdk.XunitException($"resolved: {value}"), gap => gap);

    // ------------------------------------------------------------------------ § 107.65

    [Theory]
    [InlineData(2022, 8, 31, true)]   // 23 calendar months before July 2024: current however they are counted
    [InlineData(2022, 6, 30, false)]  // 25: not current however they are counted
    public void Recency_answers_wherever_both_readings_of_calendar_months_agree(int year, int month, int day, bool current)
    {
        var record = new KnowledgeRecord(KnowledgeEvent.InitialTest, new DateOnly(year, month, day));

        Assert.Equal(current, Value(Strict.RecencyCurrent(record, InScope)));
        Assert.Equal(current, Value(Interpreting.RecencyCurrent(record, InScope)));
    }

    [Fact]
    public void The_twenty_fourth_month_is_the_engines_to_interpret_and_it_cites_that_it_did()
    {
        var record = new KnowledgeRecord(KnowledgeEvent.RecurrentTraining, new DateOnly(2022, 7, 1));
        var operation = new DateOnly(2024, 7, 31);

        var strict = Gap(Strict.RecencyCurrent(record, operation));
        Assert.Equal(UnresolvedReason.RequiresInterpretation, strict.Reason);
        Assert.Equal(Corpus.Cite("§ 107.65"), strict.Locator);

        var interpreted = Interpreting.RecencyCurrent(record, operation);
        Assert.True(Value(interpreted));
        Assert.Equal(
            [Corpus.RegulationId, Corpus.InterpretationsId],
            Authorities(interpreted).Select(locator => locator.SourceId).ToArray());
    }

    [Fact]
    public void A_recurrent_test_counts_only_when_passed_before_April_6_2021()
    {
        var before = new KnowledgeRecord(KnowledgeEvent.RecurrentTest, new DateOnly(2021, 4, 5));
        var onTheDay = new KnowledgeRecord(KnowledgeEvent.RecurrentTest, new DateOnly(2021, 4, 6));
        var operation = new DateOnly(2022, 1, 15);

        Assert.True(Value(Strict.RecencyCurrent(before, operation)));
        Assert.False(Value(Strict.RecencyCurrent(onTheDay, operation)));
        Assert.Contains(Corpus.Cite("§ 107.65(d)"), Authorities(Strict.RecencyCurrent(onTheDay, operation)));
    }

    [Fact]
    public void Part_61_training_depends_on_a_corpus_the_engine_does_not_pin()
    {
        var gap = Gap(Strict.RecencyCurrent(new KnowledgeRecord(KnowledgeEvent.Part61Training, new DateOnly(2024, 1, 1)), InScope));

        Assert.Equal(UnresolvedReason.OutsideCurrentScope, gap.Reason);
        Assert.Equal(Corpus.PilotCertificationId, gap.Locator.SourceId);
    }

    [Fact]
    public void A_record_from_after_the_operation_is_a_caller_defect_not_an_unresolved_result()
    {
        Assert.Throws<ArgumentException>(() =>
            Strict.RecencyCurrent(new KnowledgeRecord(KnowledgeEvent.InitialTest, InScope.AddDays(1)), InScope));
    }

    // ----------------------------------------------------------- the snapshot's date range

    [Theory]
    [InlineData(2021, 4, 20)]
    [InlineData(2026, 1, 2)]
    public void An_operation_the_pinned_snapshot_does_not_cover_is_outside_scope(int year, int month, int day)
    {
        var date = new DateOnly(year, month, day);

        Assert.Equal(UnresolvedReason.OutsideCurrentScope, Gap(Strict.RecencyCurrent(Tested2021, date)).Reason);
        Assert.Equal(UnresolvedReason.OutsideCurrentScope, Gap(Strict.MayOperate(date, At(12, 0), Tested2021, 3, true)).Reason);
    }

    [Fact]
    public void Both_ends_of_the_snapshot_range_are_in_scope()
    {
        Assert.True(Value(Strict.MayOperate(Corpus.TextInForceSince, At(12, 0), Tested2021, null, null)));
        Assert.True(Value(Strict.MayOperate(Corpus.RegulationAsOf, At(12, 0), Tested2021, null, null)));
    }

    // ---------------------------------------------------------------- § 107.29 periods

    [Fact]
    public void Civil_twilight_is_the_thirty_minutes_paragraph_c_names_and_needs_lighting()
    {
        var period = Part107Engine.Period(At(20, 30));
        Assert.Equal((Part107Engine.LightPeriod.CivilTwilight, Corpus.Cite("§ 107.29(c)(2)")), period.Match(p => p, _ => default));

        Assert.False(Value(Strict.MayOperate(InScope, At(20, 15), Trained2023, null, null)));
        Assert.True(Value(Strict.MayOperate(InScope, At(20, 15), Trained2023, 3.0, true)));
        Assert.Contains(Corpus.Cite("§ 107.29(b)"), Authorities(Strict.MayOperate(InScope, At(5, 45), Trained2023, 3.0, true)));
    }

    [Fact]
    public void Beyond_the_thirty_minutes_night_is_defined_by_an_almanac_the_engine_does_not_hold()
    {
        var gap = Gap(Part107Engine.Period(At(20, 31)));

        Assert.Equal(UnresolvedReason.MissingRulesData, gap.Reason);
        Assert.Equal(new SourceLocator(Corpus.DefinitionsId, "§ 1.1 Night"), gap.Locator);
    }

    [Fact]
    public void A_time_in_neither_paragraph_c_twilight_nor_part_1_night_is_an_unresolved_interaction()
    {
        // Almanac twilight ending at 20:40 leaves 20:31-20:40 in neither definition.
        var gap = Gap(Strict.MayOperate(InScope, At(20, 35, eveningAlmanac: new TimeOnly(20, 40)), Trained2023, 3.0, true));

        Assert.Equal(UnresolvedReason.UnsupportedInteraction, gap.Reason);
        Assert.Equal(Corpus.Cite("§ 107.29(c)"), gap.Locator);
    }

    [Fact]
    public void Alaska_twilight_is_missing_rules_data()
    {
        Assert.Equal(UnresolvedReason.MissingRulesData, Gap(Part107Engine.Period(At(20, 15, alaska: true))).Reason);
    }

    // ------------------------------------------------------------------ § 107.29(a) night

    private static LightConditions Night => At(22, 0, eveningAlmanac: new TimeOnly(20, 40));

    [Fact]
    public void Night_needs_training_completed_after_April_6_2021_and_lighting()
    {
        var onTheDay = new KnowledgeRecord(KnowledgeEvent.InitialTest, new DateOnly(2021, 4, 6));
        var dayAfter = new KnowledgeRecord(KnowledgeEvent.InitialTest, new DateOnly(2021, 4, 7));

        Assert.False(Value(Strict.MayOperate(InScope, Night, onTheDay, 3.0, true)));
        Assert.True(Value(Strict.MayOperate(InScope, Night, dayAfter, 3.0, true)));
        Assert.False(Value(Strict.MayOperate(InScope, Night, dayAfter, 2.9, true)));
    }

    [Fact]
    public void A_recurrent_test_keeps_a_pilot_current_and_does_not_qualify_them_for_night()
    {
        var test = new KnowledgeRecord(KnowledgeEvent.RecurrentTest, new DateOnly(2021, 3, 1));
        var operation = new DateOnly(2022, 6, 1);

        Assert.True(Value(Strict.RecencyCurrent(test, operation)));
        Assert.False(Value(Strict.MayOperate(operation, Night, test, 3.0, true)));
    }

    [Fact]
    public void A_flash_rate_nobody_asserted_is_sufficient_requires_interpretation()
    {
        var gap = Gap(Strict.MayOperate(InScope, Night, Trained2023, 3.0, flashRateSufficient: null));

        Assert.Equal(UnresolvedReason.RequiresInterpretation, gap.Reason);
        Assert.Equal(Corpus.Cite("§ 107.29(a)(2)"), gap.Locator);
    }

    [Fact]
    public void A_resolved_night_answer_cites_every_passage_it_applied_in_order()
    {
        Assert.Equal(
            [new SourceLocator(Corpus.DefinitionsId, "§ 1.1 Night"), Corpus.Cite("§ 107.29(a)(1)"), Corpus.Cite("§ 107.29(a)(2)")],
            Authorities(Strict.MayOperate(InScope, Night, Trained2023, 3.0, true)));
    }

    // ---------------------------------------------------------------- § 107.29(d) waivers

    private static readonly NightWaiver IssuedBeforeMarch16 = new(new DateOnly(2021, 3, 1), "the operator");
    private static readonly NightWaiver IssuedInBetween = new(new DateOnly(2021, 4, 1), "the operator");
    private static readonly NightWaiver IssuedAfterApril21 = new(new DateOnly(2021, 5, 1), "the operator");

    [Fact]
    public void A_waiver_still_usable_is_outside_scope_because_its_terms_are_not_published_text()
    {
        var gap = Gap(Strict.MayOperate(InScope, Night, Trained2023, null, null, IssuedAfterApril21));

        Assert.Equal(UnresolvedReason.OutsideCurrentScope, gap.Reason);
        Assert.Equal(Corpus.Cite("§ 107.205(b)"), gap.Locator);
    }

    [Fact]
    public void After_May_17_2021_an_older_waiver_is_no_answer_at_night_and_the_rule_applies()
    {
        var resolution = Strict.MayOperate(new DateOnly(2021, 5, 18), Night, Trained2023 with { Completed = new DateOnly(2021, 5, 1) }, 3.0, true, IssuedInBetween);

        Assert.True(Value(resolution));
        Assert.Contains(Corpus.Cite("§ 107.29(d)"), Authorities(resolution));
    }

    [Fact]
    public void On_May_17_2021_itself_an_older_waiver_still_reaches_night()
    {
        // "After May 17, 2021": the day itself is not after it.
        var gap = Gap(Strict.MayOperate(new DateOnly(2021, 5, 17), Night, Trained2023 with { Completed = new DateOnly(2021, 5, 1) }, 3.0, true, IssuedInBetween));

        Assert.Equal(UnresolvedReason.OutsideCurrentScope, gap.Reason);
    }

    [Fact]
    public void A_waiver_issued_between_March_16_and_April_21_still_reaches_civil_twilight()
    {
        // (d) bars it at night only, and does not terminate it.
        var gap = Gap(Strict.MayOperate(new DateOnly(2021, 6, 1), At(20, 15), Trained2023 with { Completed = new DateOnly(2021, 5, 1) }, null, null, IssuedInBetween));

        Assert.Equal(UnresolvedReason.OutsideCurrentScope, gap.Reason);
    }

    [Fact]
    public void A_terminated_waiver_reaches_nothing_after_May_17_2021()
    {
        var twilight = Strict.MayOperate(new DateOnly(2021, 5, 18), At(20, 15), Trained2023 with { Completed = new DateOnly(2021, 5, 1) }, null, null, IssuedBeforeMarch16);

        Assert.False(Value(twilight));
    }

    [Fact]
    public void Whether_a_waiver_that_terminates_on_May_17_is_in_force_that_day_requires_interpretation()
    {
        var gap = Gap(Strict.MayOperate(new DateOnly(2021, 5, 17), At(20, 15), Trained2023 with { Completed = new DateOnly(2021, 5, 1) }, null, null, IssuedBeforeMarch16));

        Assert.Equal(UnresolvedReason.RequiresInterpretation, gap.Reason);
        Assert.Equal(Corpus.Cite("§ 107.29(d)"), gap.Locator);
    }

    [Fact]
    public void Before_May_17_2021_a_terminating_waiver_is_still_in_force_and_outside_scope()
    {
        var gap = Gap(Strict.MayOperate(new DateOnly(2021, 5, 1), Night, Trained2023 with { Completed = new DateOnly(2021, 4, 30) }, null, null, IssuedBeforeMarch16));

        Assert.Equal(UnresolvedReason.OutsideCurrentScope, gap.Reason);
    }
}
