using System;
using RulesKernel.Identity;
using RulesKernel.Provenance;

namespace Part107Probe;

/// <summary>
/// The corpora this probe pins, and the ones it cites without pinning.
///
/// <para>
/// The regulation is real. Every quotation in this project is from 14 CFR Part 107 as the
/// eCFR versioner served it for 2026-01-01, the file pinned as <c>corpus/part107.xml</c> in
/// github.com/brandonifco/faa-part-107, and <see cref="RegulationHash"/> is the SHA-256 of
/// that file. Nothing here is advice, and the probe implements a handful of paragraphs,
/// not Part 107.
/// </para>
///
/// <para>
/// Two values are NOT from the pinned text and are marked where they are used:
/// <see cref="TextInForceSince"/>, and the interpretation corpus, which is this probe's own
/// record and whose hash is fabricated.
/// </para>
/// </summary>
public static class Corpus
{
    /// <summary>The regulation. The same id faa-part-107 uses, on purpose.</summary>
    public const string RegulationId = "cfr-14-107";

    /// <summary>SHA-256 of faa-part-107's corpus/part107.xml.</summary>
    public const string RegulationHash = "80f6bc4b002df9dcc60a651fec30a2dc3590081cc3e5fd431d9885c69b7ce35e";

    /// <summary>The derivation spelling faa-part-107's generated baseline uses for those bytes.</summary>
    public const string RegulationDerivation = "ecfr-versioner-xml";

    /// <summary>The date the pinned snapshot is of.</summary>
    public static readonly DateOnly RegulationAsOf = new(2026, 1, 1);

    /// <summary>
    /// The interpretations this engine has recorded for itself: an engine's own decision
    /// log, pinned like any other corpus because adopting one changes answers.
    /// FABRICATED hash; there is no such file.
    /// </summary>
    public const string InterpretationsId = "part107-probe-interpretations";

    private const string InterpretationsHash = "7a7a7a7a7a7a7a7a7a7a7a7a7a7a7a7a7a7a7a7a7a7a7a7a7a7a7a7a7a7a7a7a";

    /// <summary>
    /// 14 CFR Part 1, which defines "night". Cited, never pinned: this probe does not hold it.
    /// </summary>
    public const string DefinitionsId = "cfr-14-1";

    /// <summary>14 CFR Part 61, which § 107.65(c) depends on. Cited, never pinned.</summary>
    public const string PilotCertificationId = "cfr-14-61";

    /// <summary>
    /// The first date the probe treats the snapshot's text as in force. NOT VERIFIED: the
    /// pinned text does not state when Amendment 107-8 took effect. Its § 107.29(d) names
    /// both March 16 and April 21, 2021, and this value is taken from the later one. The
    /// point of the probe is that an engine needs this date and the snapshot does not
    /// supply it.
    /// </summary>
    public static readonly DateOnly TextInForceSince = new(2021, 4, 21);

    /// <summary>The regulation baseline, exactly as faa-part-107 builds it.</summary>
    public static SourceBaselineId Regulation { get; } =
        new(RegulationId, RegulationHash, RegulationDerivation, RegulationAsOf);

    /// <summary>The interpretation log, pinned on the day its one decision was recorded.</summary>
    public static SourceBaselineId Interpretations { get; } =
        new(InterpretationsId, InterpretationsHash, "markdown-bytes", new DateOnly(2026, 9, 15));

    /// <summary>A designation in the regulation, e.g. <c>§ 107.29(a)(1)</c>.</summary>
    public static SourceLocator Cite(string designation) => new(RegulationId, designation);
}
