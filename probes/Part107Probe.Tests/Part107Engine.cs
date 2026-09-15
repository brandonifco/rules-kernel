using System;
using System.Collections.Generic;
using System.Linq;
using RulesKernel.Identity;
using RulesKernel.Provenance;
using RulesKernel.Resolution;

namespace Part107Probe;

/// <summary>How the caller's knowledge record was earned, in § 107.65's own terms.</summary>
public enum KnowledgeEvent
{
    /// <summary>§ 107.65(a): passed an initial aeronautical knowledge test.</summary>
    InitialTest,

    /// <summary>§ 107.65(d): passed a recurrent test, which only counts before April 6, 2021.</summary>
    RecurrentTest,

    /// <summary>§ 107.65(b): completed recurrent training.</summary>
    RecurrentTraining,

    /// <summary>§ 107.65(c): a Part 61 certificate holder's training, which also needs a § 61.56 flight review.</summary>
    Part61Training,
}

/// <summary>The most recent knowledge event the caller states, and when it was completed.</summary>
public sealed record KnowledgeRecord(KnowledgeEvent Kind, DateOnly Completed);

/// <summary>
/// A waiver of § 107.29 the caller states is held. The engine never reads a certificate's
/// terms: they are issued to one operator and are not published text.
/// </summary>
public sealed record NightWaiver(DateOnly IssuedOn, string StatedBy);

/// <summary>
/// What the caller states about a flight's light conditions. Times are local and supplied;
/// the engine reads no clock and computes no ephemeris.
/// </summary>
/// <param name="EveningCivilTwilightEnds">
/// The end of evening civil twilight as the Air Almanac publishes it for the place and date,
/// when the caller has it. § 1.1 defines night by that value, and this probe does not pin
/// the almanac.
/// </param>
public sealed record LightConditions(
    TimeOnly LocalTime,
    TimeOnly OfficialSunrise,
    TimeOnly OfficialSunset,
    bool InAlaska = false,
    TimeOnly? MorningCivilTwilightBegins = null,
    TimeOnly? EveningCivilTwilightEnds = null);

/// <summary>
/// A resolved answer and every passage it rests on, in the order the engine applied them.
///
/// <para>
/// The kernel has no such type. <c>Resolution&lt;T&gt;.Resolved</c> carries a value and no
/// citation, while <c>Unresolved</c> must carry one. faa-part-107 hit the same asymmetry
/// and solved it with an <c>Authority</c> property on each finding. This probe needs more than
/// one authority for a single answer, because an adopted interpretation is cited alongside the
/// paragraph it interprets. See FINDINGS.md, finding 4.
/// </para>
/// </summary>
public sealed record Finding<T>(T Value, IReadOnlyList<SourceLocator> Authorities)
{
    public static Resolution<Finding<T>> Resolve(T value, params SourceLocator[] authorities) =>
        Resolution<Finding<T>>.FromValue(new Finding<T>(value, authorities));
}

/// <summary>
/// A deliberately partial engine over § 107.29 (operation at night) and § 107.65
/// (aeronautical knowledge recency). What it tests is the kernel; see FINDINGS.md.
/// </summary>
public sealed class Part107Engine
{
    /// <summary>§ 107.29(a)(1): training or a test must be completed "after April 6, 2021".</summary>
    public static readonly DateOnly NightTrainingAfter = new(2021, 4, 6);

    /// <summary>§ 107.65(d): a recurrent test counts if passed "prior to April 6, 2021".</summary>
    public static readonly DateOnly RecurrentTestBefore = new(2021, 4, 6);

    /// <summary>§ 107.29(d): "After May 17, 2021, no person may operate ... at night in accordance with a certificate of waiver issued prior to April 21, 2021".</summary>
    public static readonly DateOnly NightWaiverUseEndsAfter = new(2021, 5, 17);

    /// <summary>§ 107.29(d): waivers issued before this date cannot be used at night after May 17, 2021.</summary>
    public static readonly DateOnly NightWaiverIssuedBefore = new(2021, 4, 21);

    /// <summary>§ 107.29(d): waivers issued before this date "terminate on May 17, 2021".</summary>
    public static readonly DateOnly NightWaiverTerminatesIfIssuedBefore = new(2021, 3, 16);

    public Part107Engine(bool adoptsRecordedInterpretations)
    {
        AdoptsRecordedInterpretations = adoptsRecordedInterpretations;
        Identity = new ReplayCompatibilityIdentity(
            new RulesetVersion("part107-probe", 1),
            new ReplaySchemaVersion(1),
            adoptsRecordedInterpretations
                ? [Corpus.Regulation, Corpus.Interpretations]
                : [Corpus.Regulation]);
    }

    /// <summary>
    /// Whether the engine answers the questions its own recorded interpretations settle.
    /// Adopting them changes answers, so it changes <see cref="Identity"/> through a baseline,
    /// not through the ruleset version.
    /// </summary>
    public bool AdoptsRecordedInterpretations { get; }

    public ReplayCompatibilityIdentity Identity { get; }

    private static readonly SourceLocator CalendarMonthInterpretation =
        new(Corpus.InterpretationsId, "decision 0001: a calendar month in § 107.65 is counted whole, through its last day");

    /// <summary>
    /// § 107.65: is a record current on the operation date?
    ///
    /// <para>
    /// "within the previous 24 calendar months" reads two ways when the operation falls in
    /// the 24th month after the record's month: count the operation's own month as one of
    /// the 24, or do not. The engine answers wherever both readings agree, and at the
    /// boundary only if it has adopted its recorded interpretation.
    /// </para>
    /// </summary>
    public Resolution<Finding<bool>> RecencyCurrent(KnowledgeRecord record, DateOnly operationDate)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Completed > operationDate)
        {
            throw new ArgumentException("a knowledge record completed after the operation is a caller defect, not an answer", nameof(record));
        }

        if (OutsideTheSnapshot(operationDate, "§ 107.65") is { } outside)
        {
            return Resolution<Finding<bool>>.FromUnresolved(outside);
        }

        switch (record.Kind)
        {
            case KnowledgeEvent.RecurrentTest when record.Completed >= RecurrentTestBefore:
                // Not one of (a) through (c), and (d) only reaches tests passed before the date.
                return Finding<bool>.Resolve(false, Corpus.Cite("§ 107.65"), Corpus.Cite("§ 107.65(d)"));
            case KnowledgeEvent.Part61Training:
                // (c) also requires the § 61.56 flight review, a corpus this engine does not
                // pin. The locator names it anyway. See FINDINGS.md, finding 5.
                return Resolution<Finding<bool>>.FromUnresolved(new UnresolvedResult(
                    UnresolvedReason.OutsideCurrentScope,
                    "whether a Part 61 certificate holder meets the flight review requirement",
                    new SourceLocator(Corpus.PilotCertificationId, "§ 61.56")));
        }

        int months = MonthIndex(operationDate) - MonthIndex(record.Completed);
        SourceLocator paragraph = Corpus.Cite(record.Kind switch
        {
            KnowledgeEvent.InitialTest => "§ 107.65(a)",
            KnowledgeEvent.RecurrentTraining => "§ 107.65(b)",
            _ => "§ 107.65(d)",
        });

        if (months <= 23)
        {
            return Finding<bool>.Resolve(true, paragraph);
        }

        if (months >= 25)
        {
            return Finding<bool>.Resolve(false, paragraph);
        }

        return AdoptsRecordedInterpretations
            ? Finding<bool>.Resolve(true, paragraph, CalendarMonthInterpretation)
            : Resolution<Finding<bool>>.FromUnresolved(new UnresolvedResult(
                UnresolvedReason.RequiresInterpretation,
                "whether the operation's own month counts among the previous 24 calendar months",
                Corpus.Cite("§ 107.65")));
    }

    /// <summary>
    /// § 107.29: may this flight be operated in these light conditions?
    /// </summary>
    /// <param name="training">The caller's most recent knowledge record; § 107.29(a)(1) reads its date.</param>
    /// <param name="lightingVisibleStatuteMiles">How far the lighted anti-collision lighting is visible, or null for none.</param>
    /// <param name="flashRateSufficient">
    /// Whether the flash rate is "sufficient to avoid a collision". The corpus gives no figure,
    /// so this is the caller's assertion; null means none was made.
    /// </param>
    public Resolution<Finding<bool>> MayOperate(
        DateOnly operationDate,
        LightConditions light,
        KnowledgeRecord training,
        double? lightingVisibleStatuteMiles,
        bool? flashRateSufficient,
        NightWaiver? waiver = null)
    {
        ArgumentNullException.ThrowIfNull(light);
        ArgumentNullException.ThrowIfNull(training);
        if (training.Completed > operationDate)
        {
            throw new ArgumentException("a knowledge record completed after the operation is a caller defect, not an answer", nameof(training));
        }

        if (OutsideTheSnapshot(operationDate, "§ 107.29") is { } outside)
        {
            return Resolution<Finding<bool>>.FromUnresolved(outside);
        }

        LightPeriod kind;
        SourceLocator periodAuthority;
        switch (Period(light))
        {
            case Resolution<(LightPeriod, SourceLocator)>.Resolved((var resolvedPeriod, var authority)):
                (kind, periodAuthority) = (resolvedPeriod, authority);
                break;
            case Resolution<(LightPeriod, SourceLocator)>.Unresolved(var gap):
                return Resolution<Finding<bool>>.FromUnresolved(gap);
            default:
                throw new InvalidOperationException("unreachable: Resolution<T> is a closed hierarchy");
        }

        if (kind == LightPeriod.Day)
        {
            // Nothing in § 107.29 reaches a daytime flight; the section is cited as the rule applied.
            return Finding<bool>.Resolve(true, Corpus.Cite("§ 107.29"), periodAuthority);
        }

        var authorities = new List<SourceLocator> { periodAuthority };

        if (waiver is not null)
        {
            if (waiver.IssuedOn > operationDate)
            {
                throw new ArgumentException("a waiver issued after the operation is a caller defect", nameof(waiver));
            }

            if (waiver.IssuedOn < NightWaiverTerminatesIfIssuedBefore && operationDate == NightWaiverUseEndsAfter)
            {
                // One paragraph, two phrasings: use at night ends "After May 17, 2021", and the
                // older waivers "terminate on May 17, 2021". Whether a waiver that terminates on
                // a day is in force during that day is not stated.
                return Resolution<Finding<bool>>.FromUnresolved(new UnresolvedResult(
                    UnresolvedReason.RequiresInterpretation,
                    "whether a certificate of waiver that terminates on May 17, 2021 is in force on that day",
                    Corpus.Cite("§ 107.29(d)")));
            }

            bool unusable = waiver.IssuedOn < NightWaiverTerminatesIfIssuedBefore
                ? operationDate > NightWaiverUseEndsAfter
                : kind == LightPeriod.Night
                  && waiver.IssuedOn < NightWaiverIssuedBefore
                  && operationDate > NightWaiverUseEndsAfter;

            if (!unusable)
            {
                // A waiver may authorize deviation only from § 107.29(a)(2) and (b)
                // (§ 107.205(b)), and what this one authorizes is in its terms, which the
                // engine does not read.
                return Resolution<Finding<bool>>.FromUnresolved(new UnresolvedResult(
                    UnresolvedReason.OutsideCurrentScope,
                    $"operation under a certificate of waiver of § 107.29 issued {waiver.IssuedOn:yyyy-MM-dd}, as stated by {waiver.StatedBy}",
                    Corpus.Cite("§ 107.205(b)")));
            }

            authorities.Add(Corpus.Cite("§ 107.29(d)"));
        }

        if (kind == LightPeriod.Night)
        {
            bool trained = Qualifies(training);
            authorities.Add(Corpus.Cite("§ 107.29(a)(1)"));
            if (!trained)
            {
                return Finding<bool>.Resolve(false, [.. authorities]);
            }
        }

        SourceLocator lighting = Corpus.Cite(kind == LightPeriod.Night ? "§ 107.29(a)(2)" : "§ 107.29(b)");
        authorities.Add(lighting);

        if (lightingVisibleStatuteMiles is not { } miles || miles < 3.0)
        {
            return Finding<bool>.Resolve(false, [.. authorities]);
        }

        if (flashRateSufficient is not { } sufficient)
        {
            return Resolution<Finding<bool>>.FromUnresolved(new UnresolvedResult(
                UnresolvedReason.RequiresInterpretation,
                "whether the anti-collision lighting's flash rate is sufficient to avoid a collision",
                lighting));
        }

        return Finding<bool>.Resolve(sufficient, [.. authorities]);

        static bool Qualifies(KnowledgeRecord r) =>
            r.Kind is KnowledgeEvent.InitialTest or KnowledgeEvent.RecurrentTraining or KnowledgeEvent.Part61Training
            && r.Completed > NightTrainingAfter;
    }

    /// <summary>The periods § 107.29 distinguishes.</summary>
    public enum LightPeriod
    {
        Day,
        CivilTwilight,
        Night,
    }

    /// <summary>
    /// Which of § 107.29's periods a time falls in.
    ///
    /// <para>
    /// Two corpora define the boundary, and they disagree. § 107.29(c) defines civil twilight
    /// "for purposes of paragraph (b)" as the 30 minutes either side of official sunrise and
    /// sunset. Night, in paragraph (a), is defined in 14 CFR 1.1 by the Air Almanac's civil
    /// twilight, which is not 30 minutes anywhere in particular. A time outside the 30-minute
    /// window but before the almanac's end of twilight is in neither period.
    /// </para>
    /// </summary>
    public static Resolution<(LightPeriod Period, SourceLocator Authority)> Period(LightConditions light)
    {
        ArgumentNullException.ThrowIfNull(light);
        var nightDefinition = new SourceLocator(Corpus.DefinitionsId, "§ 1.1 Night");

        if (light.InAlaska)
        {
            return Resolution<(LightPeriod, SourceLocator)>.FromUnresolved(new UnresolvedResult(
                UnresolvedReason.MissingRulesData,
                "civil twilight in Alaska, which § 107.29(c)(3) takes from the Air Almanac",
                Corpus.Cite("§ 107.29(c)(3)")));
        }

        TimeOnly t = light.LocalTime;
        bool beforeDawnWindow = t < light.OfficialSunrise.AddMinutes(-30);
        bool afterDuskWindow = t > light.OfficialSunset.AddMinutes(30);

        if (t >= light.OfficialSunrise && t <= light.OfficialSunset)
        {
            return Resolution<(LightPeriod, SourceLocator)>.FromValue((LightPeriod.Day, Corpus.Cite("§ 107.29(c)")));
        }

        if (!beforeDawnWindow && !afterDuskWindow)
        {
            return Resolution<(LightPeriod, SourceLocator)>.FromValue(
                (LightPeriod.CivilTwilight, Corpus.Cite(t < light.OfficialSunrise ? "§ 107.29(c)(1)" : "§ 107.29(c)(2)")));
        }

        TimeOnly? almanacBoundary = afterDuskWindow ? light.EveningCivilTwilightEnds : light.MorningCivilTwilightBegins;
        if (almanacBoundary is not { } boundary)
        {
            return Resolution<(LightPeriod, SourceLocator)>.FromUnresolved(new UnresolvedResult(
                UnresolvedReason.MissingRulesData,
                "whether a time outside § 107.29(c)'s 30-minute window is night, which § 1.1 decides by the Air Almanac",
                nightDefinition));
        }

        bool night = afterDuskWindow ? t > boundary : t < boundary;
        return night
            ? Resolution<(LightPeriod, SourceLocator)>.FromValue((LightPeriod.Night, nightDefinition))
            : Resolution<(LightPeriod, SourceLocator)>.FromUnresolved(new UnresolvedResult(
                UnresolvedReason.UnsupportedInteraction,
                "a time that is neither § 107.29(c)'s civil twilight nor § 1.1's night",
                Corpus.Cite("§ 107.29(c)")));
    }

    private static UnresolvedResult? OutsideTheSnapshot(DateOnly operationDate, string section)
    {
        // The snapshot says what the text read on its as-of date. It does not say since when,
        // or until when. See FINDINGS.md, finding 2.
        if (operationDate < Corpus.TextInForceSince || operationDate > Corpus.RegulationAsOf)
        {
            return new UnresolvedResult(
                UnresolvedReason.OutsideCurrentScope,
                $"{section} for an operation on {operationDate:yyyy-MM-dd}, outside the text pinned as of {Corpus.RegulationAsOf:yyyy-MM-dd}",
                Corpus.Cite(section));
        }

        return null;
    }

    private static int MonthIndex(DateOnly date) => (date.Year * 12) + date.Month - 1;
}
